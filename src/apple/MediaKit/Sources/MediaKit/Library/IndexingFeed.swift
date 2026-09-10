import Foundation
import MediaServerAPI
import Observation

/// Revisioned preparation state shared by snapshots and SSE; unknown states remain harmless.
public struct IndexingStatus: Codable, Equatable, Sendable {
    public let state: String
    public let percent: Int?
    public let revision: Int64

    public var label: String? {
        switch state {
        case "waiting": "Waiting for indexing"
        case "indexing": percent.map { "Indexing · \(max(0, min(99, $0)))%" } ?? "Indexing"
        case "saving": "Saving index"
        case "failed": "Indexing could not finish"
        default: nil
        }
    }

    init(_ dto: Components.Schemas.IndexingStatus) {
        state = dto.state
        percent = dto.percent.map(Int.init)
        revision = dto.revision
    }

    public init(state: String, percent: Int?, revision: Int64) {
        self.state = state; self.percent = percent; self.revision = revision
    }
}

struct IndexingUpdate: Decodable, Sendable {
    let itemId: String
    let sourceId: String
    let streamId: String?
    let indexing: IndexingStatus
}

/// Incremental SSE parser. Frames may span network chunks; comments and unrelated events are ignored.
struct IndexingFrameParser {
    private var line: [UInt8] = []
    private var event = ""
    private var data: [String] = []
    private var frameBytes = 0

    mutating func push(_ byte: UInt8) throws -> IndexingUpdate? {
        frameBytes += 1
        guard frameBytes <= 65_536 else { throw URLError(.dataLengthExceedsMaximum) }
        if byte != 10 { line.append(byte); return nil }
        let text = String(decoding: line, as: UTF8.self).trimmingCharacters(in: .newlines)
        line.removeAll(keepingCapacity: true)
        if text.isEmpty {
            defer { event = ""; data.removeAll(keepingCapacity: true); frameBytes = 0 }
            guard event == "indexingChanged" else { return nil }
            return try? JSONDecoder().decode(IndexingUpdate.self, from: Data(data.joined(separator: "\n").utf8))
        }
        if text.hasPrefix("event:") { event = String(text.dropFirst(6)).trimmingCharacters(in: .whitespaces) }
        if text.hasPrefix("data:") {
            let value = String(text.dropFirst(5))
            data.append(value.hasPrefix(" ") ? String(value.dropFirst()) : value)
        }
        return nil
    }
}

/// One authenticated connection per server session, shared by visible detail screens.
@MainActor @Observable
public final class IndexingFeed {
    public private(set) var connected = false
    private var values: [String: IndexingStatus] = [:]
    @ObservationIgnored private weak var session: ServerSession?
    @ObservationIgnored private let configuration: URLSessionConfiguration
    @ObservationIgnored private var task: Task<Void, Never>?
    @ObservationIgnored private var listeners: [UUID: AsyncStream<Bool>.Continuation] = [:]

    init(session: ServerSession, configuration: URLSessionConfiguration = .ephemeral) {
        self.session = session
        self.configuration = configuration
    }

    public func status(for id: String, snapshot: IndexingStatus?) -> IndexingStatus? {
        guard let live = values[id.lowercased()], live.revision > (snapshot?.revision ?? -1) else { return snapshot }
        return live
    }

    func apply(_ update: IndexingUpdate) {
        let id = (update.streamId ?? update.sourceId).lowercased()
        guard update.indexing.revision > (values[id]?.revision ?? -1) else { return }
        values[id] = update.indexing
        if values.count > 512, let oldest = values.min(by: { $0.value.revision < $1.value.revision }) {
            values.removeValue(forKey: oldest.key)
        }
    }

    /// Reconnect notifications request a fresh snapshot. Termination releases this screen's subscription.
    public func connections() -> AsyncStream<Bool> {
        let id = UUID()
        let (stream, continuation) = AsyncStream<Bool>.makeStream(bufferingPolicy: .bufferingNewest(1))
        listeners[id] = continuation
        continuation.yield(connected)
        continuation.onTermination = { [weak self] _ in
            Task { @MainActor in self?.remove(id) }
        }
        if task == nil { task = Task { [weak self] in await self?.run() } }
        return stream
    }

    private func remove(_ id: UUID) {
        listeners.removeValue(forKey: id)
        if listeners.isEmpty { task?.cancel(); task = nil; connected = false }
    }

    private func run() async {
        configuration.timeoutIntervalForRequest = 45
        configuration.timeoutIntervalForResource = 24 * 60 * 60
        let network = URLSession(configuration: configuration)
        defer { network.invalidateAndCancel() }
        var delay: UInt64 = 1
        while !Task.isCancelled {
            let started = ContinuousClock.now
            do {
                guard let session, !session.credentialLost else { break }
                let bytes = try await session.eventBytes(using: network)
                try Task.checkCancellation()
                connected = true
                for listener in listeners.values { listener.yield(true) }
                var parser = IndexingFrameParser()
                for try await byte in bytes {
                    try Task.checkCancellation()
                    if let update = try parser.push(byte) { apply(update) }
                }
            } catch { /* Keep the snapshot, visibly stale, until reconnection succeeds. */ }
            if Task.isCancelled { return }
            if ContinuousClock.now - started >= .seconds(30) { delay = 1 }
            connected = false
            for listener in listeners.values { listener.yield(false) }
            do { try await Task.sleep(nanoseconds: delay * 1_000_000_000) } catch { return }
            delay = min(delay * 2, 30)
        }
    }
}
