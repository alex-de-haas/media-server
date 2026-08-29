import AVKit
import MediaKit
import SwiftUI

/// `AVPlayerViewController`, not a player of our own.
///
/// Recorded as a decision in the epic and still the right one: the transport bar, the skip gestures, the
/// track picker and the Siri remote's whole vocabulary come free and cannot be reimplemented to the same
/// standard. Since #172 the container carries every describable track, so that picker is a real one —
/// switching a dub costs nothing and needs no second request.
///
/// A bare `AVPlayerLayer` lived here for one measurement, to find out whether AVKit's scrubbing
/// filmstrip was reading the film. It was not — the re-reading was our own `preferredForwardBufferDuration`
/// — so the alternative is gone rather than left where somebody could switch to a player with no
/// transport bar and no Dolby Vision.
struct PlayerView: UIViewControllerRepresentable {
    let stream: PlayableStream
    let startAt: Double
    let diagnostics: PlaybackDiagnostics?
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

        // Over the player's own chrome, so it survives the transport bar appearing and going.
        let overlayHost = controller.contentOverlayView ?? controller.view

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

    @MainActor
    final class Coordinator {
        private let onFinished: (Double) -> Void
        private let diagnostics: PlaybackDiagnostics?
        private var token: Any?
        private weak var player: AVPlayer?
        var overlay: UIHostingController<DiagnosticsOverlay>?

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
