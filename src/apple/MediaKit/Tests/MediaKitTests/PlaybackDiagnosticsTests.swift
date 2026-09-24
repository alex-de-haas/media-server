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

    @Test("A switch that was retired is ignored rather than fatal")
    func retiredSwitch() {
        // Anybody who turned the bare player on has `usesSimplePlayer` sitting in their stored
        // preferences, and it names nothing now. Refusing to decode it would take the viewer's
        // dynamic-range choice — the control that fixes a dark picture — down with it.
        let stored = Data(#"{"dynamicRange":"hdr10","showDiagnostics":true,"usesSimplePlayer":true}"#.utf8)

        let decoded = try? JSONDecoder.pairing.decode(PlaybackPreferences.self, from: stored)

        #expect(decoded?.dynamicRange == .hdr10)
        #expect(decoded?.showDiagnostics == true)
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

/// How long a film took to appear, split where it can be split.
///
/// Ten seconds to first frame on the television against three on a Mac, and the argument about why has
/// run on assertions for a fortnight. These two numbers divide it either side of the URL existing.
@MainActor
@Suite("Starting a film")
struct StartupTests {
    @Test("The server's half is whatever the caller timed")
    func resolveIsRecorded() {
        let diagnostics = PlaybackDiagnostics()

        diagnostics.resolved(after: 1.25)

        #expect(diagnostics.resolveSeconds == 1.25)
    }

    @Test("Nothing is claimed before anything has been measured")
    func nothingYet() {
        let diagnostics = PlaybackDiagnostics()

        #expect(diagnostics.resolveSeconds == nil)
        #expect(diagnostics.openSeconds == nil)
    }

    @Test("A play head sitting where a resume sent it is not the film starting")
    func aResumeIsNotAStart() {
        // The seek lands before the first frame appears. Counting that as the film opening would
        // report a resumed title — the very one somebody would be timing — as instant.
        #expect(!PlaybackDiagnostics.hasStarted(at: 3_600, from: 3_600))
        #expect(!PlaybackDiagnostics.hasStarted(at: 3_600.1, from: 3_600))
        #expect(PlaybackDiagnostics.hasStarted(at: 3_601, from: 3_600))
    }

    @Test("From the top, any movement at all is the film starting")
    func fromTheTop() {
        #expect(!PlaybackDiagnostics.hasStarted(at: 0, from: 0))
        #expect(PlaybackDiagnostics.hasStarted(at: 1, from: 0))
    }
}

@Suite("Contiguous player buffer coverage")
struct ContiguousBufferTests {
    private func range(_ start: Double, _ duration: Double) -> CMTimeRange {
        CMTimeRange(start: CMTime(seconds: start, preferredTimescale: 600),
                    duration: CMTime(seconds: duration, preferredTimescale: 600))
    }

    @Test("A shared boundary does not create a false zero")
    func touching() {
        #expect(PlaybackDiagnostics.bufferAhead(in: [range(100, 10), range(110, 20)], at: 110) == 20)
        #expect(PlaybackDiagnostics.bufferAhead(in: [range(100, 10), range(110, 20)], at: 105) == 25)
    }

    @Test("Unsorted overlapping and nested ranges contribute continuous coverage")
    func overlapping() {
        let ranges = [range(115, 20), range(102, 3), range(100, 20), range(0, 40)]
        #expect(PlaybackDiagnostics.bufferAhead(in: ranges, at: 110) == 25)
    }

    @Test("A real gap is never bridged, even if tiny")
    func gap() {
        #expect(PlaybackDiagnostics.bufferAhead(in: [range(100, 10), range(110.01, 20)], at: 105) == 5)
        #expect(PlaybackDiagnostics.bufferAhead(in: [range(100, 10), range(111, 20)], at: 110.5) == 0)
    }

    @Test("Invalid time values cannot poison the chart")
    func invalid() {
        #expect(PlaybackDiagnostics.bufferAhead(in: [.invalid, range(0, 20)], at: 5) == 15)
        #expect(PlaybackDiagnostics.bufferAhead(in: [range(0, 20)], at: .nan) == 0)
    }
}

@Suite("Common network diagnostics")
struct NetworkDiagnosticsTests {
    @Test("Request totals use event counters rather than the number of log events")
    func counts() {
        #expect(PlaybackDiagnostics.knownTotal(of: [100, 30, 5]) == 135)
        #expect(PlaybackDiagnostics.knownTotal(of: [Int64(1_000), 500]) == 1_500)
    }

    @Test("Unavailable or incomplete counters differ from a measured zero")
    func unknown() {
        #expect(PlaybackDiagnostics.knownTotal(of: [Int]()) == nil)
        #expect(PlaybackDiagnostics.knownTotal(of: [-1, -1]) == nil)
        #expect(PlaybackDiagnostics.knownTotal(of: [100, -1]) == nil)
        #expect(PlaybackDiagnostics.knownTotal(of: [0, 0]) == 0)
        #expect(PlaybackDiagnostics.knownTotal(of: [Int.max, 1]) == nil)
    }

    @Test("Bursty log updates use elapsed time and a bounded recent window")
    func rates() {
        let source = NSObject()
        var metrics = PlaybackNetworkMetrics()
        metrics.update(source: ObjectIdentifier(source), bytes: 0, requests: 0, at: 0)
        #expect(metrics.mbps == nil)
        #expect(metrics.meanRequestBytes == nil)
        metrics.update(source: ObjectIdentifier(source), bytes: 0, requests: 0, at: 1)
        metrics.update(source: ObjectIdentifier(source), bytes: 12_500_000, requests: 100, at: 5)
        #expect(metrics.mbps == 20)
        #expect(metrics.requestsPerSecond == 20)
        #expect(metrics.meanRequestBytes == 125_000)
        metrics.update(source: ObjectIdentifier(source), bytes: 12_500_000, requests: 100, at: 10)
        #expect(metrics.mbps == 10)
        metrics.update(source: ObjectIdentifier(source), bytes: 12_500_000, requests: 100, at: 15)
        #expect(metrics.mbps == 0)
        #expect(metrics.requestsPerSecond == 0)
        #expect(metrics.peakMbps == 20)
    }

    @Test("Replacing the native item or loader resets totals, rates and peak")
    func replacement() {
        let nativeItem = NSObject(), loader = NSObject()
        var metrics = PlaybackNetworkMetrics()
        metrics.update(source: ObjectIdentifier(nativeItem), bytes: 0, requests: 0, at: 0)
        metrics.update(source: ObjectIdentifier(nativeItem), bytes: 100_000, requests: 100, at: 1)
        // A new source with a larger byte total must not become a false throughput spike.
        metrics.update(source: ObjectIdentifier(loader), bytes: 10_000_000, requests: 2, at: 2)
        #expect(metrics.bytes == 10_000_000)
        #expect(metrics.requests == 2)
        #expect(metrics.mbps == nil)
        #expect(metrics.requestsPerSecond == nil)
        #expect(metrics.peakMbps == nil)
        // Recovery that reuses the same loader continues the measurement.
        metrics.update(source: ObjectIdentifier(loader), bytes: 11_000_000, requests: 4, at: 3)
        #expect(metrics.mbps == 8)
        #expect(metrics.requestsPerSecond == 2)
    }

    @Test("Unknown counters never appear as zero or a plausible average")
    func missingCounters() {
        let source = NSObject()
        var metrics = PlaybackNetworkMetrics()
        metrics.update(source: ObjectIdentifier(source), bytes: 1_000, requests: nil, at: 0)
        metrics.update(source: ObjectIdentifier(source), bytes: 2_000, requests: -1, at: 1)
        #expect(metrics.requests == nil)
        #expect(metrics.requestsPerSecond == nil)
        #expect(metrics.meanRequestBytes == nil)
        #expect(metrics.mbps == 0.008)
        metrics.update(source: ObjectIdentifier(source), bytes: nil, requests: nil, at: 2)
        #expect(metrics.bytes == nil)
        #expect(metrics.mbps == nil)
        metrics.update(source: ObjectIdentifier(source), bytes: 10_000, requests: 20, at: 3)
        #expect(metrics.mbps == nil)
        #expect(metrics.requestsPerSecond == nil)
    }

    @Test("Regressing counters and duplicate timestamps reset the rate baseline")
    func reset() {
        let source = NSObject()
        var metrics = PlaybackNetworkMetrics()
        metrics.update(source: ObjectIdentifier(source), bytes: 100, requests: 10, at: 1)
        metrics.update(source: ObjectIdentifier(source), bytes: 50, requests: 5, at: 2)
        #expect(metrics.mbps == nil)
        #expect(metrics.requestsPerSecond == nil)
        metrics.update(source: ObjectIdentifier(source), bytes: 100, requests: 10, at: 2)
        #expect(metrics.mbps == nil)
        metrics.update(source: ObjectIdentifier(source), bytes: 200, requests: 20, at: 3)
        #expect(metrics.mbps == 0.0008)
        #expect(metrics.requestsPerSecond == 10)
    }
}

@MainActor
@Suite("Diagnostics item lifecycle")
struct DiagnosticsLifecycleTests {
    @Test("Native playback starts with unknown network counters")
    func nativeStart() {
        let diagnostics = PlaybackDiagnostics()
        let item = AVPlayerItem(url: URL(string: "https://example.invalid/movie.mp4")!)
        diagnostics.start(observing: item)
        defer { diagnostics.stop() }
        #expect(diagnostics.loader == nil)
        #expect(diagnostics.serverRequests == nil)
        #expect(diagnostics.networkBytes == nil)
        #expect(diagnostics.networkMbps == nil)
    }

    @Test("Loader counters are used only while that loader feeds playback")
    func loaderToNative() {
        let diagnostics = PlaybackDiagnostics()
        let loader = RemuxLoader(origin: URL(string: "https://example.invalid/remux.mp4")!)
        let item = AVPlayerItem(url: URL(string: "https://example.invalid/movie.mp4")!)
        defer { diagnostics.stop(); loader.stop() }
        diagnostics.loader = loader
        diagnostics.start(observing: item)
        #expect(diagnostics.loaderDetails != nil)
        #expect(diagnostics.serverRequests == 0)
        #expect(diagnostics.networkBytes == 0)
        diagnostics.loader = nil
        diagnostics.start(observing: item)
        #expect(diagnostics.loaderDetails == nil)
        #expect(diagnostics.serverRequests == nil)
        #expect(diagnostics.networkBytes == nil)
        #expect(diagnostics.meanRequestBytes == nil)
    }

    @Test("Stalls survive item replacement and queued old notifications are ignored")
    func stallsAcrossItems() async throws {
        let diagnostics = PlaybackDiagnostics()
        let first = AVPlayerItem(url: URL(string: "https://example.invalid/first.mp4")!)
        let second = AVPlayerItem(url: URL(string: "https://example.invalid/second.mp4")!)
        diagnostics.start(observing: first)
        defer { diagnostics.stop() }
        NotificationCenter.default.post(name: AVPlayerItem.playbackStalledNotification, object: first)
        try await Task.sleep(for: .milliseconds(20))
        #expect(diagnostics.stalls == 1)
        NotificationCenter.default.post(name: AVPlayerItem.playbackStalledNotification, object: first)
        diagnostics.start(observing: second)
        NotificationCenter.default.post(name: AVPlayerItem.playbackStalledNotification, object: second)
        try await Task.sleep(for: .milliseconds(20))
        #expect(diagnostics.stalls == 2)
    }
}
