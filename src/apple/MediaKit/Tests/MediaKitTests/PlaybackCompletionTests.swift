import AVFoundation
import Foundation
import Testing
@testable import MediaKit

@Suite("Playback completion")
@MainActor
struct PlaybackCompletionTests {
    private func item() throws -> AVPlayerItem {
        let url = try #require(Bundle.module.url(
            forResource: "remux-subtitle", withExtension: "mp4", subdirectory: "Fixtures"))
        return AVPlayerItem(url: url)
    }

    @Test("Completion follows replacement items and fires only once")
    func replacement() throws {
        let original = try item()
        let replacement = try item()
        let unrelated = try item()
        let player = AVPlayer(playerItem: original)
        let observer = PlaybackCompletionObserver()
        defer { observer.stop() }
        var completions = 0
        observer.start(watching: player) { completions += 1 }

        NotificationCenter.default.post(name: AVPlayerItem.didPlayToEndTimeNotification, object: unrelated)
        #expect(completions == 0)
        player.replaceCurrentItem(with: replacement)
        NotificationCenter.default.post(name: AVPlayerItem.didPlayToEndTimeNotification, object: original)
        #expect(completions == 0)
        NotificationCenter.default.post(name: AVPlayerItem.didPlayToEndTimeNotification, object: replacement)
        #expect(completions == 1)
        NotificationCenter.default.post(name: AVPlayerItem.didPlayToEndTimeNotification, object: replacement)
        #expect(completions == 1)
    }

    @Test("Stopping observation prevents completion after manual dismissal")
    func stop() throws {
        let item = try item()
        let player = AVPlayer(playerItem: item)
        let observer = PlaybackCompletionObserver()
        var completions = 0
        observer.start(watching: player) { completions += 1 }
        observer.stop()
        observer.stop()
        NotificationCenter.default.post(name: AVPlayerItem.didPlayToEndTimeNotification, object: item)
        #expect(completions == 0)
    }

    @Test("Playing a file to its end reports the final position")
    func naturalEnd() async throws {
        let item = try item()
        let duration = try await item.asset.load(.duration).seconds
        let player = AVPlayer(playerItem: item)
        let observer = PlaybackCompletionObserver()
        defer {
            observer.stop()
            player.pause()
        }
        var positions: [Double] = []
        observer.start(watching: player) { positions.append(player.currentTime().seconds) }
        player.play()
        for _ in 0..<200 where positions.isEmpty {
            try await Task.sleep(for: .milliseconds(50))
        }
        #expect(positions.count == 1)
        let position = try #require(positions.first)
        #expect(abs(position - duration) < 0.1)
        #expect(item.status != .failed)
    }
}
