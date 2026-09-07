import Foundation
import Testing

@testable import MediaKit

@Suite("Playback version selection")
struct PlaybackSelectionTests {
    private let original = PlaybackPlan.play(PlayableStream(
        url: URL(string: "https://media.example/original")!,
        mediaSourceId: "original", versionName: "Original", decision: .directPlay,
        signalling: nil, sourceDynamicRange: "SDR", audioStreamId: nil, subtitleStreamId: nil))

    @Test("A chosen remux retains its refusal even when the original plays",
          arguments: [PlaybackRefusal.packagingPending, .unsupportedAudioCodec, .noFile])
    func chosenRefusal(reason: PlaybackRefusal) {
        let remux = PlaybackPlan.refused(reason, source: "remux")
        #expect(PlaybackPlan.select(from: [original, remux], preferring: "remux") == remux)
    }

    @Test("A removed selected version never falls back to the original")
    func missingSelection() {
        #expect(PlaybackPlan.select(from: [original], preferring: "removed")
                == .refused(.noFile, source: "removed"))
    }

    @Test("Retrying the same version plays it once indexing finishes")
    func readySelection() {
        let ready = PlaybackPlan.play(PlayableStream(
            url: URL(string: "https://media.example/remux")!,
            mediaSourceId: "remux", versionName: "Remux", decision: .remux,
            signalling: nil, sourceDynamicRange: "SDR", audioStreamId: nil, subtitleStreamId: nil))
        #expect(PlaybackPlan.select(from: [original, ready], preferring: "remux") == ready)
    }

    @Test("Automatic selection skips a pending or unsupported first copy",
          arguments: [PlaybackRefusal.packagingPending, .unsupportedAudioCodec])
    func automaticSelection(reason: PlaybackRefusal) {
        let pending = PlaybackPlan.refused(reason, source: "remux")
        #expect(PlaybackPlan.select(from: [pending, original], preferring: nil) == original)
        #expect(PlaybackPlan.select(from: [pending], preferring: nil) == pending)
        #expect(PlaybackPlan.select(from: [], preferring: nil) == .refused(.noFile, source: ""))
        #expect(PlaybackPlan.select(from: [], preferring: "remux") == .refused(.noFile, source: "remux"))
    }
}
