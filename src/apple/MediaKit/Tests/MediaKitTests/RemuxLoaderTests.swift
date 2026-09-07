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

        // The viewer seeks to the same area: two reads look like a probe and are fetched on their
        // own, the third shows a reader there, and the window restarts for it. It arrives before the
        // aside does.
        for (offset, occurrence) in [(200, 1), (208, 0)] {
            let read = Request(offset: Int64(offset), length: 8)
            await fixture.onQueue { _ = fixture.loader.accept(read) }
            let alone = try await fixture.network.range(start: offset, occurrence: occurrence)
            alone.answer(total: 1_024, start: offset, count: 8)
            try await fixture.until { read.finished }
        }
        let settled = Request(offset: 216, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(settled) }
        let fill = try await fixture.network.range(start: 216)
        fill.answer(total: 1_024, start: 216, count: 64)
        try await fixture.until { settled.finished }
        let before = await fixture.onQueue { speculative.bytes.count }
        #expect(before == 0)

        aside.answer(total: 1_024, start: 200, count: 824)
        try await fixture.until { speculative.finished }
        let bytes = await fixture.onQueue { speculative.bytes }
        #expect(bytes == payload(start: 200, count: 824))
        let details = fixture.loader.makeSnapshot()
        #expect(details.asideBehind == 0)
        #expect(details.asideAhead == 3)
        #expect(details.asideSmall == 3)
        #expect(details.asideRequestedBytes == 840)
        #expect(details.lastRestart?.windowStart == 0)
        #expect(details.lastRestart?.windowEnd == 64)
        #expect(details.lastRestart?.offset == 216)
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

    @Test("A reader a little behind the window is carried aside; one farther back restarts it a tail earlier")
    func stepsBackward() async throws {
        // The window keeps sixteen bytes behind the lowest reader. A reader's third read at 216, out
        // of the initial window's reach, restarts it a tail before: [200, 264).
        let fixture = Fixture(total: 1_024, budget: 64, tail: 16)
        defer { fixture.loader.stop() }
        for offset in [200, 208] {
            let read = Request(offset: Int64(offset), length: 8)
            await fixture.onQueue { _ = fixture.loader.accept(read) }
            let alone = try await fixture.network.range(start: offset)
            alone.answer(total: 1_024, start: offset, count: 8)
            try await fixture.until { read.finished }
        }
        let settling = Request(offset: 216, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(settling) }
        let fill = try await fixture.network.range(start: 200, occurrence: 1)
        fill.answer(total: 1_024, start: 200, count: 64)
        try await fixture.until { settling.finished }
        #expect(fixture.loader.makeSnapshot().restarts == 1)

        // A step back of eight bytes — within the tail — is fetched on its own, and the window stays.
        let step = Request(offset: 192, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(step) }
        let aside = try await fixture.network.range(start: 192)
        aside.answer(total: 1_024, start: 192, count: 8)
        try await fixture.until { step.finished }
        let stepped = await fixture.onQueue { step.bytes }
        #expect(stepped == payload(start: 192, count: 8))
        #expect(fixture.loader.makeSnapshot().restarts == 1)

        // A step back past the tail is a seek: its first reads are fetched on their own, the third
        // shows a reader there — the lowest still reading — and the window restarts a tail before it.
        for offset in [100, 108] {
            let read = Request(offset: Int64(offset), length: 8)
            await fixture.onQueue { _ = fixture.loader.accept(read) }
            let alone = try await fixture.network.range(start: offset)
            alone.answer(total: 1_024, start: offset, count: 8)
            try await fixture.until { read.finished }
        }
        #expect(fixture.loader.makeSnapshot().restarts == 1)
        let settled = Request(offset: 116, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(settled) }
        let refill = try await fixture.network.range(start: 100, occurrence: 1)
        refill.answer(total: 1_024, start: 100, count: 64)
        try await fixture.until { settled.finished }
        let sought = await fixture.onQueue { settled.bytes }
        #expect(sought == payload(start: 116, count: 8))
        #expect(fixture.loader.makeSnapshot().restarts == 2)
        #expect(fixture.loader.makeSnapshot().lastRestart?.offset == 116)
    }

    @Test("A probe of a place behind the play head is fetched on its own, and the window stays")
    func probeBehind() async throws {
        let fixture = Fixture(total: 2_048, budget: 64, tail: 16)
        defer { fixture.loader.stop() }
        for offset in [1_000, 1_008] {
            let read = Request(offset: Int64(offset), length: 8)
            await fixture.onQueue { _ = fixture.loader.accept(read) }
            let alone = try await fixture.network.range(start: offset)
            alone.answer(total: 2_048, start: offset, count: 8)
            try await fixture.until { read.finished }
        }
        let settling = Request(offset: 1_016, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(settling) }
        let fill = try await fixture.network.range(start: 1_000, occurrence: 1)
        fill.answer(total: 2_048, start: 1_000, count: 64)
        try await fixture.until { settling.finished }
        #expect(fixture.loader.makeSnapshot().restarts == 1)

        // Two reads at the middle of the file, far behind: a probe, not a seek.
        for offset in [100, 108] {
            let read = Request(offset: Int64(offset), length: 8)
            await fixture.onQueue { _ = fixture.loader.accept(read) }
            let alone = try await fixture.network.range(start: offset)
            alone.answer(total: 2_048, start: offset, count: 8)
            try await fixture.until { read.finished }
        }
        let next = Request(offset: 1_024, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(next) }
        try await fixture.until { next.finished }
        let bytes = await fixture.onQueue { next.bytes }
        #expect(bytes == payload(start: 1_024, count: 8))
        let details = fixture.loader.makeSnapshot()
        #expect(details.restarts == 1)
        #expect(details.asideBehind == 2)
        // The probe does not stop the trim either: the window still stands behind the play head.
        #expect(details.aheadBytes == 40)
    }

    @Test("A settled reader far ahead does not move the window while the play head still reads")
    func probeFarAhead() async throws {
        let fixture = Fixture(total: 2_048, budget: 64)
        defer { fixture.loader.stop() }
        let first = Request(offset: 0, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(first) }
        let fill = try await fixture.network.range(start: 0)
        fill.answer(total: 2_048, start: 0, count: 64)
        try await fixture.until { first.finished }
        let second = Request(offset: 8, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(second) }
        try await fixture.until { second.finished }

        // Two reads far ahead, as AVFoundation makes at the middle of a film: each is fetched on its
        // own, and neither moves the window away from the reader at the play head.
        let probe = Request(offset: 1_000, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(probe) }
        let alone = try await fixture.network.range(start: 1_000)
        alone.answer(total: 2_048, start: 1_000, count: 8)
        try await fixture.until { probe.finished }
        let again = Request(offset: 1_008, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(again) }
        let aside = try await fixture.network.range(start: 1_008)
        aside.answer(total: 2_048, start: 1_008, count: 8)
        try await fixture.until { again.finished }
        #expect(fixture.loader.makeSnapshot().restarts == 0)

        let third = Request(offset: 16, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(third) }
        try await fixture.until { third.finished }
        let bytes = await fixture.onQueue { third.bytes }
        #expect(bytes == payload(start: 16, count: 8))
        let details = fixture.loader.makeSnapshot()
        #expect(details.restarts == 0)
        // The probe never settled: the play head is the only reader.
        #expect(details.readers == 1)
        #expect(details.readerSpread == 0)
    }

    @Test("A forward seek restarts the window once the reader it left behind falls quiet")
    func forwardSeek() async throws {
        let fixture = Fixture(total: 2_048, budget: 64, quiet: 1)
        defer { fixture.loader.stop() }
        let first = Request(offset: 0, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(first) }
        let fill = try await fixture.network.range(start: 0)
        fill.answer(total: 2_048, start: 0, count: 64)
        try await fixture.until { first.finished }
        for offset in [8, 16] {
            let read = Request(offset: Int64(offset), length: 8)
            await fixture.onQueue { _ = fixture.loader.accept(read) }
            try await fixture.until { read.finished }
        }

        // The seek's reads are fetched on their own while the reader it left behind is still fresh,
        // even once they are more than a probe's worth.
        for offset in [1_000, 1_008, 1_016] {
            let read = Request(offset: Int64(offset), length: 8)
            await fixture.onQueue { _ = fixture.loader.accept(read) }
            let alone = try await fixture.network.range(start: offset)
            alone.answer(total: 2_048, start: offset, count: 8)
            try await fixture.until { read.finished }
        }
        #expect(fixture.loader.makeSnapshot().restarts == 0)
        #expect(fixture.loader.makeSnapshot().readers == 2)

        // Once the reader left behind has been quiet, the reader at the seek is the lowest still
        // reading, and its next read restarts the window there.
        try await Task.sleep(for: .milliseconds(1_500))
        let next = Request(offset: 1_024, length: 8)
        await fixture.onQueue { _ = fixture.loader.accept(next) }
        let refill = try await fixture.network.range(start: 1_024)
        refill.answer(total: 2_048, start: 1_024, count: 64)
        try await fixture.until { next.finished }
        let bytes = await fixture.onQueue { next.bytes }
        #expect(bytes == payload(start: 1_024, count: 8))
        let details = fixture.loader.makeSnapshot()
        #expect(details.restarts == 1)
        #expect(details.lastRestart?.offset == 1_024)
        #expect(details.readers == 1)
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
    /// Byte-scale slack for the ledger, as its own tests use: with the loader's megabytes every read
    /// here would be one reader. Behind, enough for a step back of a few reads to stay the reader.
    init(total: Int, budget: Int = 64, tail: Int64 = 0, quiet: TimeInterval = 2) {
        network = Network(total: total)
        host = UUID().uuidString.lowercased()
        Stub.register(network, host: host)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [Stub.self]
        loader = RemuxLoader(origin: URL(string: "https://\(host)/film")!, budget: budget,
                             tail: tail, lag: 32, target: 20, configuration: configuration,
                             readers: ReaderLedger(slackBehind: 64, slackAhead: 8, patience: 5),
                             quiet: quiet)
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
