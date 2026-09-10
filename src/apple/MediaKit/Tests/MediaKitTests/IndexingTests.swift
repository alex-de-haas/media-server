import Foundation
import MediaServerAPI
import Testing
@testable import MediaKit

@Suite("Indexing progress")
struct IndexingTests {
    @Test("Visible screens share one authenticated stream and the last cancellation closes it")
    @MainActor func lifecycle() async throws {
        let host = "indexing-\(UUID().uuidString.lowercased()).invalid"
        let paired = PairedServer(server: URL(string: "https://\(host)/")!, serverName: "Test",
            appId: "test", coreOrigin: URL(string: "https://example.invalid/")!, coreToken: "test",
            identity: .init(accessToken: "test", expiresAt: .distantFuture))
        let session = ServerSession(paired: paired)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [IndexingProtocol.self]
        let feed = IndexingFeed(session: session, configuration: configuration)
        let first = Task { for await _ in feed.connections() {} }
        let second = Task { for await _ in feed.connections() {} }
        defer { first.cancel(); second.cancel(); IndexingProtocol.clear(host) }
        try await eventually { feed.connected && IndexingProtocol.count(host) == 1 }
        #expect(IndexingProtocol.token(host) == "Bearer test")
        first.cancel()
        await first.value
        #expect(feed.connected)
        #expect(IndexingProtocol.count(host) == 1)
        second.cancel()
        await second.value
        try await eventually { !feed.connected && IndexingProtocol.stopped(host) }
    }

    @MainActor private func eventually(_ condition: () -> Bool) async throws {
        let end = ContinuousClock.now + .seconds(3)
        while !condition(), ContinuousClock.now < end { try await Task.sleep(for: .milliseconds(10)) }
        #expect(condition())
    }

    @Test("SSE accepts fragmented UTF-8, CRLF, comments and multiple data lines")
    func frames() throws {
        var parser = IndexingFrameParser()
        let input = ": ping\r\n\r\nevent: unrelated\ndata: {}\n\n"
            + "event: indexingChanged\r\ndata: {\"itemId\":\"film\",\"sourceId\":\"source\",\r\n"
            + "data: \"indexing\":{\"state\":\"indexing\",\"percent\":42,\"revision\":2}}\r\n\r\n"
        var received: [IndexingUpdate] = []
        for byte in input.utf8 { if let update = try parser.push(byte) { received.append(update) } }
        #expect(received.count == 1)
        #expect(received.first?.indexing.percent == 42)
    }

    @Test("Malformed or unknown events do not break subsequent indexing updates")
    func malformed() throws {
        var parser = IndexingFrameParser()
        for byte in "event: indexingChanged\ndata: not-json\n\n".utf8 {
            #expect(try parser.push(byte) == nil)
        }
        let status = IndexingStatus(state: "future", percent: nil, revision: 1)
        #expect(status.label == nil)
        #expect(IndexingStatus(state: "indexing", percent: 100, revision: 1).label == "Indexing · 99%")
        #expect(IndexingStatus(state: "ready", percent: nil, revision: 1).label == nil)
    }

    @Test("Oversized frames are bounded")
    func oversized() {
        #expect(throws: URLError.self) {
            var parser = IndexingFrameParser()
            for _ in 0...65_536 { _ = try parser.push(65) }
        }
    }

    @Test("Optional generated metadata maps to the domain and older servers remain supported")
    func mapping() throws {
        let old = #"{"id":"source","container":"mkv","fileName":"film.mkv","sizeBytes":100,"durationTicks":0,"streams":[]}"#
        let decoder = JSONDecoder()
        let dto = try decoder.decode(Components.Schemas.MediaSourceDto.self, from: Data(old.utf8))
        #expect(TitleVersion(dto).indexing == nil)
        var updated = dto
        updated.indexing = .init(state: "saving", revision: 3)
        #expect(TitleVersion(updated).indexing?.label == "Saving index")
    }

    @Test("Completion, retry and sidecar events cannot be overwritten by older snapshots")
    @MainActor func ordering() {
        let paired = PairedServer(server: URL(string: "https://example.invalid/")!, serverName: "Test",
            appId: "test", coreOrigin: URL(string: "https://example.invalid/")!, coreToken: "test",
            identity: .init(accessToken: "test", expiresAt: .distantFuture))
        let session = ServerSession(paired: paired)
        let feed = session.indexing!
        let snapshot = IndexingStatus(state: "waiting", percent: nil, revision: 1)
        feed.apply(.init(itemId: "film", sourceId: "SOURCE", streamId: nil,
                         indexing: .init(state: "ready", percent: nil, revision: 3)))
        feed.apply(.init(itemId: "film", sourceId: "SOURCE", streamId: nil,
                         indexing: .init(state: "indexing", percent: 42, revision: 2)))
        #expect(feed.status(for: "source", snapshot: snapshot)?.state == "ready")
        feed.apply(.init(itemId: "film", sourceId: "SOURCE", streamId: "DUB",
                         indexing: .init(state: "indexing", percent: 12, revision: 4)))
        #expect(feed.status(for: "source", snapshot: snapshot)?.state == "ready")
        #expect(feed.status(for: "dub", snapshot: nil)?.percent == 12)
        let retried = IndexingStatus(state: "indexing", percent: 0, revision: 5)
        #expect(feed.status(for: "source", snapshot: retried) == retried)
    }
}


private final class IndexingProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var requests: [String: [IndexingProtocol]] = [:]
    private var didStop = false
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.withLock { Self.requests[request.url!.host!, default: []].append(self) }
        client?.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: 200,
            httpVersion: nil, headerFields: ["Content-Type": "text/event-stream"])!, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(": connected\n\n".utf8))
    }
    override func stopLoading() { Self.lock.withLock { didStop = true } }
    static func count(_ host: String) -> Int { lock.withLock { requests[host]?.count ?? 0 } }
    static func token(_ host: String) -> String? { lock.withLock { requests[host]?.first?.request.value(forHTTPHeaderField: "Authorization") } }
    static func stopped(_ host: String) -> Bool { lock.withLock { requests[host]?.last?.didStop ?? false } }
    static func clear(_ host: String) { _ = lock.withLock { requests.removeValue(forKey: host) } }
}
