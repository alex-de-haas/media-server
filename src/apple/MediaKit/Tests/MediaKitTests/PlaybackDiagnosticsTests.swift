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

/// The reading that settles what a flat buffer cannot.
///
/// A buffer parked at two seconds says bytes arrive exactly as fast as they are spent — equally true of
/// a path that cannot go faster and of a player that has decided not to ask for more. Only a peak well
/// above the film's own rate tells those apart, so this arithmetic has to be right.
@Suite("Inflow")
struct InflowTests {
    @Test("Bytes over a second, in the unit a speed test is quoted in")
    func megabits() {
        // 12.5 MB in one second is 100 Mbit/s, decimal — the unit an interface reports.
        #expect(PlaybackDiagnostics.rate(bytes: 12_500_000, over: 1) == 100)
    }

    @Test("A longer gap between readings is divided out")
    func acrossSeveralSeconds() {
        #expect(PlaybackDiagnostics.rate(bytes: 25_000_000, over: 2) == 100)
    }

    @Test("Nothing arrived is no rate, not a division")
    func nothingArrived() {
        #expect(PlaybackDiagnostics.rate(bytes: 0, over: 1) == 0)
    }

    @Test("No time passed is no rate")
    func noTime() {
        // Two samples in the same instant would otherwise divide by zero and poison the session peak
        // with an infinity that never leaves it.
        #expect(PlaybackDiagnostics.rate(bytes: 1_000_000, over: 0) == 0)
    }

    @Test("A total that went backwards is not a negative rate")
    func wentBackwards() {
        // Summing every access-log event should make the total monotonic, so this should not arise.
        // It is guarded anyway: a negative rate would be recorded as a measurement of something.
        #expect(PlaybackDiagnostics.rate(bytes: -5_000, over: 1) == 0)
    }
}

/// The session total, which is not the number the player hands over.
@Suite("Bytes across access-log events")
struct TransferredTotalTests {
    @Test("Every event counts, not just the newest")
    func acrossEvents() {
        // `numberOfBytesTransferred` is per event. Reading only the last one makes the total collapse
        // each time AVFoundation opens another connection — and the film's cost collapses with it.
        #expect(PlaybackDiagnostics.total(of: [4_000, 6_000, 1_000]) == 11_000)
    }

    @Test("An event with no figure to give contributes nothing rather than subtracting")
    func unknownEvent() {
        #expect(PlaybackDiagnostics.total(of: [4_000, -1, 6_000]) == 10_000)
    }

    @Test("No events yet is nothing transferred")
    func noEvents() {
        #expect(PlaybackDiagnostics.total(of: []) == 0)
    }
}

/// Seconds of film actually played, which is what the cost per second is divided by.
@Suite("Watched time")
struct AdvanceTests {
    @Test("An ordinary second of playback counts")
    func ordinary() {
        #expect(PlaybackDiagnostics.advance(from: 100, to: 101) == 1)
    }

    @Test("A resume does not make the hour before it watched")
    func resume() {
        // The bytes are this session's; the position is the film's. Dividing one by the other reports a
        // fraction of the real cost and points the diagnosis at the wrong half of the problem.
        #expect(PlaybackDiagnostics.advance(from: 0, to: 3_600) == 0)
    }

    @Test("A forward seek is not watching")
    func seekForward() {
        #expect(PlaybackDiagnostics.advance(from: 100, to: 400) == 0)
    }

    @Test("A backward seek does not subtract from what was watched")
    func seekBackward() {
        #expect(PlaybackDiagnostics.advance(from: 400, to: 100) == 0)
    }

    @Test("A paused second advances nothing")
    func paused() {
        #expect(PlaybackDiagnostics.advance(from: 100, to: 100) == 0)
    }
}
