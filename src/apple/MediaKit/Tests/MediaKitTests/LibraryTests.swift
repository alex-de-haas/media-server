import Foundation
import HTTPTypes
import MediaServerAPI
import OpenAPIRuntime
import Testing

@testable import MediaKit

/// A server made of canned answers per path, which records what it was asked.
private final class SurfaceStub: ClientTransport, @unchecked Sendable {
    typealias Answer = (status: Int, body: String)

    private let lock = NSLock()
    private var answers: [String: [Answer]]
    private(set) var requests: [(path: String, authorization: String?)] = []

    init(_ answers: [String: [Answer]]) {
        self.answers = answers
    }

    func send(
        _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
    ) async throws -> (HTTPResponse, HTTPBody?) {
        let path = request.path?.split(separator: "?").first.map(String.init) ?? ""
        let answer: Answer = lock.withLock {
            requests.append((path, request.headerFields[.authorization]))
            guard var queued = answers[path], !queued.isEmpty else { return (404, "") }
            // The last answer repeats, so "401 then 200 for ever" needs only two entries.
            let next = queued.count == 1 ? queued[0] : queued.removeFirst()
            answers[path] = queued
            return next
        }

        var response = HTTPResponse(status: .init(code: answer.status))
        response.headerFields[.contentType] = "application/json"
        return (response, HTTPBody(answer.body))
    }

    var tokensSeen: [String] {
        lock.withLock { requests.compactMap(\.authorization) }
    }
}

private func pairing(token: String = "old-token") -> PairedServer {
    PairedServer(
        server: URL(string: "https://media.example")!,
        serverName: "Home",
        appId: "com.haas.media-server",
        coreOrigin: URL(string: "https://core.example")!,
        coreToken: "core-token",
        identity: AppIdentity(accessToken: token, expiresAt: .distantFuture))
}

private let freshIdentity = """
{"accessToken":"fresh-token","tokenType":"Bearer","expiresAt":"2030-01-01T00:00:00.000Z",\
"expiresInSeconds":604800}
"""

private func page(_ items: String, cursor: String, hasMore: Bool) -> String {
    """
    {"items":[\(items)],"removedIds":[],"changedPreferenceScopes":[],\
    "cursor":"\(cursor)","hasMore":\(hasMore),"resetRequired":false}
    """
}

private func title(_ id: String, _ kind: String, _ name: String, played: Bool = false, ticks: Int = 0) -> String {
    """
    {"id":"\(id)","publicId":"p\(id)","catalogId":"cat","kind":"\(kind)","title":"\(name)",\
    "year":2001,"posterUrl":"https://tmdb/x.jpg",\
    "userData":{"key":"k","playbackPositionTicks":\(ticks),"playCount":0,"isFavorite":false,"played":\(played)}}
    """
}

@Suite("The credential that keeps itself alive")
@MainActor
struct ServerSessionTests {
    /// Core's half of the exchange, which the refresh runs when the surface says 401.
    private func core() -> StubTransportForCore {
        StubTransportForCore()
    }

    final class StubTransportForCore: HTTPTransport, @unchecked Sendable {
        private let lock = NSLock()
        private(set) var calls = 0

        func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
            let path = request.url?.path ?? ""
            lock.withLock { if path.contains("apps/token") { calls += 1 } }

            let body = path.contains("apps/authorize")
                ? #"{"code":"c","redirectUri":"x","expiresAt":"2030-01-01T00:00:00Z"}"#
                : freshIdentity

            return (
                Data(body.utf8),
                HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!
            )
        }
    }

    @Test("A 401 re-mints the grant and retries, and the caller never learns it happened")
    func retriesOnce() async throws {
        // The grant lapses after seven days idle while its stated expiry is thirty days out, so a clock
        // cannot tell it has gone. Only a request can.
        let surface = SurfaceStub(["/native/v1/sync": [(401, ""), (200, page("", cursor: "c", hasMore: false))]])
        let session = ServerSession(
            paired: pairing(), store: InMemoryCredentialStore(),
            pairing: PairingClient(transport: core(), surface: surface), transport: surface)

        _ = try await session.api().getNativeV1Sync(query: .init(cursor: nil)).ok

        #expect(surface.tokensSeen == ["Bearer old-token", "Bearer fresh-token"])
    }

    @Test("The re-minted credential is stored, so the next launch does not repeat the exchange")
    func storesTheRefresh() async throws {
        let store = InMemoryCredentialStore(pairing())
        let surface = SurfaceStub(["/native/v1/sync": [(401, ""), (200, page("", cursor: "c", hasMore: false))]])
        let session = ServerSession(
            paired: pairing(), store: store,
            pairing: PairingClient(transport: core(), surface: surface), transport: surface)

        _ = try await session.api().getNativeV1Sync(query: .init(cursor: nil)).ok

        #expect(store.load()?.identity.accessToken == "fresh-token")
    }

    @Test("Several requests failing together produce one exchange, not one each")
    func refreshesOnce() async throws {
        let surface = SurfaceStub(["/native/v1/sync": [(401, ""), (200, page("", cursor: "c", hasMore: false))]])
        let coreStub = core()
        let session = ServerSession(
            paired: pairing(), store: InMemoryCredentialStore(),
            pairing: PairingClient(transport: coreStub, surface: surface), transport: surface)
        let client = session.api()

        // The first answer is consumed by whichever arrives first; the rest see the repeating 200. What
        // matters is that a burst of failures does not become a burst of exchanges.
        async let first = client.getNativeV1Sync(query: .init(cursor: nil))
        async let second = client.getNativeV1Sync(query: .init(cursor: nil))
        _ = try await (first, second)

        #expect(coreStub.calls <= 1)
    }

    @Test("A refusal Core will not fix forgets the pairing rather than failing again tomorrow")
    func terminalRefusalUnpairs() async throws {
        // The stored grant's absolute expiry can still be weeks away, so a device that swallowed this
        // would restore itself as paired on the next launch and fail in exactly the same way.
        struct RevokedCore: HTTPTransport {
            func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
                (
                    Data(#"{"error":"user_not_assigned","message":"No."}"#.utf8),
                    HTTPURLResponse(url: request.url!, statusCode: 403, httpVersion: nil, headerFields: nil)!
                )
            }
        }

        let store = InMemoryCredentialStore(pairing())
        let surface = SurfaceStub(["/native/v1/sync": [(401, "")]])
        let session = ServerSession(
            paired: pairing(), store: store,
            pairing: PairingClient(transport: RevokedCore(), surface: surface), transport: surface)

        _ = try? await session.api().getNativeV1Sync(query: .init(cursor: nil))

        #expect(session.credentialLost)
        #expect(store.load() == nil)
    }

    @Test("A server having a bad day keeps the pairing")
    func transientRefusalKeepsPairing() async throws {
        struct SickCore: HTTPTransport {
            func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
                (Data(), HTTPURLResponse(url: request.url!, statusCode: 503, httpVersion: nil, headerFields: nil)!)
            }
        }

        let store = InMemoryCredentialStore(pairing())
        let surface = SurfaceStub(["/native/v1/sync": [(401, "")]])
        let session = ServerSession(
            paired: pairing(), store: store,
            pairing: PairingClient(transport: SickCore(), surface: surface), transport: surface)

        _ = try? await session.api().getNativeV1Sync(query: .init(cursor: nil))

        #expect(!session.credentialLost)
        #expect(store.load() != nil)
    }

    @Test("A refusal Core will not fix is passed through rather than retried for ever")
    func givesUp() async throws {
        struct DeadCore: HTTPTransport {
            func send(_ request: URLRequest) async throws -> (Data, HTTPURLResponse) {
                (
                    Data(#"{"error":"session_invalid","message":"Gone."}"#.utf8),
                    HTTPURLResponse(url: request.url!, statusCode: 401, httpVersion: nil, headerFields: nil)!
                )
            }
        }

        let surface = SurfaceStub(["/native/v1/sync": [(401, "")]])
        let session = ServerSession(
            paired: pairing(), store: InMemoryCredentialStore(),
            pairing: PairingClient(transport: DeadCore(), surface: surface), transport: surface)

        // Two attempts at most: the original, and nothing after the exchange failed.
        _ = try? await session.api().getNativeV1Sync(query: .init(cursor: nil))

        #expect(surface.requests.filter { $0.path == "/native/v1/sync" }.count == 1)
    }
}

@Suite("Reading the library")
@MainActor
struct LibraryStoreTests {
    private func store(_ surface: SurfaceStub) -> LibraryStore {
        LibraryStore(session: ServerSession(
            paired: pairing(), store: InMemoryCredentialStore(), transport: surface))
    }

    @Test("The whole feed is drained, because there is no route that lists a library")
    func drainsEveryPage() async {
        let surface = SurfaceStub(["/native/v1/sync": [
            (200, page(title("1", "Movie", "Alpha"), cursor: "c1", hasMore: true)),
            (200, page(title("2", "Movie", "Beta"), cursor: "c2", hasMore: true)),
            (200, page(title("3", "Series", "Gamma"), cursor: "c3", hasMore: false)),
        ]])

        let subject = store(surface)
        await subject.load()

        #expect(subject.state == .loaded)
        #expect(subject.items.count == 3)
        #expect(subject.movies.map(\.title) == ["Alpha", "Beta"])
        #expect(subject.series.map(\.title) == ["Gamma"])
    }

    @Test("A feed that claims more without moving stops, and does not add the repeat")
    func stopsOnAStuckCursor() async {
        // Always the same cursor and always hasMore. A client that trusted it would never return — and
        // one that stopped after taking the page would show the last title twice.
        let surface = SurfaceStub(["/native/v1/sync": [
            (200, page(title("1", "Movie", "Alpha"), cursor: "same", hasMore: true)),
        ]])

        let subject = store(surface)
        await subject.load()

        #expect(subject.state == .loaded)
        #expect(subject.items.count == 1)
    }

    @Test("Kinds this client does not list are dropped rather than shown as neither")
    func ignoresOtherKinds() async {
        let items = [
            title("1", "Movie", "Alpha"),
            title("2", "Episode", "Some episode"),
            title("3", "Season", "Season 1"),
            title("4", "Series", "Gamma"),
        ].joined(separator: ",")

        let subject = store(SurfaceStub(["/native/v1/sync": [(200, page(items, cursor: "c", hasMore: false))]]))
        await subject.load()

        #expect(subject.items.count == 2)
    }

    @Test("Resume and watched come across, which is what the generator used to drop")
    func carriesUserData() async {
        // `userData` is described as a union with null, which the generator skipped entirely until the
        // server started emitting a plain reference. It vanished silently, so this asserts it is there.
        let items = [
            title("1", "Movie", "Started", ticks: 45_000_000_000),
            title("2", "Movie", "Finished", played: true),
        ].joined(separator: ",")

        let subject = store(SurfaceStub(["/native/v1/sync": [(200, page(items, cursor: "c", hasMore: false))]]))
        await subject.load()

        let started = subject.items.first { $0.title == "Started" }
        #expect(started?.resumeSeconds == 4500)          // ticks are hundred-nanosecond units
        #expect(started?.played == false)
        #expect(subject.items.first { $0.title == "Finished" }?.played == true)
    }

    @Test("Titles are sorted the way a viewer reads them, not the way the feed sent them")
    func sorts() async {
        let items = [title("1", "Movie", "Zulu"), title("2", "Movie", "alpha")].joined(separator: ",")

        let subject = store(SurfaceStub(["/native/v1/sync": [(200, page(items, cursor: "c", hasMore: false))]]))
        await subject.load()

        #expect(subject.movies.map(\.title) == ["alpha", "Zulu"])
    }

    @Test("Artwork is asked of this server, never of the metadata provider")
    func artworkComesFromUs() async {
        // The DTO carries the provider's URL, which is what the web UI uses. A television is pointed at
        // our copy so it keeps working with no internet and does not tell TMDb what is being browsed.
        let subject = store(SurfaceStub(["/native/v1/sync": [
            (200, page(title("abc", "Movie", "Alpha"), cursor: "c", hasMore: false)),
        ]]))
        await subject.load()

        let url = subject.items[0].artworkURL(on: subject.server)
        #expect(url?.absoluteString == "https://media.example/native/v1/items/abc/images/primary")
    }

    @Test("A failure is a state a screen can show, not a crash")
    func failure() async {
        let subject = store(SurfaceStub(["/native/v1/sync": [(500, "")]]))
        await subject.load()

        if case .failed = subject.state {} else {
            Issue.record("Expected a failure, got \(subject.state)")
        }
    }
}

@Suite("Dates as this server writes them")
struct DateTranscoderTests {
    private let subject = LenientDateTranscoder()

    @Test("Seven fractional digits, which is what .NET's DateTimeOffset emits")
    func dotNetPrecision() throws {
        // The whole sync feed failed to decode on this, with a `dataCorrupted` that named no field.
        let date = try subject.decode("2026-08-14T14:38:22.1234567+00:00")

        #expect(abs(date.timeIntervalSince1970 - 1_786_718_302.123) < 0.001)
    }

    @Test("Three digits and none, which everything else sends")
    func ordinaryPrecision() throws {
        let millis = try subject.decode("2026-08-14T14:38:22.123Z")
        let whole = try subject.decode("2026-08-14T14:38:22Z")

        #expect(abs(millis.timeIntervalSince(whole) - 0.123) < 0.001)
    }

    @Test("An offset that is not UTC keeps its meaning")
    func offset() throws {
        let plusTwo = try subject.decode("2026-08-14T16:38:22.5000000+02:00")
        let utc = try subject.decode("2026-08-14T14:38:22.5Z")

        #expect(abs(plusTwo.timeIntervalSince(utc)) < 0.001)
    }

    @Test("Something that is not a date is refused rather than guessed at")
    func refuses() {
        #expect(throws: (any Error).self) { try subject.decode("not a date") }
        #expect(throws: (any Error).self) { try subject.decode("") }
    }

    @Test("What it writes, it can read")
    func roundTrip() throws {
        let now = Date(timeIntervalSince1970: 1_786_718_302.25)

        #expect(abs(try subject.decode(try subject.encode(now)).timeIntervalSince(now)) < 0.001)
    }
}

@Suite("What the server says about playing something")
struct PlaybackPlanTests {
    private let server = URL(string: "https://media.example")!

    private func resolution(
        decision: String, url: String? = "/native/v1/media/abc/remux?token=t",
        transport: String? = "ByteRange", reason: String? = nil, signalling: String? = "dvh1"
    ) -> String {
        func field(_ name: String, _ value: String?) -> String {
            guard let value else { return "\"\(name)\":null" }
            return "\"\(name)\":\"\(value)\""
        }

        var parts: [String] = []
        parts.append(field("mediaSourceId", "abc"))
        parts.append(field("versionName", nil))
        parts.append(field("decision", decision))
        parts.append(field("transport", transport))
        parts.append(field("url", url))
        parts.append(field("signalling", signalling))
        parts.append(field("sourceDynamicRange", "Dolby Vision"))
        parts.append(field("reason", reason))
        return "{" + parts.joined(separator: ",") + "}"
    }

    private func plan(_ body: String) throws -> PlaybackPlan {
        let json = "{\"itemId\":\"i\",\"sources\":[" + body + "]}"
        let dto = try JSONDecoder().decode(
            Components.Schemas.NativePlaybackResolutionResponse.self, from: Data(json.utf8))
        return PlaybackPlan.all(dto, server: server)[0]
    }

    @Test("A remux is a stream, with the signalling the server chose")
    func remux() throws {
        guard case .play(let stream) = try plan(resolution(decision: "Remux")) else {
            Issue.record("expected a stream")
            return
        }

        #expect(stream.decision == .remux)
        #expect(stream.signalling == "dvh1")
        #expect(stream.url.absoluteString.hasPrefix("https://media.example/native/v1/media/"))
    }

    @Test("Every refusal keeps its own name rather than becoming \"cannot play\"")
    func refusals() throws {
        let cases: [(String, PlaybackRefusal)] = [
            ("packaging_pending", .packagingPending),
            ("packaging_unsupported_audio", .packagingUnsupportedAudio),
            ("packaging_unsupported_video", .packagingUnsupportedVideo),
            ("unsupported_dynamic_range", .unsupportedDynamicRange),
            ("no_audio_track", .noAudioTrack),
            ("no_file", .noFile),
        ]

        for (wire, expected) in cases {
            #expect(try plan(resolution(decision: "Unsupported", url: nil, reason: wire))
                == .refused(expected))
        }
    }

    @Test("A reason this build has never heard of is carried, not flattened")
    func unknownReason() throws {
        // An older client meeting a newer server must not turn a specific answer into "cannot play".
        #expect(try plan(resolution(decision: "Unsupported", url: nil, reason: "something_new"))
            == .refused(.unknown("something_new")))
    }

    @Test("Only pending means waiting is the remedy")
    func pending() {
        #expect(PlaybackRefusal.packagingPending.isPending)
        #expect(!PlaybackRefusal.packagingUnsupportedAudio.isPending)
        #expect(!PlaybackRefusal.noFile.isPending)
    }

    @Test("A decision to play with nowhere to play from is refused rather than handed to AVFoundation")
    func playableWithoutUrl() throws {
        // A server contradicting itself. Passing this on would fail inside AVFoundation, where the
        // reason is lost.
        guard case .refused = try plan(resolution(decision: "Remux", url: nil)) else {
            Issue.record("expected a refusal")
            return
        }
    }

    @Test("HLS is refused, because this build has no idea what to do with it")
    func hls() throws {
        // Deliberately unbuilt on the server, so meeting it means meeting a newer server.
        #expect(try plan(resolution(decision: "Remux", transport: "Hls"))
            == .refused(.unknown("transport_hls")))
    }

    @Test("The first copy that plays is the one taken, not the first copy")
    func picksAPlayableCopy() throws {
        // A title can hold a 4K copy this device cannot open beside a 1080p one it can, and collapsing
        // that to one verdict would hide the copy that works.
        let refused = resolution(decision: "Unsupported", url: nil, reason: "no_audio_track")
        let playable = resolution(decision: "DirectPlay", url: "/native/v1/media/def?token=t")
        let body = "{\"itemId\":\"i\",\"sources\":[" + refused + "," + playable + "]}"
        let dto = try JSONDecoder().decode(
            Components.Schemas.NativePlaybackResolutionResponse.self, from: Data(body.utf8))
        let plans = PlaybackPlan.all(dto, server: server)

        #expect(plans.count == 2)
        guard case .play = plans[1] else {
            Issue.record("expected the second copy to play")
            return
        }
    }
}
