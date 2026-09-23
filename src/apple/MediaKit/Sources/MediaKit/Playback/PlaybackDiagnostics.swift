import AVFoundation
import Foundation
import Darwin
import Observation
import os

/// Opt-in playback observations. Loaded time ranges and access logs are estimates and may lag;
/// a zero buffer estimate alone is not proof of a visible stall.
@MainActor
@Observable
public final class PlaybackDiagnostics {
    /// One reading, kept so a viewer can see what led up to a freeze rather than only its aftermath.
    public struct Sample: Identifiable, Sendable {
        public let id = UUID()
        public let at: Date
        public let position: Double

        /// Contiguous loaded-time coverage past the play head, as reported by AVPlayer.
        public let bufferAhead: Double

        public let stalls: Int
        public let observedBitrate: Double
        public let keepingUp: Bool

        /// Network bytes for the current loader or native item. Negative when unavailable.
        public let bytesTransferred: Int64

        /// Megabits per second over the recent sampling window (up to ten seconds).
        public let inflow: Double
    }

    /// What everything read at one instant — a stall, or the buffer's lowest point — kept because those
    /// are the instants nobody photographs in time. The fourth run's stalls were reported from memory.
    public struct Moment: Sendable {
        public let position: Double
        public let bufferAhead: Double

        /// The loader's figures at that instant, or nil when the player fetched for itself.
        public let window: RemuxLoader.Snapshot?
    }

    public private(set) var samples: [Sample] = []
    public private(set) var stalls = 0

    /// The most recent stall, and the buffer's lowest point, with everything else as it was then.
    public private(set) var lastStall: Moment?
    public private(set) var lastRecovery: Moment?
    private var network = PlaybackNetworkMetrics()
    public var networkMbps: Double? { network.mbps }
    public var networkBytes: Int64? { network.bytes }
    public var serverRequests: Int? { network.requests }
    public var requestsPerSecond: Double? { network.requestsPerSecond }
    /// Aggregate received bytes divided by counted GETs, not a distribution of request sizes.
    public var meanRequestBytes: Double? { network.meanRequestBytes }
    public var peakInflow: Double? { network.peakMbps }
    public private(set) var residentMB: Double = 0
    public private(set) var droppedFrames: Int?
    public private(set) var playerObservedMbps: Double?
    public private(set) var lowestMoment: Moment?

    /// When the buffer was at its lowest since playback began, and how low. A run that never dipped
    /// below a minute has a different problem from one that reached zero four times.
    public private(set) var lowestBuffer = Double.infinity
    public private(set) var lowestAt: Double = 0

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

    /// The loader feeding the player, when one is. Read once a second beside everything else, so the
    /// overlay can say what only this layer knows: how much is held, how far ahead, and how many
    /// requests actually reached the server.
    public weak var loader: RemuxLoader?
    public private(set) var windowMB: Double?
    public private(set) var aheadMB: Double?
    public private(set) var windowRestarts = 0
    public private(set) var asideFetches = 0
    public private(set) var loaderDetails: RemuxLoader.Snapshot?

    /// How many times a stuck player was re-seated. Counted where it can be seen: a remedy that runs
    /// constantly is a symptom, not a fix.
    public private(set) var recoveries = 0

    public func recovered() {
        lastRecovery = moment(at: item?.currentTime().seconds ?? 0)
        recoveries += 1
    }

    /// Kept short: this is read on a television, at a glance, while something is going wrong.
    private static let keep = 240

    private static let log = Logger(subsystem: "com.haas.mediaserver", category: "playback")

    private var timer: Timer?
    private var stallObserver: (any NSObjectProtocol)?
    private weak var item: AVPlayerItem?
    private var errorCursor = 0
    private var stallsBeforeItem = 0
    private var notifiedStalls = 0
    private var loggedStalls = 0
    private var observationID = UUID()

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

        // The error journal belongs to the item, so the cursor has to move with it. Carried over from a
        // replaced item — a track switch builds one — it would skip the new item's first entries, or
        // every one of them if its journal never grew past the old count. Failures after a switch would
        // then vanish from both the overlay and the log, which is the one thing this must not do.
        errorCursor = 0
        stallsBeforeItem = stalls
        notifiedStalls = 0
        loggedStalls = 0
        droppedFrames = nil
        playerObservedMbps = nil
        let observationID = observationID

        self.item = item
        stallObserver = NotificationCenter.default.addObserver(
            forName: AVPlayerItem.playbackStalledNotification, object: item, queue: nil
        ) { [weak self] _ in
            // The notification is posted on whatever thread noticed, which is not necessarily this
            // one. Hopping is required rather than assumed — `assumeIsolated` would trap.
            Task { @MainActor [weak self] in
                guard let self, self.observationID == observationID else { return }
                self.recordStall()
            }
        }

        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in
                guard let self, self.observationID == observationID else { return }
                self.sample()
            }
        }
        sample()
    }

    public func stop() {
        observationID = UUID()
        timer?.invalidate()
        timer = nil

        if let stallObserver {
            NotificationCenter.default.removeObserver(stallObserver)
        }

        stallObserver = nil
        item = nil
    }

    private func recordStall() {
        notifiedStalls += 1
        stalls = stallsBeforeItem + max(notifiedStalls, loggedStalls)
        let position = item?.currentTime().seconds ?? 0
        lastStall = moment(at: position)
        Self.log.warning("Playback stalled (#\(self.stalls)) at \(position, format: .fixed(precision: 1))s")
    }

    private func moment(at position: Double) -> Moment {
        let ahead = item.map { Self.bufferAhead(in: $0.loadedTimeRanges.map(\.timeRangeValue), at: position) } ?? 0
        return Moment(position: position, bufferAhead: ahead, window: loader?.makeSnapshot())
    }

    private func sample() {
        guard let item else { return }

        let position = item.currentTime().seconds
        guard position.isFinite else { return }

        let ahead = Self.bufferAhead(
            in: item.loadedTimeRanges.map(\.timeRangeValue), at: position)

        readErrors(item)
        residentMB = Self.residentMemoryMB()

        let events = item.accessLog()?.events ?? []
        let event = events.last
        let now = ProcessInfo.processInfo.systemUptime
        if let loader {
            let held = loader.makeSnapshot()
            loaderDetails = held
            network.update(source: ObjectIdentifier(loader), bytes: held.networkBytes,
                           requests: held.serverRequests, at: now)
            windowMB = Double(held.windowBytes) / 1_000_000
            aheadMB = Double(held.aheadBytes) / 1_000_000
            windowRestarts = held.restarts
            asideFetches = held.asides
        } else {
            loaderDetails = nil
            // For progressive files this is the HTTP byte-range GET count, not events.count.
            // Never substitute the player's custom-resource reads for the loader's network GETs.
            network.update(source: ObjectIdentifier(item),
                           bytes: Self.knownTotal(of: events.map(\.numberOfBytesTransferred)),
                           requests: Self.knownTotal(of: events.map(\.numberOfMediaRequests)), at: now)
            windowMB = nil
            aheadMB = nil
            windowRestarts = 0
            asideFetches = 0
        }
        droppedFrames = Self.knownTotal(of: events.map(\.numberOfDroppedVideoFrames))
        playerObservedMbps = event.flatMap { $0.observedBitrate >= 0 ? $0.observedBitrate / 1_000_000 : nil }

        // Both sources describe the same stalls; use the larger count for this item, not their sum.
        loggedStalls = max(loggedStalls, Self.knownTotal(of: events.map(\.numberOfStalls)) ?? 0)
        let combinedStalls = stallsBeforeItem + max(notifiedStalls, loggedStalls)
        if combinedStalls > stalls {
            stalls = combinedStalls
            lastStall = Moment(position: position, bufferAhead: ahead, window: loaderDetails)
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
            lowestMoment = Moment(position: position, bufferAhead: ahead, window: loaderDetails)
        }

        if let previous = samples.last {
            watched += Self.advance(from: previous.position, to: position)
        }

        samples.append(Sample(
            at: Date(), position: position, bufferAhead: ahead, stalls: stalls,
            observedBitrate: event?.observedBitrate ?? 0,
            keepingUp: item.isPlaybackLikelyToKeepUp,
            bytesTransferred: network.bytes ?? -1,
            inflow: network.mbps ?? 0))

        if samples.count > Self.keep {
            samples.removeFirst(samples.count - Self.keep)
        }
    }

    private static func residentMemoryMB() -> Double {
        var info = mach_task_basic_info()
        var count = mach_msg_type_number_t(MemoryLayout<mach_task_basic_info>.size / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &info) { pointer in
            pointer.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { words in
                task_info(mach_task_self_, task_flavor_t(MACH_TASK_BASIC_INFO), words, &count)
            }
        }
        return result == KERN_SUCCESS ? Double(info.resident_size) / 1_000_000 : 0
    }

    /// Anything new in the player's own error journal, said out loud.
    ///
    /// Read every second rather than waited for: `AVPlayerItemNewErrorLogEntry` exists, but a journal
    /// polled beside everything else needs no second delivery path and cannot miss an entry that
    /// arrived before the observer did.
    private func readErrors(_ item: AVPlayerItem) {
        guard let events = item.errorLog()?.events else { return }

        // A journal that shrank is a different journal. Trusting the old cursor would silence every
        // entry from here on, so it starts again rather than reading past the end of the new one.
        if events.count < errorCursor {
            errorCursor = 0
        }

        guard events.count > errorCursor else { return }

        for event in events[errorCursor...] {
            let status = event.errorStatusCode
            let domain = event.errorDomain
            let comment = event.errorComment ?? "no comment"
            Self.log.error(
                "Player error: \(domain, privacy: .public) \(status) — \(comment, privacy: .public)")

            // The comment is kept whatever the status. It is the half that says what happened —
            // "Playlist File unchanged", "Connection lost" — and a bare domain and number on a
            // television is something to photograph and look up rather than something to read.
            lastError = status == 0 ? "\(domain): \(comment)" : "\(domain) \(status): \(comment)"
        }

        errors += events.count - errorCursor
        errorCursor = events.count
    }

    /// Merge touching/overlapping ranges before measuring coverage. A boundary shared by two
    /// ranges is not an empty buffer. Do not bridge actual gaps or count disconnected future data.
    nonisolated static func bufferAhead(in ranges: [CMTimeRange], at position: Double) -> Double {
        guard position.isFinite else { return 0 }
        let intervals = ranges.compactMap { range -> (start: Double, end: Double)? in
            let start = range.start.seconds
            let end = CMTimeRangeGetEnd(range).seconds
            guard start.isFinite, end.isFinite, end >= start else { return nil }
            return (start, end)
        }.sorted { $0.start < $1.start }
        var end = position
        for interval in intervals {
            if interval.start > end { break }
            if interval.end > end { end = interval.end }
        }
        return end - position
    }

    /// A missing or partially unknown journal cannot provide a complete total. In particular,
    /// unavailable request counts must not appear as zero, or produce a misleading bytes/GET ratio.
    nonisolated static func knownTotal<T: FixedWidthInteger>(of values: [T]) -> T? {
        guard !values.isEmpty, values.allSatisfy({ $0 >= 0 }) else { return nil }
        var total: T = 0
        for value in values {
            let sum = total.addingReportingOverflow(value)
            guard !sum.overflow else { return nil }
            total = sum.partialValue
        }
        return total
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

    /// Receive rate over the recent sampling window; native access-log updates may be delayed.
    public var inflow: Double { samples.last?.inflow ?? 0 }

    /// Received network gigabytes for the current source. Includes prefetch and repeat reads.
    public var transferredGB: Double {
        Double(max(samples.last?.bytesTransferred ?? 0, 0)) / 1_000_000_000
    }

    public var bufferAhead: Double { samples.last?.bufferAhead ?? 0 }
    public var position: Double { samples.last?.position ?? 0 }
    public var keepingUp: Bool { samples.last?.keepingUp ?? true }
}

/// Network readings share one calculation in native, RAM and disk modes. Totals belong to the
/// current source (native item or loader); replacing it resets the baseline and peak. Recovery on
/// the same loader preserves its counters. Rates span up to ten seconds to smooth bursty updates.
struct PlaybackNetworkMetrics: Sendable {
    private struct Reading: Sendable {
        let at: TimeInterval
        let bytes: Int64?
        let requests: Int?
    }
    private var source: ObjectIdentifier?
    private var readings: [Reading] = []
    private(set) var bytes: Int64?
    private(set) var requests: Int?
    private(set) var mbps: Double?
    private(set) var requestsPerSecond: Double?
    private(set) var peakMbps: Double?

    var meanRequestBytes: Double? {
        guard let bytes, let requests, requests > 0 else { return nil }
        return Double(bytes) / Double(requests)
    }

    mutating func update(source: ObjectIdentifier, bytes: Int64?, requests: Int?, at: TimeInterval) {
        let bytes = bytes.flatMap { $0 >= 0 ? $0 : nil }
        let requests = requests.flatMap { $0 >= 0 ? $0 : nil }
        let regressed = (bytes != nil && self.bytes != nil && bytes! < self.bytes!)
            || (requests != nil && self.requests != nil && requests! < self.requests!)
        if self.source != source || regressed || at <= (readings.last?.at ?? -.infinity) {
            readings.removeAll(keepingCapacity: true)
            peakMbps = nil
        }
        self.source = source
        self.bytes = bytes
        self.requests = requests
        readings.append(Reading(at: at, bytes: bytes, requests: requests))
        while readings.count > 1, readings[0].at < at - 10 { readings.removeFirst() }
        mbps = nil
        requestsPerSecond = nil
        if let first = readings.first, at > first.at {
            if let bytes, let before = first.bytes, readings.allSatisfy({ $0.bytes != nil }) {
                mbps = PlaybackDiagnostics.rate(bytes: bytes - before, over: at - first.at)
                peakMbps = max(peakMbps ?? 0, mbps ?? 0)
            }
            if let requests, let before = first.requests, readings.allSatisfy({ $0.requests != nil }) {
                requestsPerSecond = Double(max(0, requests - before)) / (at - first.at)
            }
        }
    }
}
