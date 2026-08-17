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
    }

    public private(set) var samples: [Sample] = []
    public private(set) var stalls = 0

    /// When the buffer was at its lowest since playback began, and how low. A run that never dipped
    /// below a minute has a different problem from one that reached zero four times.
    public private(set) var lowestBuffer = Double.infinity
    public private(set) var lowestAt: Double = 0

    /// Kept short: this is read on a television, at a glance, while something is going wrong.
    private static let keep = 240

    private static let log = Logger(subsystem: "com.haas.mediaserver", category: "playback")

    private var timer: Timer?
    private weak var item: AVPlayerItem?

    public init() {}

    public func start(observing item: AVPlayerItem) {
        self.item = item
        NotificationCenter.default.addObserver(
            self, selector: #selector(stalled),
            name: AVPlayerItem.playbackStalledNotification, object: item)

        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.sample() }
        }
    }

    public func stop() {
        timer?.invalidate()
        timer = nil
        NotificationCenter.default.removeObserver(self)
    }

    @objc private func stalled() {
        MainActor.assumeIsolated {
            stalls += 1
            let position = item?.currentTime().seconds ?? 0
            Self.log.warning("Playback stalled (#\(self.stalls)) at \(position, format: .fixed(precision: 1))s")
        }
    }

    private func sample() {
        guard let item else { return }

        let position = item.currentTime().seconds
        guard position.isFinite else { return }

        let loaded = item.loadedTimeRanges.first?.timeRangeValue
        let ahead = loaded.map {
            CMTimeGetSeconds($0.start) + CMTimeGetSeconds($0.duration) - position
        } ?? 0

        // The access log's own stall count, which counts what the notification can miss.
        let event = item.accessLog()?.events.last
        let logged = event?.numberOfStalls ?? -1
        if logged > stalls {
            stalls = logged
        }

        if ahead < lowestBuffer, position > 5 {
            lowestBuffer = ahead
            lowestAt = position
        }

        samples.append(Sample(
            at: Date(), position: position, bufferAhead: ahead, stalls: stalls,
            observedBitrate: event?.observedBitrate ?? 0,
            keepingUp: item.isPlaybackLikelyToKeepUp))

        if samples.count > Self.keep {
            samples.removeFirst(samples.count - Self.keep)
        }
    }

    /// Megabits per second the player believes it is receiving. Zero until it has an opinion.
    public var observedMbps: Double {
        (samples.last?.observedBitrate ?? 0) / 1_000_000
    }

    public var bufferAhead: Double { samples.last?.bufferAhead ?? 0 }
    public var position: Double { samples.last?.position ?? 0 }
    public var keepingUp: Bool { samples.last?.keepingUp ?? true }
}
