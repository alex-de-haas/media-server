import AVKit
import MediaKit
import SwiftUI

/// `AVPlayerViewController`, not a player of our own.
///
/// Recorded as a decision in the epic and still the right one: the transport bar, the skip gestures, the
/// track picker and the Siri remote's whole vocabulary come free and cannot be reimplemented to the same
/// standard. Since #172 the container carries every describable track, so that picker is a real one —
/// switching a dub costs nothing and needs no second request.
struct PlayerView: UIViewControllerRepresentable {
    let stream: PlayableStream
    let startAt: Double
    let diagnostics: PlaybackDiagnostics?
    let onProgress: (Double) -> Void
    let onFinished: (Double) -> Void

    func makeUIViewController(context: Context) -> AVPlayerViewController {
        let controller = AVPlayerViewController()
        let player = AVPlayer(url: stream.url)
        controller.player = player

        if startAt > 1 {
            player.seek(to: CMTime(seconds: startAt, preferredTimescale: 600))
        }

        if let diagnostics, let item = player.currentItem {
            diagnostics.start(observing: item)
            // Over the player's own chrome, so it survives the transport bar appearing and going.
            let overlay = UIHostingController(rootView: DiagnosticsOverlay(diagnostics: diagnostics))
            overlay.view.backgroundColor = .clear
            controller.contentOverlayView?.addSubview(overlay.view)
            overlay.view.frame = controller.view.bounds
            overlay.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
            context.coordinator.overlay = overlay
        }

        context.coordinator.observe(player, onProgress: onProgress)
        player.play()
        return controller
    }

    func updateUIViewController(_ controller: AVPlayerViewController, context: Context) {}

    static func dismantleUIViewController(_ controller: AVPlayerViewController, coordinator: Coordinator) {
        let position = controller.player?.currentTime().seconds ?? 0
        controller.player?.pause()
        coordinator.finish(at: position.isFinite ? position : 0)
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

        func finish(at position: Double) {
            if let token, let player {
                player.removeTimeObserver(token)
            }

            token = nil
            diagnostics?.stop()
            overlay?.view.removeFromSuperview()
            overlay = nil
            onFinished(position)
        }
    }
}
