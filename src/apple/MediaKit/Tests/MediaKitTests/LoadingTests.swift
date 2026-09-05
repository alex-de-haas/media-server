import Foundation
import Testing

@testable import MediaKit

/// The window the player is fed from. Every rule about what it holds decides whether a request is
/// answered from memory, waited on, or treated as a seek — and a wrong one plays badly in a way that
/// looks like the network.
@Suite("The byte window")
struct ByteWindowTests {
    private func bytes(_ count: Int, _ value: UInt8) -> Data {
        Data(repeating: value, count: count)
    }

    @Test("What was appended can be read back, across chunk boundaries")
    func readAcrossChunks() {
        var window = ByteWindow(start: 100, budget: 1_000)
        window.append(bytes(10, 1))
        window.append(bytes(10, 2))

        let read = window.read(from: 105, upTo: 10)

        #expect(read == bytes(5, 1) + bytes(5, 2))
        #expect(window.end == 120)
    }

    @Test("A read past what is held is shortened, not refused")
    func partialRead() {
        var window = ByteWindow(start: 0, budget: 1_000)
        window.append(bytes(10, 1))

        #expect(window.read(from: 4, upTo: 100)?.count == 6)
    }

    @Test("An offset not held is nil, so the caller waits or seeks rather than getting empty data")
    func notHeld() {
        var window = ByteWindow(start: 100, budget: 1_000)
        window.append(bytes(10, 1))

        #expect(window.read(from: 99, upTo: 1) == nil)
        #expect(window.read(from: 110, upTo: 1) == nil)
    }

    @Test("Trimming drops whole chunks behind the play head and never splits one")
    func trim() {
        var window = ByteWindow(start: 0, budget: 1_000)
        window.append(bytes(10, 1))
        window.append(bytes(10, 2))
        window.append(bytes(10, 3))

        window.trim(keepingFrom: 15)

        // The chunk holding byte 15 stays whole: a few bytes kept longer is cheaper than a copy.
        #expect(window.start == 10)
        #expect(window.count == 20)
        #expect(window.read(from: 15, upTo: 1) == bytes(1, 2))
    }

    @Test("A restart forgets everything and begins at the seek")
    func restart() {
        var window = ByteWindow(start: 0, budget: 1_000)
        window.append(bytes(10, 1))

        window.restart(at: 5_000)

        #expect(window.start == 5_000)
        #expect(window.end == 5_000)
        #expect(window.count == 0)
        #expect(window.read(from: 0, upTo: 1) == nil)
    }

    @Test("Where an offset stands decides whether it is read, waited for, fetched alone, or a seek")
    func placement() {
        var window = ByteWindow(start: 1_000, budget: 100)
        window.append(bytes(50, 1))

        #expect(window.place(1_049, lag: 10) == .held)
        #expect(window.place(1_050, lag: 10) == .ahead)   // the fill arrives here next
        #expect(window.place(1_149, lag: 10) == .ahead)
        #expect(window.place(1_150, lag: 10) == .away)    // farther than the fill will reach
        #expect(window.place(999, lag: 10) == .behind)    // a reader that lags: fetched alone
        #expect(window.place(990, lag: 10) == .behind)
        #expect(window.place(989, lag: 10) == .away)      // a seek backwards
    }

    @Test("An empty window at a seek is ahead of nothing, so the first request waits for the fill")
    func emptyWindow() {
        let window = ByteWindow(start: 5_000, budget: 100)

        #expect(window.place(5_000, lag: 10) == .ahead)
        #expect(window.read(from: 5_000, upTo: 1) == nil)
    }

    @Test("Trimming through many chunks does not cost a copy per chunk")
    func trimManyChunks() {
        var window = ByteWindow(start: 0, budget: 1_000_000)
        for i in 0 ..< 200 {
            window.append(bytes(10, UInt8(i % 250)))
        }

        window.trim(keepingFrom: 1_500)

        #expect(window.start == 1_500)
        #expect(window.count == 500)
        #expect(window.read(from: 1_500, upTo: 1) == bytes(1, UInt8(150 % 250)))
    }

    @Test("Budget and room say when filling should stop")
    func budget() {
        var window = ByteWindow(start: 0, budget: 30)
        window.append(bytes(20, 1))

        #expect(window.room == 10)
        #expect(!window.isFull)
        window.append(bytes(10, 1))
        #expect(window.isFull)
        #expect(window.room == 0)
    }
}

/// What a request is still owed. A byte past the end, or one short, is a request that never finishes.
@Suite("What a request is owed")
struct LoadRangeTests {
    @Test("A request inside the resource is owed exactly what it asked for")
    func whole() {
        #expect(LoadRange.owed(current: 100, requestedOffset: 100, requestedLength: 50, toEnd: false, total: 1_000)
            == 100 ..< 150)
    }

    @Test("Once part has been answered, only the rest is owed")
    func partiallyAnswered() {
        #expect(LoadRange.owed(current: 130, requestedOffset: 100, requestedLength: 50, toEnd: false, total: 1_000)
            == 130 ..< 150)
    }

    @Test("A request running past the end is clamped to it")
    func clamped() {
        #expect(LoadRange.owed(current: 980, requestedOffset: 980, requestedLength: 50, toEnd: false, total: 1_000)
            == 980 ..< 1_000)
    }

    @Test("To the end means to the end")
    func toEnd() {
        #expect(LoadRange.owed(current: 5, requestedOffset: 5, requestedLength: 0, toEnd: true, total: 1_000)
            == 5 ..< 1_000)
    }

    @Test("Nothing is owed once answered, or when it began past the end")
    func nothing() {
        #expect(LoadRange.owed(current: 150, requestedOffset: 100, requestedLength: 50, toEnd: false, total: 1_000) == nil)
        #expect(LoadRange.owed(current: 1_000, requestedOffset: 1_000, requestedLength: 1, toEnd: false, total: 1_000) == nil)
        #expect(LoadRange.owed(current: 1_000, requestedOffset: 1_000, requestedLength: 0, toEnd: true, total: 1_000) == nil)
    }
}

/// These decide whether a film that is playing perfectly well gets interrupted, so they come before
/// the remedy is wired to anything.
@Suite("Telling a stuck player from a waiting one")
struct WedgeDetectorTests {
    private func reading(
        position: Double, delivered: Int64, ahead: Int64 = 1_000_000, paused: Bool = false
    ) -> WedgeDetector.Reading {
        .init(position: position, delivered: delivered, heldAhead: ahead, paused: paused)
    }

    /// Feeds the readings in order and reports which of them fired. `#expect` cannot mutate, so the
    /// mutation happens here and the assertion is over the list.
    private func fired(patience: Int, _ readings: [WedgeDetector.Reading]) -> [Bool] {
        var detector = WedgeDetector(patience: patience)
        return readings.map { detector.observe($0) }
    }

    @Test("Still, unfed, with bytes in hand, for long enough — that is stuck")
    func wedged() {
        let still = Array(repeating: reading(position: 100, delivered: 500), count: 5)

        // Baseline, then three still readings; the fourth observation is the third still one.
        #expect(fired(patience: 3, still) == [false, false, false, true, false])
    }

    @Test("A player being fed slowly is waiting, not stuck")
    func fedSlowly() {
        let fed = [500, 600, 700, 800].map { reading(position: 100, delivered: Int64($0)) }

        #expect(fired(patience: 2, fed).allSatisfy { !$0 })
    }

    @Test("A player with nothing held ahead is starving, and re-seating it would change nothing")
    func starving() {
        let starving = Array(repeating: reading(position: 100, delivered: 500, ahead: 0), count: 5)

        #expect(fired(patience: 2, starving).allSatisfy { !$0 })
    }

    @Test("A paused film is not stuck, and the pause is not counted against it")
    func paused() {
        let sequence = [
            reading(position: 100, delivered: 500),
            reading(position: 100, delivered: 500, paused: true),
            reading(position: 100, delivered: 500, paused: true),
            // Resumed: a baseline again, and the next still reading is the first, not the third.
            reading(position: 100, delivered: 500),
            reading(position: 100, delivered: 500),
            reading(position: 100, delivered: 500),
        ]

        #expect(fired(patience: 2, sequence) == [false, false, false, false, false, true])
    }

    @Test("A play head that moves resets the count")
    func moving() {
        let sequence = [
            reading(position: 100, delivered: 500),
            reading(position: 100, delivered: 500),
            reading(position: 101, delivered: 500),
            reading(position: 101, delivered: 500),
            reading(position: 101, delivered: 500),
        ]

        #expect(fired(patience: 2, sequence) == [false, false, false, false, true])
    }

    @Test("After a re-seat the next reading is a baseline")
    func restarted() {
        var detector = WedgeDetector(patience: 1)
        let before = detector.observe(reading(position: 100, delivered: 500))
        detector.restarted()
        let baseline = detector.observe(reading(position: 100, delivered: 500))
        let after = detector.observe(reading(position: 100, delivered: 500))

        #expect(!before)
        #expect(!baseline)
        #expect(after)
    }
}

/// Which reads enter the ledger. The first television run showed what happens when the speculative
/// reader moves the window: twenty requests a second, a hundred megabytes held ahead of the film.
@Suite("Telling the readers apart")
struct DemandTests {
    @Test("A read at the play head or of audio is a reader's; a speculative or open-ended one is not")
    func demand() {
        #expect(RemuxLoader.isDemand(length: 65_536, toEnd: false))
        #expect(RemuxLoader.isDemand(length: 1 << 20, toEnd: false))
        #expect(RemuxLoader.isDemand(length: RemuxLoader.demandLimit, toEnd: false))
        #expect(!RemuxLoader.isDemand(length: RemuxLoader.demandLimit + 1, toEnd: false))
        // The smallest speculative read the log showed: two megabytes, and it must not anchor.
        #expect(!RemuxLoader.isDemand(length: 2_000_000, toEnd: false))
        #expect(!RemuxLoader.isDemand(length: 1_000, toEnd: true))
    }
}

/// The ledger the window is placed by. The third television run measured why the pending list is
/// not enough: the play-head reader is between reads most of the time, and the trim followed the
/// audio reader forty megabytes ahead of it.
@Suite("The reader ledger")
struct ReaderLedgerTests {
    /// Byte-sized slack and patience, so the arithmetic is legible. The slack behind covers the
    /// longest read here, as the loader's covers its largest small read.
    private func ledger() -> ReaderLedger {
        ReaderLedger(slackBehind: 16, slackAhead: 8, patience: 5)
    }

    @Test("Contiguous reads are one reader, and it settles on the second")
    func oneReader() {
        var ledger = ledger()
        ledger.observe(offset: 100, length: 10, at: 0)
        #expect(ledger.settled.isEmpty)
        #expect(ledger.lowest == nil)

        ledger.observe(offset: 110, length: 10, at: 0.1)
        #expect(ledger.readers.count == 1)
        #expect(ledger.settled.count == 1)
        #expect(ledger.lowest == 110)
    }

    @Test("A read far from every reader is a new one, and the lowest is what the window keeps")
    func twoReaders() {
        var ledger = ledger()
        ledger.observe(offset: 100, length: 10, at: 0)
        ledger.observe(offset: 110, length: 10, at: 0.1)
        ledger.observe(offset: 500, length: 2, at: 0.2)      // audio, well ahead
        ledger.observe(offset: 506, length: 2, at: 0.3)      // the next burst, within the slack
        ledger.observe(offset: 120, length: 10, at: 0.4)     // the play head again

        #expect(ledger.readers.count == 2)
        #expect(ledger.settled.count == 2)
        #expect(ledger.lowest == 120)
        #expect(ledger.spread == 506 - 120)

        // The reader ahead reading last does not move what the window keeps.
        ledger.observe(offset: 512, length: 2, at: 0.5)
        #expect(ledger.lowest == 120)
    }

    @Test("A request re-issued for the rest of a range continues its reader rather than starting one")
    func reissued() {
        var ledger = ledger()
        ledger.observe(offset: 100, length: 15, at: 0)      // asked for 100..<115
        ledger.observe(offset: 102, length: 13, at: 0.1)    // asked again for 102..<115

        #expect(ledger.readers.count == 1)
        #expect(ledger.lowest == 102)
    }

    @Test("A single read is a probe: it neither settles nor moves the lowest")
    func probe() {
        var ledger = ledger()
        ledger.observe(offset: 100, length: 10, at: 0)
        ledger.observe(offset: 110, length: 10, at: 0.1)
        ledger.observe(offset: 9_000, length: 2, at: 0.2)   // the end of the file, looked at once

        #expect(ledger.settled.count == 1)
        #expect(ledger.lowest == 110)
        #expect(ledger.spread == 0)
    }

    @Test("A reader not heard from within the patience is forgotten")
    func patience() {
        var ledger = ledger()
        ledger.observe(offset: 100, length: 10, at: 0)
        ledger.observe(offset: 110, length: 10, at: 0.1)
        ledger.observe(offset: 500, length: 2, at: 0.2)
        ledger.observe(offset: 506, length: 2, at: 0.3)

        ledger.expire(at: 4)
        #expect(ledger.settled.count == 2)

        ledger.observe(offset: 512, length: 2, at: 4.5)
        ledger.expire(at: 6)
        #expect(ledger.settled.count == 1)
        #expect(ledger.lowest == 512)
    }

    @Test("After a restart only the reader it was made for is still known")
    func keepOnly() {
        var ledger = ledger()
        ledger.observe(offset: 100, length: 10, at: 0)
        ledger.observe(offset: 110, length: 10, at: 0.1)
        ledger.observe(offset: 5_000, length: 10, at: 0.2)  // a seek
        ledger.observe(offset: 5_010, length: 10, at: 0.3)

        let seek = ledger.settled.first { $0.last == 5_010 }!
        ledger.keep(only: seek)

        #expect(ledger.readers.count == 1)
        #expect(ledger.lowest == 5_010)
    }
}

@Suite("The loader's URL")
struct RemuxLoaderURLTests {
    @Test("Only the scheme changes, so the token rides along and the origin is recoverable")
    func schemeSwap() {
        let origin = URL(string: "http://192.168.1.50:8096/native/v1/media/abc/remux?token=t&signalling=dvh1")!

        let asset = RemuxLoader.assetURL(for: origin)

        #expect(asset.scheme == RemuxLoader.scheme)
        #expect(asset.host == "192.168.1.50")
        #expect(asset.port == 8096)
        #expect(asset.path == "/native/v1/media/abc/remux")
        #expect(asset.query == "token=t&signalling=dvh1")
    }
}
