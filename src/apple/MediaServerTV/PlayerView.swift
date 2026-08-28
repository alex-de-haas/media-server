import AVKit
import MediaKit
import SwiftUI

/// A view whose layer *is* the video layer, so the picture resizes with it and nothing has to be laid
/// out by hand.
private final class PlayerLayerView: UIView {
    override class var layerClass: AnyClass { AVPlayerLayer.self }

    // Guaranteed by `layerClass`: UIKit makes the layer this class asks for.
    var playerLayer: AVPlayerLayer { layer as! AVPlayerLayer }
}

/// The bare player: a video layer, and nothing else at all.
///
/// It exists to answer one question. The server's log shows the television reading the film **from the
/// beginning, at full speed, while it plays** — and a scrubbing filmstrip is the only thing on that
/// screen with a reason to. Take the chrome away, and either the re-reading stops and the cause is
/// named, or it does not and AVKit is innocent.
private final class PlainPlayerController: UIViewController {
    private let player: AVPlayer
    private let onExit: () -> Void

    init(player: AVPlayer, onExit: @escaping () -> Void) {
        self.player = player
        self.onExit = onExit
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("Not loaded from a nib.")
    }

    override func loadView() {
        let view = PlayerLayerView()
        view.backgroundColor = .black
        view.playerLayer.player = player
        view.playerLayer.videoGravity = .resizeAspect
        self.view = view
    }

    override func viewDidLoad() {
        super.viewDidLoad()

        // AVKit's controller answers the Menu button itself. This one has to, or the only way out of a
        // film is to unplug the television.
        let menu = UITapGestureRecognizer(target: self, action: #selector(exit))
        menu.allowedPressTypes = [NSNumber(value: UIPress.PressType.menu.rawValue)]
        view.addGestureRecognizer(menu)
    }

    @objc private func exit() {
        onExit()
    }
}

/// `AVPlayerViewController`, unless a diagnostic says otherwise.
///
/// Recorded as a decision in the epic and still the right one: the transport bar, the skip gestures, the
/// track picker and the Siri remote's whole vocabulary come free and cannot be reimplemented to the same
/// standard. Since #172 the container carries every describable track, so that picker is a real one —
/// switching a dub costs nothing and needs no second request.
///
/// The bare alternative is a measurement, not a preference: see `PlaybackPreferences.usesSimplePlayer`.
struct PlayerView: UIViewControllerRepresentable {
    let stream: PlayableStream
    let startAt: Double
    let diagnostics: PlaybackDiagnostics?

    /// Play through a bare layer rather than AVKit's controller, to find out what is reading the film.
    let simple: Bool

    let onProgress: (Double) -> Void
    let onFinished: (Double) -> Void

    /// Leaving the film. AVKit does this itself; the bare player has to be told.
    let onExit: () -> Void

    func makeUIViewController(context: Context) -> UIViewController {
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

        let controller: UIViewController
        let overlayHost: UIView
        if simple {
            let plain = PlainPlayerController(player: player, onExit: onExit)
            controller = plain
            overlayHost = plain.view
        } else {
            let chrome = AVPlayerViewController()
            chrome.player = player
            controller = chrome
            // Over the player's own chrome, so it survives the transport bar appearing and going.
            overlayHost = chrome.contentOverlayView ?? chrome.view
        }

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

    func updateUIViewController(_ controller: UIViewController, context: Context) {}

    static func dismantleUIViewController(_ controller: UIViewController, coordinator: Coordinator) {
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
