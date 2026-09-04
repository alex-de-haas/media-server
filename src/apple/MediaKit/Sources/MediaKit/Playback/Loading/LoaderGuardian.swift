import AVFoundation
import Foundation
import os

/// Watches a player fed by `RemuxLoader`, and says when it has stopped asking with the answer in hand.
///
/// Once a second it reads the two things that both stop when a player wedges — where the play head
/// is, and how much has been handed over — and one thing only the loader knows: whether bytes are
/// being held that the player has not asked for. `WedgeDetector` decides; this only reads and reports.
/// What to do about it belongs to whoever owns the player.
@MainActor
public final class LoaderGuardian {
    private static let log = Logger(subsystem: "com.haas.mediaserver", category: "loader")

    private var timer: Timer?
    private var detector: WedgeDetector
    private weak var player: AVPlayer?
    private var loader: RemuxLoader?
    private var onWedged: ((Int) -> Void)?

    /// How many times this has fired for the film being watched. Shown where it can be seen: a
    /// remedy that runs constantly is a symptom, not a fix.
    public private(set) var recoveries = 0

    public init(patience: Int = 6) {
        detector = WedgeDetector(patience: patience)
    }

    public func start(watching player: AVPlayer, fedBy loader: RemuxLoader, onWedged: @escaping (Int) -> Void) {
        stop()
        self.player = player
        self.loader = loader
        self.onWedged = onWedged

        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in self?.check() }
        }
    }

    /// The player was re-seated: the next reading is a baseline, not a comparison.
    public func restarted() {
        detector.restarted()
    }

    public func stop() {
        timer?.invalidate()
        timer = nil
        player = nil
        loader = nil
        onWedged = nil
        detector.restarted()
    }

    private func check() {
        guard let player, let loader, let item = player.currentItem else { return }

        let position = item.currentTime().seconds
        guard position.isFinite else { return }

        // What the player already holds ahead is the meter on open-ended delivery, and this is the
        // one place that can read it.
        loader.playerHolds(seconds: PlaybackDiagnostics.bufferAhead(
            in: item.loadedTimeRanges.map(\.timeRangeValue), at: position))

        let snapshot = loader.makeSnapshot()
        let reading = WedgeDetector.Reading(
            position: position,
            delivered: snapshot.delivered,
            heldAhead: snapshot.aheadBytes,
            paused: player.timeControlStatus == .paused)

        guard detector.observe(reading) else { return }

        recoveries += 1
        Self.log.warning(
            "Player wedged at \(position, format: .fixed(precision: 1))s with \(snapshot.aheadBytes) bytes in hand (attempt \(self.recoveries))")
        onWedged?(recoveries)
    }
}
