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

/// Holds the first response until the test has attempted a concurrent refresh.
private actor GatedCollectionTransport: ClientTransport {
    private let firstStatus: Int
    private var responseGate: CheckedContinuation<Void, Never>?
    private var requestGate: CheckedContinuation<Void, Never>?
    private(set) var calls = 0

    init(firstStatus: Int) { self.firstStatus = firstStatus }

    func waitForRequest() async {
        if calls > 0 { return }
        await withCheckedContinuation { requestGate = $0 }
    }

    func release() { responseGate?.resume(); responseGate = nil }

    func send(
        _ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String
    ) async throws -> (HTTPResponse, HTTPBody?) {
        calls += 1
        let status = calls == 1 ? firstStatus : 200
        if calls == 1 {
            await withCheckedContinuation { continuation in
                responseGate = continuation
                requestGate?.resume()
                requestGate = nil
            }
        }
        var response = HTTPResponse(status: .init(code: status))
        response.headerFields[.contentType] = "application/json"
        return (response, HTTPBody("[]"))
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

/// One title in full, as `items/{id}` answers: only what the detail needs to decode.
private func item(_ id: String, _ name: String, played: Bool = false, ticks: Int = 0) -> String {
    """
    {"detail":{"id":"\(id)","catalogId":"cat","catalogName":"Films","catalogRoot":"/films",\
    "kind":"Movie","title":"\(name)","genres":[],"mediaSources":[],"cast":[],"directors":[],"creators":[],\
    "studios":[],"keywords":[],\
    "userData":{"key":"k","playbackPositionTicks":\(ticks),"playCount":0,"isFavorite":false,"played":\(played)}},\
    "sources":[],"images":{}}
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

@Suite("Which stream is the film")
struct TitleVideoTests {
    private func version(_ codecs: [String]) -> TitleVersion {
        TitleVersion(
            id: "s", versionName: nil, container: "mkv", sizeBytes: 1, durationSeconds: 1,
            videos: codecs.enumerated().map { index, codec in
                TitleTrack(
                    id: "v\(index)", label: codec, language: nil, codec: codec, hdrFormat: nil,
                    dolbyVision: nil, isExternal: false)
            },
            audio: [], subtitles: [])
    }

    @Test("A cover listed after the picture is not the picture")
    func coverSecond() {
        #expect(version(["hevc", "mjpeg"]).video?.codec == "hevc")
    }

    @Test("A cover listed before the picture is not the picture either")
    func coverFirst() {
        // The case that matters: taking the first would disagree with what the server judges, and the
        // two disagreeing about what the film is was the whole defect.
        #expect(version(["mjpeg", "hevc"]).video?.codec == "hevc")
    }

    @Test("A file whose only video is a still says so rather than claiming none")
    func onlyAStill() {
        #expect(version(["png"]).video?.codec == "png")
    }

    @Test("A source with no video has no film")
    func noVideo() {
        #expect(version([]).video == nil)
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

    @Test("Opening a title brings its card up to date, which the feed read at launch cannot")
    func detailRefreshesTheCard() async throws {
        // The feed said never started. By the time the title's screen is fetched again — after a
        // viewing was reported — the server says half an hour in, then watched.
        let surface = SurfaceStub([
            "/native/v1/sync": [(200, page(title("1", "Movie", "Alpha"), cursor: "c", hasMore: false))],
            "/native/v1/items/1": [
                (200, item("1", "Alpha", ticks: 18_000_000_000)),
                (200, item("1", "Alpha", played: true)),
            ],
        ])

        let subject = store(surface)
        await subject.load()
        #expect(subject.items[0].resumeSeconds == 0)

        let started = try await subject.detail(for: "1")
        #expect(started.resumeSeconds == 1800)
        #expect(subject.items[0].resumeSeconds == 1800)
        #expect(subject.items[0].played == false)

        let finished = try await subject.detail(for: "1")
        #expect(finished.played)
        #expect(subject.items[0].played)
        #expect(subject.items[0].resumeSeconds == 0)
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
        transport: String? = "ByteRange", reason: String? = nil, signalling: String? = "dvh1",
        audioStreamId: String? = nil, subtitleStreamId: String? = nil
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
        parts.append(field("audioStreamId", audioStreamId))
        parts.append(field("subtitleStreamId", subtitleStreamId))
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
                == .refused(expected, source: "abc"))
        }
    }

    @Test("A reason this build has never heard of is carried, not flattened")
    func unknownReason() throws {
        // An older client meeting a newer server must not turn a specific answer into "cannot play".
        #expect(try plan(resolution(decision: "Unsupported", url: nil, reason: "something_new"))
            == .refused(.unknown("something_new"), source: "abc"))
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
            == .refused(.unknown("transport_hls"), source: "abc"))
    }

    @Test("A refusal says which copy it is about, so a viewer can be told why their pick will not play")
    func refusalNamesItsCopy() throws {
        let refused = try plan(resolution(decision: "Unsupported", url: nil, reason: "no_file"))

        #expect(refused.mediaSourceId == "abc")
        #expect(!refused.isPlayable)
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

    @Test("The tracks the server chose come back with the stream")
    func chosenTracks() throws {
        // A picker ticks these rather than its own last request: a stored preference answers when
        // nothing was picked, so the first menu already has a tick against a row nobody selected here.
        guard case .play(let stream) = try plan(resolution(
            decision: "Remux",
            audioStreamId: "3f2b1c4d-0000-4000-8000-000000000001",
            subtitleStreamId: "3f2b1c4d-0000-4000-8000-000000000002")) else {
            Issue.record("expected a stream")
            return
        }

        #expect(stream.audioStreamId == "3f2b1c4d-0000-4000-8000-000000000001")
        #expect(stream.subtitleStreamId == "3f2b1c4d-0000-4000-8000-000000000002")
    }

    @Test("Direct play names no tracks, because the choice was never the server's")
    func directPlayNamesNoTracks() throws {
        guard case .play(let stream) = try plan(resolution(
            decision: "DirectPlay", url: "/native/v1/media/abc?token=t", signalling: nil)) else {
            Issue.record("expected a stream")
            return
        }

        #expect(stream.audioStreamId == nil)
        #expect(stream.subtitleStreamId == nil)
    }
}

@Suite("Collection browsing")
@MainActor
struct CollectionTests {
    private func store(_ answers: [String: [SurfaceStub.Answer]]) -> (CollectionStore, SurfaceStub) {
        let transport = SurfaceStub(answers)
        return (CollectionStore(session: ServerSession(paired: pairing(), transport: transport)), transport)
    }

    @Test func distinguishesEmptyUnsupportedAndFailure() async {
        let (empty, _) = store(["/native/v1/collections": [(200, "[]")]])
        await empty.load()
        #expect(empty.state == .loaded)
        #expect(empty.items.isEmpty)
        let (old, _) = store(["/native/v1/collections": [(404, "")]])
        await old.load()
        #expect(old.state == .unsupported)
        let (failed, _) = store(["/native/v1/collections": [(503, "")]])
        await failed.load()
        guard case .failed = failed.state else { Issue.record("A server error is not an empty list"); return }
    }

    @Test(arguments: [200, 404, 503])
    func ignoresOverlappingLoadsAndAllowsLaterRefresh(firstStatus: Int) async {
        let transport = GatedCollectionTransport(firstStatus: firstStatus)
        let collections = CollectionStore(session: ServerSession(paired: pairing(), transport: transport))
        let firstLoad = Task { await collections.load() }
        await transport.waitForRequest()
        await collections.load()
        #expect(await transport.calls == 1)
        #expect(collections.state == .loading)
        await transport.release()
        await firstLoad.value
        if firstStatus == 200 { #expect(collections.state == .loaded) }
        if firstStatus == 404 { #expect(collections.state == .unsupported) }
        if firstStatus == 503 { #expect(collections.state == .failed("The server returned HTTP 503.")) }
        await collections.load()
        #expect(await transport.calls == 2)
        #expect(collections.state == .loaded)
    }

    @Test func reloadRecoversAfterServerUpgrade() async {
        let (collections, transport) = store([
            "/native/v1/collections": [
                (404, ""),
                (200, #"[{"id":"saga","name":"Saga","itemCount":2}]"#)
            ]
        ])
        await collections.load()
        #expect(collections.state == .unsupported)
        await collections.load()
        #expect(collections.state == .loaded)
        #expect(collections.items.map(\.id) == ["saga"])
        #expect(transport.tokensSeen.count == 2)
    }

    @Test func decodesCollectionAndUsesBearer() async throws {
        let (collections, transport) = store([
            "/native/v1/collections": [(200, #"[{"id":"saga","name":"Saga","posterUrl":"/native/v1/collections/saga/images/primary","itemCount":2}]"#)],
            "/native/v1/collections/saga": [(200, """
            {"id":"saga","name":"Saga","items":[\(title("one", "Movie", "First")),\(title("two", "Movie", "Second"))]}
            """)]
        ])
        await collections.load()
        #expect(collections.items.first?.itemCount == 2)
        #expect(collections.items.first?.posterPath?.hasPrefix("/native/v1/") == true)
        let detail = try await collections.detail(id: "saga")
        #expect(detail.items.map(\.id) == ["one", "two"])
        #expect(transport.tokensSeen == ["Bearer old-token", "Bearer old-token"])
    }

    @Test func removedDetailDoesNotMarkServerUnsupported() async {
        let (collections, _) = store(["/native/v1/collections/gone": [(404, "")]])
        do {
            _ = try await collections.detail(id: "gone")
            Issue.record("Missing collection must throw")
        } catch CollectionError.missing {} catch { Issue.record("Wrong error: \(error)") }
        #expect(collections.state != .unsupported)
    }

    @Test func continueWatchingRefreshesAfterCompletion() async throws {
        let transport = SurfaceStub([
            "/native/v1/sync": [(200, page([
                title("one", "Movie", "Started", ticks: 42_000_000),
                title("two", "Movie", "Watched", played: true, ticks: 42_000_000),
                title("three", "Series", "Series", ticks: 42_000_000),
                title("four", "Movie", "New")
            ].joined(separator: ","), cursor: "c", hasMore: false))],
            "/native/v1/items/one": [(200, item("one", "Started", played: true))]
        ])
        let library = LibraryStore(session: ServerSession(paired: pairing(), transport: transport))
        await library.load()
        #expect(library.continueWatching.map(\.id) == ["one"])
        _ = try await library.detail(for: "one")
        #expect(library.continueWatching.isEmpty)
    }

    @Test func resumeLabelsHandleHoursAndInvalidValues() {
        #expect(PlaybackPosition.label(2535) == "42:15")
        #expect(PlaybackPosition.label(3661.9) == "1:01:01")
        #expect(PlaybackPosition.label(-1) == "0:00")
        #expect(PlaybackPosition.label(.infinity) == "0:00")
        #expect(PlaybackPosition.label(.nan) == "0:00")
    }
}

@Suite("Library card video formats")
struct LibraryCardVideoFormatTests {
    @Test("Formats appear beside the year without fetching detail")
    func formatCaption() throws {
        let data = Data(#"{"id":"film","catalogId":"cat","kind":"Movie","title":"Film","year":1997,"videoFormats":["HDR10","Dolby Vision"]}"#.utf8)
        let dto = try JSONDecoder().decode(Components.Schemas.LibraryItemDto.self, from: data)
        let card = try #require(LibraryTitle(dto))
        #expect(card.gridSubtitle == "1997 · HDR10 · Dolby Vision")
    }

    @Test("Older server responses and absent years remain valid")
    func olderServer() throws {
        let data = Data(#"{"id":"film","catalogId":"cat","kind":"Movie","title":"Film","year":1997}"#.utf8)
        let dto = try JSONDecoder().decode(Components.Schemas.LibraryItemDto.self, from: data)
        let card = try #require(LibraryTitle(dto))
        #expect(card.videoFormats.isEmpty)
        #expect(card.gridSubtitle == "1997")
        var noYear = dto
        noYear.year = nil
        noYear.videoFormats = ["Dolby Vision"]
        #expect(LibraryTitle(noYear)?.gridSubtitle == "Dolby Vision")
        noYear.videoFormats = nil
        #expect(LibraryTitle(noYear)?.gridSubtitle == "")
    }
}

@Suite("Title credits")
struct TitleCreditTests {
    @Test("Cast order, roles, portraits and crew survive detail mapping")
    func populatedCredits() throws {
        var json = try #require(JSONSerialization.jsonObject(with: Data(item("film", "Film").utf8)) as? [String: Any])
        var detail = try #require(json["detail"] as? [String: Any])
        detail["cast"] = [
            ["provider": "tmdb", "providerId": "2", "name": "First Actor", "character": "Captain", "profileUrl": "https://images.example/actor.jpg"],
            ["provider": "tmdb", "providerId": "1", "name": "Second Actor"]
        ]
        detail["crew"] = [
            ["id": "director-credit", "provider": "tmdb", "providerId": "10", "name": "First Director", "job": "Director", "department": "Directing", "profileUrl": "https://images.example/director.jpg"],
            ["id": "writer-credit", "provider": "tmdb", "providerId": "11", "name": "Writer", "job": "Screenplay"]
        ]
        detail["directors"] = ["First Director", "Second Director"]
        detail["creators"] = ["Series Creator"]
        json["detail"] = detail
        let dto = try JSONDecoder().decode(Components.Schemas.NativeItemDto.self, from: JSONSerialization.data(withJSONObject: json))
        let result = TitleDetail(dto)
        #expect(result.cast.map(\.name) == ["First Actor", "Second Actor"])
        #expect(result.cast.first?.character == "Captain")
        #expect(result.cast.first?.profileURL?.absoluteString == "https://images.example/actor.jpg")
        #expect(result.cast.last?.profileURL == nil)
        #expect(result.cast.last?.character == nil)
        #expect(result.directors == ["First Director", "Second Director"])
        #expect(result.creators == ["Series Creator"])
        #expect(result.crew.map(\.name) == ["First Director", "Writer", "Second Director", "Series Creator"])
        #expect(result.crew.first?.profileURL?.absoluteString == "https://images.example/director.jpg")
        #expect(result.crew[1].job == "Screenplay")
        #expect(result.crew[1].profileURL == nil)
        #expect(result.crew.last?.job == "Creator")
        detail.removeValue(forKey: "crew")
        json["detail"] = detail
        let legacy = TitleDetail(try JSONDecoder().decode(Components.Schemas.NativeItemDto.self,
            from: JSONSerialization.data(withJSONObject: json)))
        #expect(legacy.crew.map(\.name) == ["First Director", "Second Director", "Series Creator"])
    }

    @Test("Empty credits remain empty")
    func emptyCredits() throws {
        let dto = try JSONDecoder().decode(Components.Schemas.NativeItemDto.self, from: Data(item("film", "Film").utf8))
        let detail = TitleDetail(dto)
        #expect(detail.cast.isEmpty && detail.crew.isEmpty && detail.directors.isEmpty && detail.creators.isEmpty)
    }
}
