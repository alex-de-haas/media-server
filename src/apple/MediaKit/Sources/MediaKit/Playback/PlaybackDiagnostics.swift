import AVFoundation
import Foundation
import Observation
import os

/// What playback is actually doing, sampled from the player rather than guessed at.
///
/// It exists because the difference between "the picture stutters" and a cause is a number nobody had:
/// a freeze with the buffer empty is starvation, a freeze with data in hand is not, and the two want
/// opposite fixes. Diagnosing this from a Mac took a purpose-built harness and still could not
/// reproduce what a television did — the machine that has the problem is the one that has to be asked.
///
/// Most of this comes from `AVPlayerItemAccessLog`, which is Apple's own instrumentation: `numberOfStalls`
/// is authoritative in a way a timer sampling `isPlaybackBufferEmpty` is not, and `observedBitrate` is
/// what the player thinks it is getting rather than what a `curl` measured once.
@MainActor
@Observable
public final class PlaybackDiagnostics {
    /// One reading, kept so a viewer can see what led up to a freeze rather than only its aftermath.
    public struct Sample: Identifiable, Sendable {
        public let id = UUID()
        public let at: Date
        public let position: Double

        /// Seconds of media already loaded past the play head. This is the number that predicts a
        /// freeze: it falls at one second per second whenever the player has stopped fetching.
        public let bufferAhead: Double

        public let stalls: Int
        public let observedBitrate: Double
        public let keepingUp: Bool

        /// Bytes taken from the server since playback began: every access-log event added up, not the
        /// newest one's counter. Negative where the player has no figure to give at all.
        public let bytesTransferred: Int64

        /// Megabits per second that arrived since the previous reading — measured here by subtraction
        /// rather than taken from the player's own estimate.
        public let inflow: Double
    }

    public private(set) var samples: [Sample] = []
    public private(set) var stalls = 0

    /// When the buffer was at its lowest since playback began, and how low. A run that never dipped
    /// below a minute has a different problem from one that reached zero four times.
    public private(set) var lowestBuffer = Double.infinity
    public private(set) var lowestAt: Double = 0

    /// The fastest second of the whole session.
    ///
    /// This is the reading that settles the argument a flat buffer cannot. A buffer parked at two
    /// seconds means bytes arrive at exactly the rate they are consumed — true whether the path cannot
    /// go faster or the player has decided not to ask. A peak far above what the film needs says the
    /// path was never the limit.
    public private(set) var peakInflow: Double = 0

    /// Seconds of film actually played this session.
    ///
    /// Not the position: a resume starts an hour in, and dividing the bytes this session fetched by an
    /// hour of media nobody fetched understates the cost by however far in the viewer resumed. Seeks are
    /// not watching either, so only an advance small enough to be ordinary playback is counted.
    public private(set) var watched: Double = 0

    /// How long the server took to say what to play, and how long the player then took to show it.
    ///
    /// Ten seconds to first frame on the Apple TV against three on a Mac, and the argument about why
    /// has run on assertions: the tables, the round trips, the tunnel. These two numbers divide it in
    /// the only place it can be divided — before the URL existed, and after.
    public private(set) var resolveSeconds: Double?
    public private(set) var openSeconds: Double?

    private var opened: Date?
    private var openedFrom: Double = 0

    /// What AVFoundation says went wrong, from a journal nobody here had ever opened.
    ///
    /// `AVPlayerItemErrorLog` records the failures a player survives — a connection dropped, a request
    /// refused, a response that stopped — with the HTTP status and the domain behind it. None of them
    /// reach `AVPlayerItem.status`, which stays `readyToPlay` throughout, so a player that quietly
    /// stopped asking for anything looks from the outside exactly like a healthy one.
    public private(set) var lastError: String?
    public private(set) var errors = 0

    /// Kept short: this is read on a television, at a glance, while something is going wrong.
    private static let keep = 240

    private static let log = Logger(subsystem: "com.haas.mediaserver", category: "playback")

    private var timer: Timer?
    private var stallObserver: (any NSObjectProtocol)?
    private weak var item: AVPlayerItem?

    public init() {}

    /// How long the server took to answer, noted by whoever asked it.
    public func resolved(after seconds: Double) {
        resolveSeconds = seconds
    }

    /// - Parameter from: where playback was asked to begin. A resume seeks there **before** the first
    ///   frame appears, so a play head merely sitting at a non-zero position is not the film starting —
    ///   and taking it for one would report a resumed film as opening instantly, which is precisely the
    ///   film somebody would be timing.
    public func start(observing item: AVPlayerItem, from: Double = 0) {
        // Starting twice would leave the first timer and observer running, doubling every count.
        stop()

        // Only the first item counts. Switching a dub builds another one, and the number a viewer
        // cares about is how long the film took to appear, not how long a change to it took.
        if opened == nil {
            opened = Date()
            openedFrom = from
        }

        self.item = item
        stallObserver = NotificationCenter.default.addObserver(
            forName: AVPlayerItem.playbackStalledNotification, object: item, queue: nil
        ) { [weak self] _ in
            // The notification is posted on whatever thread noticed, which is not necessarily this
            // one. Hopping is required rather than assumed — `assumeIsolated` would trap.
            Task { @MainActor [weak self] in self?.recordStall() }
        }

        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in self?.sample() }
        }
    }

    public func stop() {
        timer?.invalidate()
        timer = nil

        if let stallObserver {
            NotificationCenter.default.removeObserver(stallObserver)
        }

        stallObserver = nil
        item = nil
    }

    private func recordStall() {
        stalls += 1
        let position = item?.currentTime().seconds ?? 0
        Self.log.warning("Playback stalled (#\(self.stalls)) at \(position, format: .fixed(precision: 1))s")
    }

    private func sample() {
        guard let item else { return }

        let position = item.currentTime().seconds
        guard position.isFinite else { return }

        let ahead = Self.bufferAhead(
            in: item.loadedTimeRanges.map(\.timeRangeValue), at: position)

        readErrors(item)

        // The access log's own stall count, which counts what the notification can miss.
        let event = item.accessLog()?.events.last
        let logged = event?.numberOfStalls ?? -1
        if logged > stalls {
            stalls = logged
        }

        // The first frame is the first moment the play head has **moved from where it was asked to
        // start**: `readyToPlay` says the player could begin, which is not the same as a viewer seeing
        // anything, and a resume's seek puts the head at its destination before anything is shown. A
        // quarter of a second is past any landing jitter and well inside one sample.
        if openSeconds == nil, let opened, Self.hasStarted(at: position, from: openedFrom) {
            openSeconds = Date().timeIntervalSince(opened)
        }

        if ahead < lowestBuffer, watched > 5 {
            lowestBuffer = ahead
            lowestAt = position
        }

        // Every event added up rather than the last one's counter: `numberOfBytesTransferred` is per
        // event, so reading only the newest makes the session total collapse each time AVFoundation
        // opens another one — and the subtraction below would come out negative exactly there.
        let now = Date()
        let transferred = item.accessLog().map { Self.total(of: $0.events.map(\.numberOfBytesTransferred)) } ?? -1

        var inflow: Double = 0
        if transferred >= 0, let previous = samples.last, previous.bytesTransferred >= 0 {
            inflow = Self.rate(
                bytes: transferred - previous.bytesTransferred,
                over: now.timeIntervalSince(previous.at))
        }

        peakInflow = max(peakInflow, inflow)

        if let previous = samples.last {
            watched += Self.advance(from: previous.position, to: position)
        }

        samples.append(Sample(
            at: now, position: position, bufferAhead: ahead, stalls: stalls,
            observedBitrate: event?.observedBitrate ?? 0,
            keepingUp: item.isPlaybackLikelyToKeepUp,
            bytesTransferred: transferred,
            inflow: inflow))

        if samples.count > Self.keep {
            samples.removeFirst(samples.count - Self.keep)
        }
    }

    /// Anything new in the player's own error journal, said out loud.
    ///
    /// Read every second rather than waited for: `AVPlayerItemNewErrorLogEntry` exists, but a journal
    /// polled beside everything else needs no second delivery path and cannot miss an entry that
    /// arrived before the observer did.
    private func readErrors(_ item: AVPlayerItem) {
        guard let events = item.errorLog()?.events, events.count > errors else { return }

        for event in events[errors...] {
            let status = event.errorStatusCode
            let domain = event.errorDomain
            let comment = event.errorComment ?? "no comment"
            Self.log.error(
                "Player error: \(domain, privacy: .public) \(status) — \(comment, privacy: .public)")
            lastError = status == 0 ? "\(domain): \(comment)" : "\(domain) \(status)"
        }

        errors = events.count
    }

    /// How much media is loaded past the play head.
    ///
    /// The range **containing the position**, not the first one. After a seek the player keeps what it
    /// had already fetched, so the first range is often an earlier stretch of the film — and measuring
    /// from its end gives a negative number, which would read as starvation exactly when the buffer is
    /// healthy. That is the one reading this overlay exists to get right.
    /// Pure arithmetic over what the player reported, so it is testable without one.
    nonisolated static func bufferAhead(in ranges: [CMTimeRange], at position: Double) -> Double {
        for range in ranges {
            let start = CMTimeGetSeconds(range.start)
            let end = start + CMTimeGetSeconds(range.duration)
            if position >= start && position <= end {
                return end - position
            }
        }

        // Nothing covers the play head: whatever is loaded is somewhere else, and there is no buffer
        // ahead of where the viewer is.
        return 0
    }

    /// Bytes taken across the whole session, from each access-log event's own counter.
    ///
    /// The player keeps a **counter per event** and opens a new one whenever the connection is
    /// re-established, so the newest event's figure is not the session's. An event with no figure to
    /// give reports a negative and contributes nothing rather than subtracting.
    nonisolated static func total(of perEvent: [Int64]) -> Int64 {
        perEvent.reduce(0) { $0 + max($1, 0) }
    }

    /// How much of the film the last second of wall clock actually played.
    ///
    /// A seek moves the position by minutes without anybody having watched them, and a backward one
    /// moves it the wrong way entirely. Only an advance that could plausibly be ordinary playback counts
    /// — the readings are a second apart, so anything beyond a couple of seconds is a jump.
    nonisolated static func advance(from: Double, to: Double) -> Double {
        let moved = to - from
        return moved > 0 && moved <= 2 ? moved : 0
    }

    /// Whether the play head has moved from where playback was asked to begin.
    ///
    /// A quarter of a second is past any landing jitter from the seek and well inside the one-second
    /// gap between readings, so a film that opens promptly is not reported as opening a beat late.
    nonisolated static func hasStarted(at position: Double, from: Double) -> Bool {
        position > from + 0.25
    }

    /// Megabits per second, from the bytes that arrived between two readings.
    ///
    /// Megabits and not mebibits, because that is the unit every speed test and every network interface
    /// is quoted in, and a diagnostic that has to be converted before it can be compared is one that
    /// gets compared wrongly.
    nonisolated static func rate(bytes: Int64, over seconds: TimeInterval) -> Double {
        guard bytes > 0, seconds > 0 else { return 0 }
        return Double(bytes) * 8 / seconds / 1_000_000
    }

    /// Megabits per second the player believes it is receiving. Zero until it has an opinion.
    public var observedMbps: Double {
        (samples.last?.observedBitrate ?? 0) / 1_000_000
    }

    /// What arrived in the last second, measured here rather than estimated by the player.
    public var inflow: Double { samples.last?.inflow ?? 0 }

    /// Gigabytes taken from the server so far. Against the seconds actually watched this is the film's
    /// real cost per second — which for a container carrying every track is not the chosen tracks'
    /// bitrate.
    public var transferredGB: Double {
        Double(max(samples.last?.bytesTransferred ?? 0, 0)) / 1_000_000_000
    }

    public var bufferAhead: Double { samples.last?.bufferAhead ?? 0 }
    public var position: Double { samples.last?.position ?? 0 }
    public var keepingUp: Bool { samples.last?.keepingUp ?? true }
}
