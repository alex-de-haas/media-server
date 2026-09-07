import Foundation

/// The readers seen lately, told apart by where each left off.
///
/// AVFoundation reads a film with more than one reader at once, and the window must keep every one
/// of them: the one at the play head, another a few seconds ahead of it taking audio frames, and a
/// speculative one farther ahead in bigger pieces. The third television run measured what following
/// only the *pending* reads does — the play-head reader is between reads most of the time, so the
/// trim followed the one ahead, threw the play head's bytes away, and every read at the play head
/// became a fetch of its own, forty megabytes behind the window. The fifth run measured what telling
/// readers apart by the size of their reads does — on a film where the play head reads in pieces of
/// two to four megabytes it was not a reader at all, and the window followed the audio again. So
/// every bounded read is entered, whatever its size, and the window keeps behind the lowest reader.
///
/// Readers are not named by AVFoundation, so they are told apart by continuity: a read that begins
/// within a little of where a known reader stopped is that reader continuing, anything else is a new
/// one. A reader is a probe until it has read more times than a probe does — the end of the file is
/// looked at once when playback starts, and the exact middle twice, at the start and after every
/// re-seat — and the window never goes to a probe. It does keep behind one, as behind any reader it
/// can serve: the bytes a reader just took are next to the bytes it takes next, and whether it has
/// read three times yet says nothing about that. Readers not heard from for a while are forgotten,
/// so a reader AVFoundation abandoned does not pin the window for ever.
///
/// Pure value, so the rules are testable without a player or a network.
public struct ReaderLedger: Sendable {
    public struct Reader: Sendable, Equatable {
        /// Where its latest read began: the lowest byte it still wants.
        public var last: Int64

        /// Where that read ended: where the next is expected to begin, give or take.
        public var next: Int64

        public var reads: Int
        public var seen: TimeInterval
    }

    public private(set) var readers: [Reader] = []

    /// How far before where a reader left off a read may begin and still be that reader: a request
    /// AVFoundation re-issues for the rest of a range starts a little after the original did, and a
    /// seek settles by a step or two *backwards* from its target, so this covers the reads seen at
    /// the play head. A bigger read re-issued becomes a reader of its own for a moment, which costs
    /// nothing: the window keeps behind it from its first read, it settles as it reads on, and the
    /// one it left behind is forgotten in `patience`.
    public let slackBehind: Int64

    /// How far past where a reader left off: the audio reader skips between bursts of frames.
    public let slackAhead: Int64

    /// How long a reader is remembered after its last read.
    public let patience: TimeInterval

    /// How many reads AVFoundation's probe of a far place makes: measured on the fifth television run
    /// as two of sixty-four kilobytes at the exact middle of the file. A reader with no more reads
    /// than that has not shown itself to be reading; a seek shows itself by reading on.
    public static let probeReads = 2

    public init(slackBehind: Int64 = 4 << 20, slackAhead: Int64 = 8 << 20, patience: TimeInterval = 5) {
        self.slackBehind = slackBehind
        self.slackAhead = slackAhead
        self.patience = patience
    }

    /// Records a read. Continuing a known reader advances it; anything else begins a new one.
    public mutating func observe(offset: Int64, length: Int, at now: TimeInterval) {
        let continued = readers.indices
            .filter { offset >= readers[$0].next - slackBehind && offset <= readers[$0].next + slackAhead }
            .min { abs(readers[$0].next - offset) < abs(readers[$1].next - offset) }

        if let index = continued {
            readers[index].last = offset
            readers[index].next = offset + Int64(length)
            readers[index].reads += 1
            readers[index].seen = now
        } else {
            readers.append(Reader(last: offset, next: offset + Int64(length), reads: 1, seen: now))
        }
    }

    /// Forgets readers not heard from within `patience`.
    public mutating func expire(at now: TimeInterval) {
        readers.removeAll { now - $0.seen > patience }
    }

    /// Readers that have read more times than a probe does: the ones the window keeps behind and may
    /// move for.
    public var settled: [Reader] {
        readers.filter { $0.reads > Self.probeReads }
    }

    /// Where the lowest settled reader last read: what the window must keep. Nil until one settles.
    public var lowest: Int64? {
        settled.map(\.last).min()
    }

    /// The lowest reader of any age at or above `floor`: what the window must keep of what it can
    /// still serve. Probes count here — the play head at the start of a film is one until its third
    /// read, and its bytes are the last the window should throw away. A reader far below the
    /// window's start is either about to restart the window or a probe of a place long passed — the
    /// middle of the film, looked at twice after a re-seat — and neither should stop the trim behind
    /// the readers the window does hold.
    public func lowest(atOrAbove floor: Int64) -> Int64? {
        readers.map(\.last).filter { $0 >= floor }.min()
    }

    /// How far apart the settled readers are, lowest to highest. Zero with fewer than two.
    public var spread: Int64 {
        let lasts = settled.map(\.last)
        guard let low = lasts.min(), let high = lasts.max() else { return 0 }
        return high - low
    }

    /// The readers of any age still reading: heard from within `quiet`, or with their latest read
    /// among `waiting` — the offsets of reads not yet answered, since a reader kept waiting on a
    /// slow disk is reading however long ago it asked. A reader still reading from the window holds
    /// it there, settled or not.
    public func reading(at now: TimeInterval, quiet: TimeInterval, waiting: Set<Int64> = []) -> [Reader] {
        readers.filter { now - $0.seen <= quiet || waiting.contains($0.last) }
    }

    /// The settled readers still reading: the ones that may move the window, and the ones counted.
    public func active(at now: TimeInterval, quiet: TimeInterval, waiting: Set<Int64> = []) -> [Reader] {
        reading(at: now, quiet: quiet, waiting: waiting).filter { $0.reads > Self.probeReads }
    }

    /// After the window restarts for one reader, only that reader is still known to be where it was.
    public mutating func keep(only reader: Reader) {
        readers = readers.filter { $0 == reader }
    }
}
