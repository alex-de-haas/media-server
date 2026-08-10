import Foundation
import HTTPTypes
import OpenAPIRuntime
import Testing

@testable import MediaKit

/// A server made of canned answers, keyed by the path the client asks for.
///
/// Every failure in this chain is a state a viewer sees, and none of them can be produced by talking to
/// a real Core on demand — an expired code, a denied approval, a host too old. So the transport is the
/// seam.
private final class StubTransport: HTTPTransport, ClientTransport, @unchecked Sendable {
    typealias Answer = (status: Int, body: String)

    private let lock = NSLock()
    private var answers: [String: [Answer]]
    private(set) var requests: [URLRequest] = []

    init(_ answers: [String: [Answer]]) {
        self.answers = answers
    }

    convenience init(single answers: [String: Answer]) {
        self.init(answers.mapValues { [$0] })
    }

    func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
        let answer: Answer = lock.withLock {
            requests.append(request)
            let path = request.url?.path ?? ""
            guard var queued = answers[path], !queued.isEmpty else {
                return (404, "")
            }

            // The last answer repeats, so a poll can be told "pending, pending, then approved" without
            // having to know how many times it will ask.
            let next = queued.count == 1 ? queued[0] : queued.removeFirst()
            answers[path] = queued
            return next
        }

        return (
            Data(answer.body.utf8),
            HTTPURLResponse(url: request.url!, statusCode: answer.status, httpVersion: nil, headerFields: nil)!
        )
    }

    /// The same table, for the generated client. Our own surface goes through it, Core's API through
    /// `HTTPTransport`, and one set of canned answers drives both.
    func send(
        _ request: HTTPRequest,
        body: HTTPBody?,
        baseURL: URL,
        operationID: String
    ) async throws -> (HTTPResponse, HTTPBody?) {
        let path = request.path ?? ""
        let answer: Answer = lock.withLock {
            guard var queued = answers[path], !queued.isEmpty else { return (404, "") }
            let next = queued.count == 1 ? queued[0] : queued.removeFirst()
            answers[path] = queued
            return next
        }

        var response = HTTPResponse(status: .init(code: answer.status))
        response.headerFields[.contentType] = "application/json"
        return (response, HTTPBody(answer.body))
    }

    func body(forPath path: String) -> [String: Any]? {
        lock.withLock {
            guard let request = requests.last(where: { $0.url?.path == path }),
                  let data = request.httpBody
            else { return nil }

            return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        }
    }

    func header(_ name: String, forPath path: String) -> String? {
        lock.withLock {
            requests.last { $0.url?.path == path }?.value(forHTTPHeaderField: name)
        }
    }
}

// The committed OpenAPI, verbatim: surfaceVersion is a **string** and coreOrigin is nullable. Writing
// this to match the model instead of the contract is exactly how a client that never worked against a
// real server passed its own tests.
private let bootstrapBody = """
{"serverName":"Home","appId":"com.haas.media-server","surfaceVersion":"1","coreOrigin":"https://core.example"}
"""

private let grantBody = """
{"deviceCode":"dev-1","userCode":"ABCD-1234","verificationUri":"https://core.example/settings?tab=tokens",\
"intervalSeconds":1,"expiresInSeconds":300}
"""

private let tokenBody = """
{"accessToken":"app-token","tokenType":"Bearer","expiresAt":"2030-01-01T00:00:00.000Z","expiresInSeconds":604800}
"""

private func happyServer(polls: [StubTransport.Answer]) -> StubTransport {
    StubTransport([
        "/native/v1/server/public": [(200, bootstrapBody)],
        "/api/auth/device/code": [(200, grantBody)],
        "/api/auth/device/token": polls,
        "/api/auth/apps/authorize": [(200, #"{"code":"auth-code","redirectUri":"x","expiresAt":"2030-01-01T00:00:00Z"}"#)],
        "/api/auth/apps/token": [(200, tokenBody)],
    ])
}

@Suite("Address normalisation")
struct AddressTests {
    @Test("A bare host becomes https, because a television is not the place to type a scheme")
    func bareHost() {
        #expect(PairingSession.normalise("media.example.com")?.absoluteString == "https://media.example.com")
    }

    @Test("An explicit scheme is kept, including http for a server on the local network")
    func explicitScheme() {
        #expect(PairingSession.normalise("http://192.168.1.10:8080")?.absoluteString
            == "http://192.168.1.10:8080")
    }

    @Test("A path, a query or a fragment is dropped rather than carried in front of every route")
    func pathDropped() {
        #expect(PairingSession.normalise("https://media.example.com/some/page?x=1#y")?.absoluteString
            == "https://media.example.com")
        #expect(PairingSession.normalise("media.example.com/")?.absoluteString
            == "https://media.example.com")
    }

    @Test("Nonsense is refused rather than turned into a request somewhere else")
    func refused() {
        #expect(PairingSession.normalise("") == nil)
        #expect(PairingSession.normalise("   ") == nil)
        #expect(PairingSession.normalise("ftp://media.example.com") == nil)
        #expect(PairingSession.normalise("https://") == nil)
    }
}

@Suite("The pairing chain")
struct PairingClientTests {
    private let core = URL(string: "https://core.example")!
    private let server = URL(string: "https://media.example")!

    @Test("An address that is not a Media Server says so rather than failing obscurely")
    func notAMediaServer() async {
        let stub = StubTransport(single: ["/other": (200, "{}")])
        let client = PairingClient(transport: stub, surface: stub)

        await #expect(throws: PairingError.notAMediaServer) {
            try await client.bootstrap(server: server)
        }
    }

    @Test("The bootstrap decodes the contract's own shape")
    func bootstrapShape() async throws {
        let stub = StubTransport(single: ["/native/v1/server/public": (200, bootstrapBody)])
        let bootstrap = try await PairingClient(transport: stub, surface: stub).bootstrap(server: server)

        // A string, because `NativeSurface.Version` is `"1"`. Decoding it as a number failed against
        // every real server while passing a fixture written to match the model.
        #expect(bootstrap.surfaceVersion == "1")
        #expect(bootstrap.coreOrigin == "https://core.example")
    }

    @Test("A server that cannot say where its Core is decodes, and fails later with its own reason")
    func nullCoreOrigin() async throws {
        let body = #"{"serverName":"Home","appId":"x","surfaceVersion":"1","coreOrigin":null}"#
        let stub = StubTransport(single: ["/native/v1/server/public": (200, body)])

        // Nullable in the contract, so it must not read as a malformed answer.
        #expect(try await PairingClient(transport: stub, surface: stub)
            .bootstrap(server: server).coreOrigin == nil)
    }

    @Test("Something answering with the wrong shape is not a Media Server either")
    func wrongShape() async {
        let stub = StubTransport(single: ["/native/v1/server/public": (200, #"{"hello":"world"}"#)])
        let client = PairingClient(transport: stub, surface: stub)

        await #expect(throws: PairingError.notAMediaServer) {
            try await client.bootstrap(server: server)
        }
    }

    @Test("A Core without the device routes is named, not left as a bare 404")
    func coreTooOld() async {
        // The device routes arrived in Core 0.73.0. Against an older host a sign-in appears to work and
        // then bounces with nothing to explain why, which is the failure this prevents.
        let client = PairingClient(transport: StubTransport(single: ["/nothing": (200, "{}")]))

        await #expect(throws: PairingError.coreTooOld) {
            try await client.requestDeviceCode(core: core, label: "Apple TV")
        }
    }

    @Test("Too many pending requests is a wait, not a failure")
    func throttled() async {
        let client = PairingClient(transport: StubTransport(single: [
            "/api/auth/device/code": (429, #"{"error":"device_code_throttled","message":"Too many."}"#),
        ]))

        await #expect(throws: PairingError.throttled) {
            try await client.requestDeviceCode(core: core, label: "Apple TV")
        }
    }

    @Test("The label travels, because the approving human decides by it")
    func labelTravels() async throws {
        let transport = StubTransport(single: ["/api/auth/device/code": (200, grantBody)])
        _ = try await PairingClient(transport: transport)
            .requestDeviceCode(core: core, label: "Living Room")

        #expect(transport.body(forPath: "/api/auth/device/code")?["label"] as? String == "Living Room")
    }

    @Test("Every poll answer maps to its own outcome")
    func pollOutcomes() async throws {
        for (body, expected) in [
            (#"{"status":"pending","token":null}"#, DevicePollOutcome.pending),
            (#"{"status":"approved","token":"core-token"}"#, .approved(token: "core-token")),
            (#"{"status":"denied","token":null}"#, .denied),
            (#"{"status":"expired","token":null}"#, .expired),
        ] {
            let client = PairingClient(transport: StubTransport(single: ["/api/auth/device/token": (200, body)]))
            #expect(try await client.poll(core: core, deviceCode: "dev-1") == expected)
        }
    }

    @Test("Approved with no token is treated as expired rather than looped on")
    func approvedWithoutToken() async {
        // A Core bug, but a client that trusted it would poll for ever on a request already consumed.
        let client = PairingClient(transport: StubTransport(single: [
            "/api/auth/device/token": (200, #"{"status":"approved","token":null}"#),
        ]))

        await #expect(throws: PairingError.codeExpired) {
            try await client.poll(core: core, deviceCode: "dev-1")
        }
    }

    @Test("The exchange presents the Core token as a bearer, which is what makes it browserless")
    func exchangeUsesBearer() async throws {
        let transport = happyServer(polls: [(200, #"{"status":"approved","token":"core-token"}"#)])

        _ = try await PairingClient(transport: transport).exchange(
            core: core, appId: "com.haas.media-server", redirectUri: server, coreToken: "core-token")

        // A bearer-presented Core session is deliberately CSRF-exempt; a cookie would need a header this
        // client has no way to obtain.
        #expect(transport.header("Authorization", forPath: "/api/auth/apps/authorize") == "Bearer core-token")
    }

    @Test("The redirect is the server's own origin, which is the only value Core will accept")
    func redirectIsTheServer() async throws {
        // Core checks it against the app's installed endpoint origins even though nothing navigates —
        // the authorization code comes back in the body.
        let transport = happyServer(polls: [(200, #"{"status":"approved","token":"t"}"#)])

        _ = try await PairingClient(transport: transport).exchange(
            core: core, appId: "com.haas.media-server", redirectUri: server, coreToken: "t")

        #expect(transport.body(forPath: "/api/auth/apps/authorize")?["redirectUri"] as? String
            == "https://media.example")
    }

    @Test("A user not assigned to the app is a permission answer, not a network one")
    func notAssigned() async {
        let transport = StubTransport(single: [
            "/api/auth/apps/authorize": (403, #"{"error":"user_not_assigned","message":"No."}"#),
        ])

        await #expect(throws: PairingError.notAssigned) {
            try await PairingClient(transport: transport).exchange(
                core: core, appId: "x", redirectUri: server, coreToken: "t")
        }
    }
}

@Suite("The pairing session")
@MainActor
struct PairingSessionTests {
    private func session(
        _ transport: StubTransport,
        store: any CredentialStore = InMemoryCredentialStore()
    ) -> PairingSession {
        // No real waiting: the poll interval is Core's to state and this test's to skip.
        PairingSession(
            client: PairingClient(transport: transport, surface: transport),
            store: store,
            label: "Test",
            sleep: { _ in })
    }

    private func settle(_ subject: PairingSession) async {
        for _ in 0..<200 {
            if case .checking = subject.state {} else if case .awaitingApproval = subject.state {} else { return }
            await Task.yield()
        }
    }

    @Test("A code goes on screen before anyone is asked to wait for it")
    func showsTheCode() async {
        let subject = session(happyServer(polls: [(200, #"{"status":"pending","token":null}"#)]))
        subject.start(address: "media.example")

        for _ in 0..<200 {
            if case .awaitingApproval(let grant, let name) = subject.state {
                #expect(grant.userCode == "ABCD-1234")
                #expect(name == "Home")
                return
            }

            await Task.yield()
        }

        Issue.record("The code never reached the screen: \(subject.state)")
    }

    @Test("Pending is waited through, and the approval that follows is stored")
    func pairsAndStores() async {
        let store = InMemoryCredentialStore()
        let subject = session(
            happyServer(polls: [
                (200, #"{"status":"pending","token":null}"#),
                (200, #"{"status":"pending","token":null}"#),
                (200, #"{"status":"approved","token":"core-token"}"#),
            ]),
            store: store)

        subject.start(address: "media.example")
        await settle(subject)

        guard case .paired(let paired) = subject.state else {
            Issue.record("Expected a pairing, got \(subject.state)")
            return
        }

        #expect(paired.serverName == "Home")
        #expect(paired.coreToken == "core-token")
        #expect(paired.identity.accessToken == "app-token")
        #expect(store.load() == paired)
    }

    @Test("A denied approval is said out loud and nothing is stored")
    func denied() async {
        let store = InMemoryCredentialStore()
        let subject = session(
            happyServer(polls: [(200, #"{"status":"denied","token":null}"#)]), store: store)

        subject.start(address: "media.example")
        await settle(subject)

        #expect(subject.state == .failed(.denied))
        #expect(store.load() == nil)
    }

    @Test("An expired code is a different answer from a denied one")
    func expired() async {
        let subject = session(happyServer(polls: [(200, #"{"status":"expired","token":null}"#)]))

        subject.start(address: "media.example")
        await settle(subject)

        #expect(subject.state == .failed(.codeExpired))
    }

    @Test("Cancelling stops the poll rather than leaving it asking in the background")
    func cancelling() async {
        // A poll that outlives its screen is a device quietly asking to be signed in while nobody is
        // looking at it.
        let subject = session(happyServer(polls: [(200, #"{"status":"pending","token":null}"#)]))
        subject.start(address: "media.example")
        await Task.yield()

        subject.cancel()

        #expect(subject.state == .idle)
    }

    @Test("A stored pairing comes back without asking anyone")
    func restores() async {
        let paired = PairedServer(
            server: URL(string: "https://media.example")!,
            serverName: "Home",
            appId: "com.haas.media-server",
            coreOrigin: URL(string: "https://core.example")!,
            coreToken: "core-token",
            identity: AppIdentity(accessToken: "app-token", expiresAt: .distantFuture))

        let subject = session(happyServer(polls: []), store: InMemoryCredentialStore(paired))
        await subject.restore()

        #expect(subject.state == .paired(paired))
    }

    @Test("A lapsed app grant is re-minted silently, not turned into a pairing screen")
    func refreshesSilently() async {
        // The two lifetimes are the point: the app grant is days, Core's own token is months. A viewer
        // should see the pairing screen when the second has gone, not every time the first does.
        let stale = PairedServer(
            server: URL(string: "https://media.example")!,
            serverName: "Home",
            appId: "com.haas.media-server",
            coreOrigin: URL(string: "https://core.example")!,
            coreToken: "core-token",
            identity: AppIdentity(accessToken: "old", expiresAt: .distantPast))

        let store = InMemoryCredentialStore(stale)
        let subject = session(happyServer(polls: []), store: store)
        await subject.restore()

        guard case .paired(let paired) = subject.state else {
            Issue.record("Expected a silent refresh, got \(subject.state)")
            return
        }

        #expect(paired.identity.accessToken == "app-token")
        #expect(store.load()?.identity.accessToken == "app-token")
    }

    private func stalePairing(coreToken: String = "core-token") -> PairedServer {
        PairedServer(
            server: URL(string: "https://media.example")!,
            serverName: "Home",
            appId: "com.haas.media-server",
            coreOrigin: URL(string: "https://core.example")!,
            coreToken: coreToken,
            identity: AppIdentity(accessToken: "old", expiresAt: .distantPast))
    }

    @Test("A refused credential is the one reason to forget a pairing")
    func refusedCredentialUnpairs() async {
        let store = InMemoryCredentialStore(stalePairing(coreToken: "revoked"))
        let subject = session(
            StubTransport(single: [
                "/api/auth/apps/authorize": (401, #"{"error":"session_invalid","message":"Gone."}"#),
            ]),
            store: store)

        await subject.restore()

        #expect(store.load() == nil)
        #expect(subject.state == .failed(.credentialRejected))
    }

    @Test("A server that was merely asleep does not cost the viewer their pairing")
    func transientFailureKeepsPairing() async {
        // A television that woke up before the network did must not have to be paired again over it.
        let store = InMemoryCredentialStore(stalePairing())
        let subject = session(
            StubTransport(single: ["/api/auth/apps/authorize": (503, "")]), store: store)

        await subject.restore()

        #expect(store.load() != nil)
        if case .paired = subject.state {} else {
            Issue.record("Expected to stay paired, got \(subject.state)")
        }
    }

    @Test("A refresh asks the Core it was approved against, never one named just now")
    func refreshPinsTheCore() async {
        // The token about to be presented is the full-privilege one. An endpoint that could name its own
        // origin could be handed a credential reaching Core and every other app on the host.
        let transport = StubTransport([
            "/native/v1/server/public": [(200, #"{"serverName":"Evil","appId":"x","surfaceVersion":"1","coreOrigin":"https://attacker.example"}"#)],
            "/api/auth/apps/authorize": [(200, #"{"code":"c","redirectUri":"x","expiresAt":"2030-01-01T00:00:00Z"}"#)],
            "/api/auth/apps/token": [(200, tokenBody)],
        ])

        let subject = session(transport, store: InMemoryCredentialStore(stalePairing()))
        await subject.restore()

        let hosts = Set(transport.requests.compactMap(\.url?.host))
        #expect(!hosts.contains("attacker.example"))
        #expect(hosts.contains("core.example"))
    }

    @Test("Cancelling after a successful pairing leaves it alone")
    func cancelDoesNotUndoPairing() async {
        // The code screen disappears *because* pairing succeeded, and its onDisappear arrives after the
        // state has moved on. Treating that as a cancellation drops the viewer back on address entry the
        // instant they finish.
        let subject = session(
            happyServer(polls: [(200, #"{"status":"approved","token":"core-token"}"#)]))

        subject.start(address: "media.example")
        await settle(subject)

        subject.cancel()

        if case .paired = subject.state {} else {
            Issue.record("Expected to stay paired, got \(subject.state)")
        }
    }

    @Test("A server with no Core origin says so rather than reading as malformed")
    func noCoreOrigin() async {
        let subject = session(StubTransport(single: [
            "/native/v1/server/public": (200, #"{"serverName":"Home","appId":"x","surfaceVersion":"1","coreOrigin":null}"#),
        ]))

        subject.start(address: "media.example")
        await settle(subject)

        #expect(subject.state == .failed(.noCoreOrigin))
    }

    @Test("An address that is not one fails before anything is asked of the network")
    func badAddress() async {
        let transport = happyServer(polls: [])
        let subject = session(transport)

        subject.start(address: "not a host")

        #expect(subject.state == .failed(.notAMediaServer))
        #expect(transport.requests.isEmpty)
    }
}
