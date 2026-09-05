import AVFoundation
import Foundation
import Testing

@testable import MediaKit

@Suite("Loader delivery lifecycle", .serialized)
struct RemuxLoaderTests {
    @Test("An open-ended reader continues beyond the first full window with exact bytes")
    func openEndedRefills() async throws {
        let fixture = Fixture(total: 256)
        defer { fixture.loader.stop() }
        let request = Request(offset: 0, length: 0, toEnd: true)
        await fixture.onQueue { _ = fixture.loader.accept(request) }

        for start in stride(from: 0, to: 256, by: 64) {
            let connection = try await fixture.network.range(start: start)
            connection.answer(total: 256, start: start, count: 64)
            try await fixture.until { request.currentOffset >= Int64(start + 64) }
            fixture.loader.playerHolds(seconds: 0)
        }
        try await fixture.until { request.finished }
        let bytes = await fixture.onQueue { request.bytes }
        #expect(bytes == payload(start: 0, count: 256))
        #expect(fixture.loader.makeSnapshot().serverRequests == 4)
        #expect(fixture.loader.makeSnapshot().asides == 0)
    }

    @Test("A late aside response cannot duplicate bytes already served by a moved window")
    func asideOwnsDelivery() async throws {
        let fixture = Fixture(total: 1_024)
        defer { fixture.loader.stop() }
        let initial = Request(offset: 0, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(initial) }
        let first = try await fixture.network.range(start: 0)
        first.answer(total: 1_024, start: 0, count: 64)
        try await fixture.until { initial.finished }

        // A large speculative reader gets its own HTTP response, which we delay.
        let speculative = Request(offset: 200, length: 2_000_000)
        await fixture.onQueue { _ = fixture.loader.accept(speculative) }
        let aside = try await fixture.network.range(start: 200)
        #expect(fixture.loader.makeSnapshot().restarts == 0)

        // The viewer seeks to the same area. The window arrives before the aside does.
        let seek = Request(offset: 200, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(seek) }
        let fill = try await fixture.network.range(start: 200, occurrence: 1)
        fill.answer(total: 1_024, start: 200, count: 64)
        try await fixture.until { seek.finished }
        let before = await fixture.onQueue { speculative.bytes.count }
        #expect(before == 0)

        aside.answer(total: 1_024, start: 200, count: 824)
        try await fixture.until { speculative.finished }
        let bytes = await fixture.onQueue { speculative.bytes }
        #expect(bytes == payload(start: 200, count: 824))
        let details = fixture.loader.makeSnapshot()
        #expect(details.asideBehind == 0)
        #expect(details.asideAhead == 1)
        #expect(details.asideSmall == 1)
        #expect(details.asideRequestedBytes == 824)
        #expect(details.lastRestart?.windowStart == 0)
        #expect(details.lastRestart?.windowEnd == 64)
        #expect(details.lastRestart?.offset == 200)
        #expect(details.lastRestart?.requestedLength == 8)
        #expect(details.lastRestart?.toEnd == false)
    }

    @Test("A speculative reader inside the window cannot evict the next small play-head read")
    func speculativeDoesNotEvict() async throws {
        let fixture = Fixture(total: 1_024, budget: 256)
        defer { fixture.loader.stop() }
        let initial = Request(offset: 0, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(initial) }
        let fill = try await fixture.network.range(start: 0)
        fill.answer(total: 1_024, start: 0, count: 256)
        try await fixture.until { initial.finished }
        let speculative = Request(offset: 128, length: 2_000_000)
        await fixture.onQueue { _ = fixture.loader.accept(speculative) }
        try await fixture.until { speculative.currentOffset >= 256 }
        let next = Request(offset: 8, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(next) }
        try await fixture.until { next.finished }
        let bytes = await fixture.onQueue { next.bytes }
        #expect(bytes == payload(start: 8, count: 8))
        #expect(fixture.loader.makeSnapshot().restarts == 0)
    }

    @Test("Cancelling an aside cancels its HTTP task and prevents delivery")
    func cancellation() async throws {
        let fixture = Fixture(total: 1_024)
        defer { fixture.loader.stop() }
        let initial = Request(offset: 0, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(initial) }
        let fill = try await fixture.network.range(start: 0)
        fill.answer(total: 1_024, start: 0, count: 64)
        try await fixture.until { initial.finished }
        let request = Request(offset: 200, length: 2_000_000)
        await fixture.onQueue { _ = fixture.loader.accept(request) }
        let aside = try await fixture.network.range(start: 200)
        await fixture.onQueue { fixture.loader.cancel(request) }
        try await waitUntil { aside.wasStopped }
        let bytes = await fixture.onQueue { request.bytes }
        #expect(bytes.isEmpty)
        #expect(fixture.loader.makeSnapshot().outstanding == 0)
    }

    @Test("A speculative request just past a full window is fetched instead of waiting forever")
    func fullWindowAhead() async throws {
        let fixture = Fixture(total: 1_024)
        defer { fixture.loader.stop() }
        let initial = Request(offset: 0, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(initial) }
        let fill = try await fixture.network.range(start: 0)
        fill.answer(total: 1_024, start: 0, count: 64)
        try await fixture.until { initial.finished }
        let request = Request(offset: 80, length: 2_000_000)
        await fixture.onQueue { _ = fixture.loader.accept(request) }
        let aside = try await fixture.network.range(start: 80)
        aside.answer(total: 1_024, start: 80, count: 944)
        try await fixture.until { request.finished }
        let bytes = await fixture.onQueue { request.bytes }
        #expect(bytes == payload(start: 80, count: 944))
        #expect(fixture.loader.makeSnapshot().restarts == 0)
    }
}

private func payload(start: Int, count: Int) -> Data {
    Data((start ..< start + count).map { UInt8($0 % 251) })
}

private enum Timeout: Error { case expired }

private func waitUntil(_ condition: () async -> Bool) async throws {
    let deadline = ContinuousClock.now + .seconds(5)
    while !(await condition()) {
        if ContinuousClock.now >= deadline { throw Timeout.expired }
        try await Task.sleep(for: .milliseconds(5))
    }
}

private final class Request: LoadingRequest, LoadingDataRequest, @unchecked Sendable {
    let requestedOffset: Int64
    let requestedLength: Int
    let requestsAllDataToEndOfResource: Bool
    var bytes = Data()
    var finished = false
    var error: (any Error)?
    var currentOffset: Int64 { requestedOffset + Int64(bytes.count) }
    var loadingData: (any LoadingDataRequest)? { self }

    init(offset: Int64, length: Int, toEnd: Bool = false) {
        requestedOffset = offset
        requestedLength = length
        requestsAllDataToEndOfResource = toEnd
    }
    func describe(length: Int64) {}
    func respond(with data: Data) { bytes.append(data) }
    func finishLoading() { finished = true }
    func finishLoading(with error: (any Error)?) { self.error = error; finished = true }
}

private final class Fixture: @unchecked Sendable {
    let loader: RemuxLoader
    let network: Network
    private let host: String
    init(total: Int, budget: Int = 64) {
        network = Network(total: total)
        host = UUID().uuidString.lowercased()
        Stub.register(network, host: host)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [Stub.self]
        loader = RemuxLoader(origin: URL(string: "https://\(host)/film")!, budget: budget,
                             tail: 0, lag: 32, target: 20, configuration: configuration)
    }
    deinit { Stub.unregister(host: host) }
    func onQueue<T: Sendable>(_ body: @escaping @Sendable () -> T) async -> T {
        await withCheckedContinuation { continuation in
            loader.queue.async { continuation.resume(returning: body()) }
        }
    }
    func until(_ condition: @escaping @Sendable () -> Bool) async throws {
        try await waitUntil { await self.onQueue(condition) }
    }
}

private final class Network: @unchecked Sendable {
    let total: Int
    private let lock = NSLock()
    private var connections: [Stub] = []
    init(total: Int) { self.total = total }
    func receive(_ connection: Stub) {
        if connection.request.httpMethod == "HEAD" {
            connection.answerHead(total: total)
        } else {
            lock.withLock { connections.append(connection) }
        }
    }
    func range(start: Int, occurrence: Int = 0) async throws -> Stub {
        func found() -> Stub? {
            lock.withLock {
                let matches = connections.filter {
                    $0.request.value(forHTTPHeaderField: "Range")?.hasPrefix("bytes=\(start)-") == true
                }
                return matches.indices.contains(occurrence) ? matches[occurrence] : nil
            }
        }
        try await waitUntil { found() != nil }
        return found()!
    }
}

private final class Stub: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var networks: [String: Network] = [:]
    private let state = NSLock()
    private var stopped = false
    var wasStopped: Bool { state.withLock { stopped } }
    static func register(_ network: Network, host: String) {
        lock.withLock { networks[host] = network }
    }
    static func unregister(host: String) {
        _ = lock.withLock { networks.removeValue(forKey: host) }
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let network = Self.lock.withLock { Self.networks[request.url!.host!] }
        guard let network else { return }
        network.receive(self)
    }
    override func stopLoading() { state.withLock { stopped = true } }
    func answerHead(total: Int) {
        client!.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: 200,
            httpVersion: nil, headerFields: ["Content-Length": "\(total)"])!, cacheStoragePolicy: .notAllowed)
        client!.urlProtocolDidFinishLoading(self)
    }
    func answer(total: Int, start: Int, count: Int) {
        client!.urlProtocol(self, didReceive: HTTPURLResponse(url: request.url!, statusCode: 206,
            httpVersion: nil, headerFields: ["Content-Length": "\(count)",
                "Content-Range": "bytes \(start)-\(start + count - 1)/\(total)"])!, cacheStoragePolicy: .notAllowed)
        for offset in stride(from: start, to: start + count, by: 8) {
            client!.urlProtocol(self, didLoad: payload(start: offset, count: min(8, start + count - offset)))
        }
        client!.urlProtocolDidFinishLoading(self)
    }
}
