import Foundation

/// A contiguous run of a resource's bytes, held in memory ahead of whoever is reading it.
///
/// Kept as the chunks it arrived in rather than one growing buffer: the front is dropped as the play
/// head moves on, and dropping a chunk costs nothing where trimming a single `Data` would copy
/// everything behind it — a hundred megabytes, once a second, for the length of a film. Dropped
/// chunks are skipped by index rather than removed one at a time, and the array is compacted only
/// once the dead part outweighs the live one.
///
/// Pure value, so every rule about what it holds is testable without a player or a network.
public struct ByteWindow: Sendable {
    /// Where an offset stands relative to what is held.
    public enum Placement: Equatable, Sendable {
        /// Readable now.
        case held

        /// Past the end, but within a budget of it: filling forward will arrive there.
        case ahead

        /// Below the start, but not by much: a reader that lags behind the one the window follows.
        /// The window cannot grow backwards, so this is fetched on its own rather than waited for —
        /// a request left pending here would be pending for ever.
        case behind

        /// Anywhere else. A seek: the window restarts there.
        case away
    }

    private var chunks: [Data] = []
    private var head = 0

    /// Offset in the resource of the first byte held.
    public private(set) var start: Int64

    /// How many bytes are held.
    public private(set) var count = 0

    /// How many the window may hold before filling stops.
    public let budget: Int

    public init(start: Int64, budget: Int) {
        self.start = start
        self.budget = budget
    }

    /// One past the last byte held.
    public var end: Int64 { start + Int64(count) }

    public var isFull: Bool { count >= budget }

    /// Bytes still allowed in before the budget is reached.
    public var room: Int { max(0, budget - count) }

    public func holds(_ offset: Int64) -> Bool {
        offset >= start && offset < end
    }

    /// - Parameter lag: how far below the start still counts as a reader that lags rather than a seek.
    public func place(_ offset: Int64, lag: Int64) -> Placement {
        if holds(offset) { return .held }
        if offset >= end && offset < end + Int64(budget) { return .ahead }
        if offset < start && offset >= start - lag { return .behind }
        return .away
    }

    /// Everything held from `offset` onwards, up to `limit` bytes. Nil when `offset` is not held at
    /// all; possibly shorter than `limit` when the window has not yet filled that far.
    public func read(from offset: Int64, upTo limit: Int) -> Data? {
        guard holds(offset), limit > 0 else { return nil }

        var skip = Int(offset - start)
        var remaining = min(limit, Int(end - offset))
        var out = Data(capacity: remaining)

        for chunk in chunks[head...] {
            if skip >= chunk.count {
                skip -= chunk.count
                continue
            }

            let take = min(chunk.count - skip, remaining)
            let from = chunk.startIndex + skip
            out.append(chunk.subdata(in: from ..< from + take))
            remaining -= take
            skip = 0

            if remaining == 0 {
                break
            }
        }

        return out
    }

    public mutating func append(_ chunk: Data) {
        guard !chunk.isEmpty else { return }
        chunks.append(chunk)
        count += chunk.count
    }

    /// Drops whole chunks that end at or before `offset`. Never splits a chunk: a few hundred
    /// kilobytes kept longer than necessary is cheaper than copying to be exact.
    public mutating func trim(keepingFrom offset: Int64) {
        while head < chunks.count, start + Int64(chunks[head].count) <= offset {
            start += Int64(chunks[head].count)
            count -= chunks[head].count
            head += 1
        }

        // Compacted only when the dead part outweighs the live one, so the copy is rare and small.
        if head > 64, head * 2 > chunks.count {
            chunks.removeFirst(head)
            head = 0
        }
    }

    /// Forgets everything and begins again at `offset`. What a seek does.
    public mutating func restart(at offset: Int64) {
        chunks.removeAll()
        head = 0
        count = 0
        start = offset
    }
}
