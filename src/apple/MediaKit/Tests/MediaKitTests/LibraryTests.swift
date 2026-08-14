import Foundation
import HTTPTypes
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

    @Test("A feed that claims more without moving stops instead of paging for ever")
    func stopsOnAStuckCursor() async {
        // Always the same cursor and always hasMore. A client that trusted it would never return.
        let surface = SurfaceStub(["/native/v1/sync": [
            (200, page(title("1", "Movie", "Alpha"), cursor: "same", hasMore: true)),
        ]])

        let subject = store(surface)
        await subject.load()

        #expect(subject.state == .loaded)
        #expect(subject.items.count <= 2)
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
