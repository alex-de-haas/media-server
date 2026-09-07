import AVFoundation
import Foundation
import Testing
@testable import MediaKit

@Suite("Remux subtitle presentation")
@MainActor
struct RemuxSubtitlesTests {
    private func item() throws -> AVPlayerItem {
        let url = try #require(Bundle.module.url(
            forResource: "remux-subtitle", withExtension: "mp4", subdirectory: "Fixtures"))
        return AVPlayerItem(url: url)
    }

    @Test("An explicit subtitle choice overrides Off and can be turned off again")
    func selection() async throws {
        let item = try item()
        let group = try #require(try await item.asset.loadMediaSelectionGroup(for: .legible))
        item.select(nil, in: group)
        try await RemuxSubtitles.apply(to: item, enabled: true)
        #expect(item.currentMediaSelection.selectedMediaOption(in: group) != nil)
        try await RemuxSubtitles.apply(to: item, enabled: false)
        #expect(item.currentMediaSelection.selectedMediaOption(in: group) == nil)
    }

    @Test("Cancellation leaves the item's subtitle selection unchanged")
    func cancellation() async throws {
        let item = try item()
        let group = try #require(try await item.asset.loadMediaSelectionGroup(for: .legible))
        item.select(nil, in: group)
        let task = Task { @MainActor in
            try await RemuxSubtitles.apply(to: item, enabled: true)
        }
        task.cancel()
        do {
            try await task.value
            Issue.record("Cancelled selection unexpectedly completed")
        } catch is CancellationError {}
        #expect(item.currentMediaSelection.selectedMediaOption(in: group) == nil)
    }

    @Test("A server-generated tx3g track delivers readable text to AVPlayer")
    func emitsText() async throws {
        let item = try item()
        let sink = SubtitleSink()
        let output = AVPlayerItemLegibleOutput()
        output.setDelegate(sink, queue: .main)
        item.add(output)
        let player = AVPlayer(playerItem: item)
        defer { player.pause() }
        try await RemuxSubtitles.apply(to: item, enabled: true)
        player.play()
        for _ in 0..<100 where sink.strings.isEmpty {
            try await Task.sleep(for: .milliseconds(50))
        }
        #expect(sink.strings.contains("Visible subtitle"))
        #expect(sink.fontSizes.contains { $0 > 0 })
        #expect(item.status != .failed)
    }
}

@MainActor
private final class SubtitleSink: NSObject, @preconcurrency AVPlayerItemLegibleOutputPushDelegate {
    var strings: [String] = []
    var fontSizes: [Double] = []

    func legibleOutput(
        _ output: AVPlayerItemLegibleOutput,
        didOutputAttributedStrings strings: [NSAttributedString],
        nativeSampleBuffers: [Any],
        forItemTime itemTime: CMTime
    ) {
        self.strings.append(contentsOf: strings.map(\.string))
        for string in strings where string.length > 0 {
            if let size = string.attribute(
                NSAttributedString.Key(kCMTextMarkupAttribute_BaseFontSizePercentageRelativeToVideoHeight as String),
                at: 0, effectiveRange: nil) as? NSNumber {
                fontSizes.append(size.doubleValue)
            }
        }
    }
}
