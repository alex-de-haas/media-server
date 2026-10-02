import Foundation
import Testing

@testable import MediaKit

@Suite("Playback version selection")
struct PlaybackSelectionTests {
    private let alternate = PlaybackPlan.play(PlayableStream(
        url: URL(string: "https://media.example/alternate")!,
        mediaSourceId: "alternate", versionName: "Alternate", decision: .remux,
        signalling: nil, sourceDynamicRange: "SDR", audioStreamId: nil, subtitleStreamId: nil))
    private let original = PlaybackPlan.play(PlayableStream(
        url: URL(string: "https://media.example/original")!,
        mediaSourceId: "original", versionName: "Original", decision: .directPlay,
        signalling: nil, sourceDynamicRange: "SDR", audioStreamId: nil, subtitleStreamId: nil))

    @Test("A chosen remux retains its refusal even when the original plays",
          arguments: [PlaybackRefusal.packagingPending, .unsupportedAudioCodec, .noFile, .requiresConversion])
    func chosenRefusal(reason: PlaybackRefusal) {
        let remux = PlaybackPlan.refused(reason, source: "remux")
        #expect(PlaybackPlan.select(from: [original, remux], preferring: "remux") == remux)
    }

    @Test("A disc requires an explicit MKV operation, not a retry")
    func discConversionReason() {
        #expect(PlaybackRefusal("requires_conversion") == .requiresConversion)
        #expect(!PlaybackRefusal.requiresConversion.isPending)
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
          arguments: [PlaybackRefusal.packagingPending, .unsupportedAudioCodec, .requiresConversion])
    func automaticSelection(reason: PlaybackRefusal) {
        let pending = PlaybackPlan.refused(reason, source: "remux")
        #expect(PlaybackPlan.select(from: [pending, original], preferring: nil) == original)
        #expect(PlaybackPlan.select(from: [pending], preferring: nil) == pending)
        #expect(PlaybackPlan.select(from: [], preferring: nil) == .refused(.noFile, source: ""))
        #expect(PlaybackPlan.select(from: [], preferring: "remux") == .refused(.noFile, source: "remux"))
    }

    @Test("The title's default leads even when resolve lists it last")
    func defaultOrder() {
        let selection = PlaybackPlan.select(from: [original, alternate], preferring: nil,
                                            sourceOrder: ["alternate", "original"])
        #expect(selection == alternate)
        #expect(selection.mediaSourceId == "alternate")
    }

    @Test("Without a default, the title order consistently chooses the first playable version")
    func titleOrder() {
        #expect(PlaybackPlan.select(from: [alternate, original], preferring: nil,
                                   sourceOrder: ["original", "alternate"]) == original)
    }

    @Test("An unavailable default does not hide another playable version",
          arguments: [PlaybackRefusal.packagingPending, .unsupportedDynamicRange, .requiresConversion])
    func unavailableDefault(reason: PlaybackRefusal) {
        let refused = PlaybackPlan.refused(reason, source: "alternate")
        #expect(PlaybackPlan.select(from: [original, refused], preferring: nil,
                                   sourceOrder: ["alternate", "original"]) == original)
        #expect(PlaybackPlan.select(from: [original, refused], preferring: "alternate",
                                   sourceOrder: ["original", "alternate"]) == refused)
    }

    @Test("Stale detail order preserves new sources and an explicit removed choice")
    func changedSources() {
        #expect(PlaybackPlan.select(from: [original], preferring: nil,
                                   sourceOrder: ["removed"]) == original)
        #expect(PlaybackPlan.select(from: [original], preferring: "removed",
                                   sourceOrder: ["original"]) == .refused(.noFile, source: "removed"))
    }
}
