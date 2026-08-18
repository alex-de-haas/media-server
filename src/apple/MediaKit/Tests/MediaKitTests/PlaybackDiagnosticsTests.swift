import AVFoundation
import Foundation
import Testing

@testable import MediaKit

/// The one reading this overlay exists to get right.
///
/// Buffer ahead is what separates a freeze caused by starvation from one that is not, and the two want
/// opposite fixes. A wrong number here would send the next diagnosis the same way the last three went.
@Suite("Buffer ahead")
struct BufferAheadTests {
    private func range(_ start: Double, _ duration: Double) -> CMTimeRange {
        CMTimeRange(
            start: CMTime(seconds: start, preferredTimescale: 600),
            duration: CMTime(seconds: duration, preferredTimescale: 600))
    }

    @Test("What is loaded past the play head")
    func straightforward() {
        #expect(PlaybackDiagnostics.bufferAhead(in: [range(0, 120)], at: 30) == 90)
    }

    @Test("The range containing the position, not the first one")
    func afterASeek() {
        // A player keeps what it already fetched, so after a forward seek the first range is an earlier
        // stretch of the film. Measuring from its end gives a negative number — starvation reported at
        // the exact moment the buffer is healthy.
        let ranges = [range(0, 100), range(4000, 60)]

        #expect(PlaybackDiagnostics.bufferAhead(in: ranges, at: 4010) == 50)
    }

    @Test("Nothing loaded around the play head is no buffer, not a negative one")
    func nothingCovers() {
        #expect(PlaybackDiagnostics.bufferAhead(in: [range(0, 100)], at: 4000) == 0)
    }

    @Test("Nothing loaded at all")
    func empty() {
        #expect(PlaybackDiagnostics.bufferAhead(in: [], at: 0) == 0)
    }

    @Test("At the very end of a range there is no buffer left")
    func exhausted() {
        #expect(PlaybackDiagnostics.bufferAhead(in: [range(0, 120)], at: 120) == 0)
    }

    @Test("The start of a range counts as inside it")
    func atTheStart() {
        #expect(PlaybackDiagnostics.bufferAhead(in: [range(50, 30)], at: 50) == 30)
    }
}

@Suite("The diagnostics preference")
struct DiagnosticsPreferenceTests {
    @Test("It survives a round trip")
    func roundTrip() {
        let store = PlaybackPreferencesStore(
            defaults: UserDefaults(suiteName: "MediaKitTests.\(UUID().uuidString)")!)

        store.save(PlaybackPreferences(dynamicRange: .hdr10, showDiagnostics: true))

        #expect(store.load().showDiagnostics)
        #expect(store.load().dynamicRange == .hdr10)
    }

    @Test("Something written before diagnostics existed reads as off")
    func legacyJson() throws {
        // The alternative is a decode failure, which throws away the viewer's dynamic-range choice
        // along with it — and that choice is the one that fixes a dark picture.
        let older = Data(#"{"dynamicRange":"sdr"}"#.utf8)

        let decoded = try JSONDecoder.pairing.decode(PlaybackPreferences.self, from: older)

        #expect(decoded.dynamicRange == .sdr)
        #expect(!decoded.showDiagnostics)
    }

    @Test("A fresh install has it off")
    func defaultsOff() {
        #expect(!PlaybackPreferences().showDiagnostics)
    }
}
