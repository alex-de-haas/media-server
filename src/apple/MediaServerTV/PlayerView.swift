import AVKit
import MediaKit
import SwiftUI

/// `AVPlayerViewController`, not a player of our own.
///
/// Recorded as a decision in the epic and still the right one: the transport bar, the skip gestures, the
/// track picker and the Siri remote's whole vocabulary come free and cannot be reimplemented to the same
/// standard.
///
/// The one thing it cannot do here is choose a track. The container carries a single dub and a single
/// subtitle — that is what made the header small enough to play at all — so AVKit's own picker has
/// nothing to choose between. Ours goes in the transport bar beside it, and switching means asking the
/// server for the same film with a different track and re-seating the player where the viewer was: a
/// second of interruption, against a film that plays.
///
/// A bare `AVPlayerLayer` lived here for one measurement, to find out whether AVKit's scrubbing
/// filmstrip was reading the film. It was not — the re-reading was our own `preferredForwardBufferDuration`
/// — so the alternative is gone rather than left where somebody could switch to a player with no
/// transport bar and no Dolby Vision.
struct PlayerView: UIViewControllerRepresentable {
    let stream: PlayableStream
    let startAt: Double
    let diagnostics: PlaybackDiagnostics?

    /// What this edition has to offer, for the menu. Known already from the title screen, so choosing
    /// costs no request until something is chosen.
    let audioTracks: [TitleTrack]
    let subtitleTracks: [TitleTrack]

    /// Asks the server for the same film with different tracks. Answers with what to play, or nil when
    /// it could not — in which case the film keeps playing as it was, which is the better failure.
    let switchTracks: (String?, String?) async -> PlayableStream?

    let onProgress: (Double) -> Void
    let onFinished: (Double) -> Void

    func makeUIViewController(context: Context) -> AVPlayerViewController {
        // No `preferredForwardBufferDuration`, and its removal is a measurement rather than a tidy-up.
        //
        // It was set to 60 in #211 to make the television hold more than two seconds of buffer. Two runs
        // later the buffer was still two seconds, so it never did that. What it did do — and what no
        // range log before it exists to compare against, since it landed first — is match the one number
        // the server keeps reporting: every one of those forty-to-seventy-megabyte re-reads of the head
        // of the film stops at **sixty-one seconds**, from wherever it started. Sixty seconds asked for,
        // sixty-one seconds fetched, and none of it near the play head.
        //
        // So this asks for nothing and lets the player choose, which is what it was doing before #211
        // and while playback was no worse than it is now.
        let item = AVPlayerItem(asset: AVURLAsset(url: stream.url))
        let player = AVPlayer(playerItem: item)

        if startAt > 1 {
            player.seek(to: CMTime(seconds: startAt, preferredTimescale: 600))
        }

        let controller = AVPlayerViewController()
        controller.player = player

        // Loaded before asking for the overlay host, because `contentOverlayView` is nil until it is —
        // and the fallback would then be a view that does not exist either.
        controller.loadViewIfNeeded()

        // Over the player's own chrome, so it survives the transport bar appearing and going. Typed,
        // because `view` is implicitly unwrapped and the coalescing would otherwise stay optional.
        let overlayHost: UIView = controller.contentOverlayView ?? controller.view

        if let diagnostics {
            diagnostics.start(observing: item)
            let overlay = UIHostingController(rootView: DiagnosticsOverlay(diagnostics: diagnostics))
            overlay.view.backgroundColor = .clear
            // A child of the player, not a loose view inside it: a hosting controller that never joins
            // the hierarchy misses trait changes and appearance callbacks, and is harder to take down.
            controller.addChild(overlay)
            overlayHost.addSubview(overlay.view)
            overlay.view.frame = overlayHost.bounds
            overlay.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
            overlay.didMove(toParent: controller)
            context.coordinator.overlay = overlay
        }

        context.coordinator.observe(player, onProgress: onProgress)
        context.coordinator.present(
            controller, audio: audioTracks, subtitles: subtitleTracks,
            chosen: stream, switching: switchTracks, diagnostics: diagnostics)

        player.play()
        return controller
    }

    func updateUIViewController(_ controller: AVPlayerViewController, context: Context) {}

    static func dismantleUIViewController(_ controller: AVPlayerViewController, coordinator: Coordinator) {
        coordinator.finish()
    }

    func makeCoordinator() -> Coordinator {
        Coordinator(onFinished: onFinished, diagnostics: diagnostics)
    }

    /// One row of the track menu, and whether it is the one being heard.
    ///
    /// Built from what the **server said it chose**, not from what was asked for: a stored preference
    /// answers when nothing was picked, so the first menu a viewer opens already has a tick against a
    /// row nobody in this process selected.
    static func menu(
        audio: [TitleTrack],
        subtitles: [TitleTrack],
        chosen: PlayableStream,
        choose: @escaping (String?, String?) -> Void
    ) -> [UIMenuElement] {
        var sections: [UIMenuElement] = []

        if audio.count > 1 {
            sections.append(UIMenu(title: "Audio", options: .singleSelection, children: audio.map { track in
                UIAction(title: track.label, state: track.id == chosen.audioStreamId ? .on : .off) { _ in
                    choose(track.id, chosen.subtitleStreamId)
                }
            }))
        }

        if !subtitles.isEmpty {
            // "Off" is a row rather than the absence of one: a viewer who turned subtitles on has to be
            // able to turn them off again, and nothing else on this screen does that.
            let off = UIAction(title: "Off", state: chosen.subtitleStreamId == nil ? .on : .off) { _ in
                choose(chosen.audioStreamId, nil)
            }

            sections.append(UIMenu(
                title: "Subtitles", options: .singleSelection,
                children: [off] + subtitles.map { track in
                    UIAction(title: track.label, state: track.id == chosen.subtitleStreamId ? .on : .off) { _ in
                        choose(chosen.audioStreamId, track.id)
                    }
                }))
        }

        return sections
    }

    @MainActor
    final class Coordinator {
        private let onFinished: (Double) -> Void
        private let diagnostics: PlaybackDiagnostics?
        private var token: Any?
        private weak var player: AVPlayer?
        var overlay: UIHostingController<DiagnosticsOverlay>?

        private weak var controller: AVPlayerViewController?
        private var audio: [TitleTrack] = []
        private var subtitles: [TitleTrack] = []
        private var switching: ((String?, String?) async -> PlayableStream?)?
        private var chosen: PlayableStream?

        init(onFinished: @escaping (Double) -> Void, diagnostics: PlaybackDiagnostics?) {
            self.onFinished = onFinished
            self.diagnostics = diagnostics
        }

        /// Every ten seconds, which is often enough that a resume lands where the viewer left and rare
        /// enough that a two-hour film is a few hundred requests rather than a few hundred thousand.
        func observe(_ player: AVPlayer, onProgress: @escaping (Double) -> Void) {
            self.player = player
            token = player.addPeriodicTimeObserver(
                forInterval: CMTime(seconds: 10, preferredTimescale: 1), queue: .main
            ) { time in
                let seconds = time.seconds
                if seconds.isFinite, seconds > 0 {
                    onProgress(seconds)
                }
            }
        }

        /// Puts the track menu in the transport bar and remembers what it needs to rebuild it.
        func present(
            _ controller: AVPlayerViewController,
            audio: [TitleTrack],
            subtitles: [TitleTrack],
            chosen: PlayableStream,
            switching: @escaping (String?, String?) async -> PlayableStream?,
            diagnostics: PlaybackDiagnostics?
        ) {
            self.controller = controller
            self.audio = audio
            self.subtitles = subtitles
            self.chosen = chosen
            self.switching = switching
            refreshMenu()
        }

        private func refreshMenu() {
            guard let controller, let chosen else { return }
            controller.transportBarCustomMenuItems = PlayerView.menu(
                audio: audio, subtitles: subtitles, chosen: chosen
            ) { [weak self] audioId, subtitleId in
                self?.switch(audio: audioId, subtitle: subtitleId)
            }
        }

        /// Swapping a track means a different container, so it means a different URL and a new item.
        ///
        /// The position is taken before asking and restored exactly afterwards: a viewer who changes a
        /// dub expects the film to carry on where it was, and landing a second earlier repeats a line
        /// of dialogue they just heard. Everything else — the menu, the diagnostics — follows the item.
        ///
        /// A refusal leaves the film playing as it was. That is the better failure: the alternative is
        /// stopping a working film because a menu did not get its way.
        private func `switch`(audio audioId: String?, subtitle subtitleId: String?) {
            guard let player, let switching else { return }
            let at = player.currentTime()

            Task { @MainActor [weak self] in
                guard let replacement = await switching(audioId, subtitleId) else { return }

                let item = AVPlayerItem(asset: AVURLAsset(url: replacement.url))
                player.replaceCurrentItem(with: item)
                await player.seek(to: at, toleranceBefore: .zero, toleranceAfter: .zero)

                self?.diagnostics?.start(observing: item)
                player.play()

                self?.chosen = replacement
                self?.refreshMenu()
            }
        }

        /// The position is read from the player rather than passed in: whichever controller was showing,
        /// this is the one thing that knows where the viewer actually got to.
        func finish() {
            let position = player?.currentTime().seconds ?? 0
            player?.pause()

            if let token, let player {
                player.removeTimeObserver(token)
            }

            token = nil
            diagnostics?.stop()
            overlay?.willMove(toParent: nil)
            overlay?.view.removeFromSuperview()
            overlay?.removeFromParent()
            overlay = nil
            onFinished(position.isFinite ? position : 0)
        }
    }
}
