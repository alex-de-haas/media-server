import Foundation

/// The small readers seen lately, told apart by where each left off.
///
/// AVFoundation reads a film with more than one reader at once, and the window must keep every one
/// of them: the one at the play head, in pieces of a megabyte or less, and another a few seconds
/// ahead of it, taking a handful of audio frames at a time. The third television run measured what
/// following only the *pending* reads does — the play-head reader is between reads most of the time,
/// so the trim followed the one ahead, threw the play head's bytes away, and every read at the play
/// head became a fetch of its own, forty megabytes behind the window.
///
/// Readers are not named by AVFoundation, so they are told apart by continuity: a read that begins
/// within a little of where a known reader stopped is that reader continuing, anything else is a new
/// one. A single read is a probe until it is followed — the end of the file is looked at once when
/// playback starts, and that must not become somewhere the window keeps. Readers not heard from for
/// a while are forgotten, so a reader AVFoundation abandoned does not pin the window for ever.
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
    /// AVFoundation re-issues for the rest of a range starts a little after the original did, so this
    /// must cover the longest read entered — `RemuxLoader.demandLimit`, a megabyte and a half.
    public let slackBehind: Int64

    /// How far past where a reader left off: the audio reader skips between bursts of frames.
    public let slackAhead: Int64

    /// How long a reader is remembered after its last read.
    public let patience: TimeInterval

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

    /// Readers that have read more than once. A single read is a probe until it is followed.
    public var settled: [Reader] {
        readers.filter { $0.reads > 1 }
    }

    /// Where the lowest settled reader last read: what the window must keep. Nil until one settles.
    public var lowest: Int64? {
        settled.map(\.last).min()
    }

    /// How far apart the settled readers are, lowest to highest. Zero with fewer than two.
    public var spread: Int64 {
        let lasts = settled.map(\.last)
        guard let low = lasts.min(), let high = lasts.max() else { return 0 }
        return high - low
    }

    /// After the window restarts for one reader, only that reader is still known to be where it was.
    public mutating func keep(only reader: Reader) {
        readers = readers.filter { $0 == reader }
    }
}
