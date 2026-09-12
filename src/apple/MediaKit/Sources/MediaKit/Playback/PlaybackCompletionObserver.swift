import AVFoundation
import Foundation

/// Reports natural completion once for the player's current item, including after item replacement.
@MainActor
public final class PlaybackCompletionObserver {
    private var token: (any NSObjectProtocol)?
    private var onEnded: (() -> Void)?

    public init() {}

    public func start(watching player: AVPlayer, onEnded: @escaping () -> Void) {
        stop()
        self.onEnded = onEnded
        // Observe all items and compare at delivery: track switches and loader recovery replace
        // the current item, and a late notification from the old one must not end the new one.
        token = NotificationCenter.default.addObserver(
            forName: AVPlayerItem.didPlayToEndTimeNotification, object: nil, queue: .main
        ) { [weak self, weak player] notification in
            guard let item = notification.object as? AVPlayerItem else { return }
            MainActor.assumeIsolated {
                guard let self, self.token != nil,
                      let player, item === player.currentItem else { return }
                let ended = self.onEnded
                self.stop()
                ended?()
            }
        }
    }

    public func stop() {
        if let token {
            NotificationCenter.default.removeObserver(token)
        }
        token = nil
        onEnded = nil
    }
}
