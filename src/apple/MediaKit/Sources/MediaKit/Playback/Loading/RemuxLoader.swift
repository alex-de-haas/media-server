import AVFoundation
import Foundation
import os

/// Feeds the player its bytes, instead of handing it a URL and hoping.
///
/// An asset opened on a scheme AVFoundation cannot fetch makes it ask *us* for byte ranges. The bytes
/// still come from the same endpoint over the same protocol — same container, same decoder, so Dolby
/// Vision is unaffected. What changes is who decides when to fetch and how much.
///
/// What it changes, and what the measurements said needed changing: the player asks in pieces of half
/// a megabyte with a separate 64 KB request for every handful of audio frames, roughly seven extra
/// round trips a second. Here those are answered from a **window** held in the selected cache storage, filled by a few
/// large requests running ahead of the play head. The window keeps every reader the `ReaderLedger`
/// has seen — the one at the play head and the one a few seconds ahead of it taking audio — so the
/// isolated fetches fall inside a window that already holds them.
///
/// What it does not change: AVFoundation still decides what to ask for. A player that stops asking
/// still stops — but with this in the middle, that moment is visible, and `WedgeDetector` acts on it.
///
/// One thing it must not do is answer an open-ended request at line rate. A request for "everything
/// to the end" accepts whatever it is given, and a loader that keeps giving would pull a whole film
/// into the player's memory in minutes with its own budget bounding only what *it* kept. Delivery to
/// such a request is therefore metered by what the player already holds ahead of the play head —
/// which is, at last, a read-ahead in seconds under our control.
///
/// Every piece of state lives on one serial queue: AVFoundation calls in on it, and the session's
/// delegate queue is bound to it, so there is nothing to lock except what other threads read.
public final class RemuxLoader: NSObject, AVAssetResourceLoaderDelegate, @unchecked Sendable {
    /// The scheme that makes AVFoundation defer to the delegate. Anything it does not know how to
    /// fetch itself will do; this one says what is behind it.
    public static let scheme = "mediaserver-remux"

    private static let log = Logger(subsystem: "com.haas.mediaserver", category: "loader")

    /// What the overlay shows. Copied out under a lock, because it is read from the main actor and
    /// everything else here lives on the loader's own queue.
    public struct Snapshot: Sendable {
        public var windowBytes = 0
        public var aheadBytes: Int64 = 0
        public var serverRequests = 0
        public var delivered: Int64 = 0
        public var outstanding = 0
        public var totalLength: Int64?
        public var diskCache = false
        public var cacheBudget = 0
        public var behindBytes: Int64 = 0
        public var estimatedAheadSeconds: Double?
        public var networkBytes: Int64 = 0
        public var cacheReadBytes: Int64 = 0
        public var cacheFailures = 0
        public var cachedRequests = 0
        public var oldestRequestSeconds: Double = 0
        public var maximumDiskReadMS: Double = 0
        public var maximumDiskWriteMS: Double = 0
        public var deliveryPaused = false

        /// How often the window was moved for a seek, and how often a request was fetched on its own.
        /// The first television run showed both running wild — twenty requests a second, the window
        /// emptied and refilled ahead of where the player read — so they are counted where it shows.
        public var restarts = 0
        public var asides = 0
        public var asideBehind = 0
        public var asideAhead = 0
        public var asideSmall = 0
        public var asideRequestedBytes: Int64 = 0
        public var lastRestart: Restart?

        /// The readers still reading, and how far apart the lowest and highest are. Two readers tens
        /// of megabytes apart is the shape the third run found; a window that follows only one of them
        /// is the shape of every run before it.
        public var readers = 0
        public var readerSpread: Int64 = 0
    }

    /// The request that actually discarded the window; no media URL or token is recorded.
    public struct Restart: Sendable {
        public let windowStart: Int64
        public let windowEnd: Int64
        public let offset: Int64
        public let requestedLength: Int
        public let toEnd: Bool
    }

    /// The URL to build the asset from: the origin with its scheme swapped and nothing else touched,
    /// so the signed token rides along and the origin is recoverable by swapping it back.
    public let assetURL: URL

    /// AVFoundation calls the delegate here, and the session delivers here too.
    public let queue = DispatchQueue(label: "com.haas.mediaserver.loader")

    /// How far ahead the player may hold before an open-ended request stops being fed. Twenty seconds
    /// of a 4K film is a couple of hundred megabytes in the player's own memory, beside the window.
    public let target: Double

    private let origin: URL
    private let tail: Int64
    private var retentionTail: Int64
    private let memoryBudget: Int
    private let memoryTail: Int64
    private let useDiskCache: Bool
    private let diskRoot: URL?
    private let diskBudget: Int
    private var desiredBytes: Int
    private var estimatedBytesPerSecond: Double?
    private var cacheFailures = 0
    private var networkBytes: Int64 = 0
    private var cacheReadBytes: Int64 = 0
    private var maximumDiskReadMS: Double = 0
    private var maximumDiskWriteMS: Double = 0
    private var acceptedAt: [ObjectIdentifier: TimeInterval] = [:]
    private var asideGeneration: [ObjectIdentifier: UUID] = [:]
    private var servingScheduled = false
    private let lag: Int64

    /// How long after its last read a reader still counts as reading. A reader that has fallen
    /// quiet this long is not consulted about where the window goes: after a forward seek, the
    /// reader left behind stops, and the one at the new place takes over once it has.
    private let quiet: TimeInterval
    private let relay = Relay()
    private let session: URLSession

    /// How much an open-ended request may be given per second while the player is below `target`.
    /// Existing delivery policy, kept separate from disk prefetch for this experiment.
    private static let allowancePerSecond = 16 << 20

    /// Before the first reading of the player arrives, so the first frames are not waited for.
    private static let initialAllowance = 32 << 20

    /// The most a lagging reader is fetched on its own in one go.
    private static let asideLimit = 8 << 20

    // Everything below is touched on `queue` only.
    private var total: Int64?
    private var entityTag: String?
    private var fetchEnd: Int64 = 0
    private var learning = false
    private var window: any LoaderByteWindow
    private var pending: [any LoadingRequest] = []
    private var aside: [ObjectIdentifier: URLSessionDataTask] = [:]
    private var fetch: URLSessionDataTask?
    private var readers = ReaderLedger()
    private var demand: Int64 = 0
    private var delivered: Int64 = 0
    private var serverRequests = 0
    private var restarts = 0
    private var asides = 0
    private var asideBehind = 0
    private var asideAhead = 0
    private var asideSmall = 0
    private var asideRequestedBytes: Int64 = 0
    private var lastRestart: Restart?
    private var playerAhead: Double = 0
    private var allowance = RemuxLoader.initialAllowance
    private var stopped = false

    private let shared = NSLock()
    private var snapshot = Snapshot()
    private var stopping = false

    /// - Parameters:
    ///   - budget: initial read-ahead and RAM fallback budget. Disk retention grows once duration is known.
    ///   - tail: how much is kept *behind* the lowest reader when trimming, for a request that
    ///     re-reads a little of what that reader already had.
    ///   - lag: how far below the window's start a request is still fetched on its own rather than
    ///     counted as a seek. Only the shape of the count on the overlay depends on it now.
    ///   - target: seconds the player may hold ahead before open-ended delivery pauses.
    public convenience init(
        origin: URL,
        budget: Int = 128 << 20,
        tail: Int64 = 8 << 20,
        lag: Int64 = 32 << 20,
        target: Double = 20,
        cacheStorage: PlaybackCacheStorage = .memory
    ) {
        self.init(origin: origin, budget: budget, tail: tail, lag: lag, target: target,
                  configuration: .default, cacheStorage: cacheStorage)
    }

    init(origin: URL, budget: Int, tail: Int64, lag: Int64, target: Double,
         configuration: URLSessionConfiguration, readers: ReaderLedger = ReaderLedger(),
         quiet: TimeInterval = 2, cacheStorage: PlaybackCacheStorage = .memory, diskRoot: URL? = nil,
         diskBudget: Int = DiskByteWindow.maximumBudget) {
        self.origin = origin
        self.memoryBudget = budget
        self.memoryTail = tail
        self.retentionTail = tail
        self.desiredBytes = budget
        self.useDiskCache = cacheStorage == .disk
        self.diskRoot = diskRoot
        self.diskBudget = diskBudget
        self.tail = tail
        self.lag = lag
        self.target = target
        self.readers = readers
        self.quiet = quiet
        self.window = ByteWindow(start: 0, budget: budget)
        self.assetURL = Self.assetURL(for: origin)

        let delivery = OperationQueue()
        delivery.maxConcurrentOperationCount = 1
        delivery.underlyingQueue = queue

        configuration.httpMaximumConnectionsPerHost = 3
        configuration.timeoutIntervalForRequest = 30
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.urlCache = nil
        self.session = URLSession(configuration: configuration, delegate: relay, delegateQueue: delivery)

        super.init()
        relay.loader = self
    }

    /// The origin's URL with the scheme swapped, so it becomes a question for us.
    public static func assetURL(for origin: URL) -> URL {
        var parts = URLComponents(url: origin, resolvingAgainstBaseURL: false)!
        parts.scheme = scheme
        return parts.url!
    }

    /// An asset that asks this loader for its bytes. May be called again to re-seat a player: the
    /// window survives, so the new item's first requests are answered from the cache.
    public func makeAsset() -> AVURLAsset {
        let asset = AVURLAsset(url: assetURL)
        asset.resourceLoader.setDelegate(self, queue: queue)
        return asset
    }

    public func makeSnapshot() -> Snapshot {
        shared.withLock { snapshot }
    }

    /// How far ahead of the play head the player already holds, read off it once a second by whoever
    /// can see it. This is the meter on open-ended delivery: below `target` the request is fed, at or
    /// above it nothing more is handed over until the player has consumed some.
    public func playerHolds(seconds: Double, duration: Double = .nan) {
        queue.async {
            if self.window is DiskByteWindow, let total = self.total,
               duration.isFinite, duration > 0 {
                let rate = Double(total) / duration
                self.estimatedBytesPerSecond = rate
                self.desiredBytes = max(1, Int(min(Double(self.window.budget), max(Double(self.memoryBudget), rate * 360))))
                self.retentionTail = Int64(min(Double(self.desiredBytes) / 6, rate * 60))
            }
            self.playerAhead = seconds
            self.allowance = seconds < self.target ? Self.allowancePerSecond : 0
            self.serve()
        }
    }

    /// Ends every fetch and releases the session. The session holds its delegate strongly until it is
    /// invalidated, so a loader that is not stopped is a loader that never goes away.
    ///
    /// The refusal is immediate and the teardown asynchronous: a request AVFoundation hands over
    /// between the two is turned away rather than accepted onto a session about to be invalidated.
    public func stop() {
        shared.withLock { stopping = true }
        queue.async {
            self.stopped = true
            self.fetch?.cancel()
            self.fetch = nil
            self.pending.removeAll()
            self.acceptedAt.removeAll()
            self.window = ByteWindow(start: 0, budget: self.memoryBudget)
            self.cancelAsides()
            self.session.invalidateAndCancel()
        }
    }

    private var isStopping: Bool {
        shared.withLock { stopping }
    }

    // MARK: - AVAssetResourceLoaderDelegate

    public func resourceLoader(
        _ resourceLoader: AVAssetResourceLoader,
        shouldWaitForLoadingOfRequestedResource request: AVAssetResourceLoadingRequest
    ) -> Bool {
        accept(request)
    }

    // Kept independent of AVFoundation's non-constructible requests for lifecycle regression tests.
    func accept(_ request: any LoadingRequest) -> Bool {
        guard !stopped, !isStopping else { return false }

        pending.append(request)
        acceptedAt[ObjectIdentifier(request)] = Self.uptime
        // Every bounded read is a reader's footprint, whatever its size. An open-ended one is not: it
        // begins somewhere and takes whatever it is given, which says nothing about where it reads.
        if let data = request.loadingData, !data.requestsAllDataToEndOfResource {
            readers.observe(offset: data.requestedOffset, length: data.requestedLength, at: Self.uptime)
        }

        if total == nil {
            learn()
        } else {
            serve()
        }

        return true
    }

    /// Monotonic, for the ledger's patience; wall-clock time can jump.
    private static var uptime: TimeInterval { ProcessInfo.processInfo.systemUptime }

    public func resourceLoader(
        _ resourceLoader: AVAssetResourceLoader, didCancel request: AVAssetResourceLoadingRequest
    ) {
        cancel(request)
    }

    func cancel(_ request: any LoadingRequest) {
        pending.removeAll { $0 === request }
        aside.removeValue(forKey: ObjectIdentifier(request))?.cancel()
        asideGeneration.removeValue(forKey: ObjectIdentifier(request))
        acceptedAt.removeValue(forKey: ObjectIdentifier(request))
        serve()
    }

    private func cancelAsides() {
        for task in aside.values { task.cancel() }
        aside.removeAll()
        asideGeneration.removeAll()
    }

    // MARK: - Learning the resource

    /// One HEAD, once, to answer the content-information request. A wrong answer here is "does not
    /// play at all" rather than "plays worse", so it is taken from the server and not assumed.
    private func learn() {
        guard !learning else { return }
        learning = true

        var request = URLRequest(url: origin)
        request.httpMethod = "HEAD"

        // A task made with a completion handler reports there rather than to the delegate, and the
        // handler runs on the delegate queue — which is this one.
        session.dataTask(with: request) { [weak self] _, response, error in
            guard let self, !self.stopped else { return }
            self.learning = false

            guard let http = response as? HTTPURLResponse,
                  http.statusCode == 200,
                  let length = http.value(forHTTPHeaderField: "Content-Length").flatMap(Int64.init)
            else {
                Self.log.error("Could not learn the film's length: \(String(describing: error ?? URLError(.badServerResponse)))")
                self.fail(with: error ?? URLError(.badServerResponse))
                return
            }

            self.total = length
            self.entityTag = http.value(forHTTPHeaderField: "ETag")
            if self.useDiskCache {
                do {
                    self.window = try DiskByteWindow(budget: self.diskBudget, root: self.diskRoot)
                    self.desiredBytes = min(self.memoryBudget, self.window.budget)
                } catch {
                    self.cacheFailures += 1
                    Self.log.warning("Disk cache unavailable; using bounded memory")
                }
            }
            self.serve()
        }.resume()
    }

    private func fail(with error: any Error) {
        for request in pending {
            request.finishLoading(with: error)
        }
        pending.removeAll()
        acceptedAt.removeAll()
        cancelAsides()
        publish()
    }

    // MARK: - Serving

    /// Answers every pending request with whatever the window holds for it, fetches a lagging one on
    /// its own, moves the window once if a request is somewhere else, and keeps the fill running
    /// ahead. Called on every event: a new request, a chunk arriving, a fetch ending, a reading of
    /// the player.
    private func serve() {
        guard let total, !stopped else { return }

        var done: [any LoadingRequest] = []
        var live: [(request: any LoadingRequest, data: any LoadingDataRequest, owed: Range<Int64>)] = []
        var lowestReader: Int64?

        for request in pending {
            request.describe(length: total)

            guard let data = request.loadingData else {
                request.finishLoading()
                done.append(request)
                continue
            }

            guard let owed = LoadRange.owed(
                current: data.currentOffset,
                requestedOffset: data.requestedOffset,
                requestedLength: data.requestedLength,
                toEnd: data.requestsAllDataToEndOfResource,
                total: total)
            else {
                request.finishLoading()
                done.append(request)
                continue
            }

            live.append((request, data, owed))
            // Only a reader that started at or behind demand may stand in for the ledger when it
            // has nothing: a speculative one ahead is not what the window follows.
            if data.requestedOffset <= demand + tail {
                lowestReader = min(lowestReader ?? owed.lowerBound, owed.lowerBound)
            }
        }

        // The window is placed before anything is served, and by the ledger rather than by what
        // happens to be pending: the play-head reader is between reads most of the time, and a window
        // that followed the pending reads followed the reader ahead of it instead — the third run's
        // forty megabytes of play-head reads fetched one by one behind a window full of the future.
        let now = Self.uptime
        readers.expire(at: now)

        // A settled reader somewhere the window does not hold and the fill will not reach is a seek,
        // or a window that ran ahead of the play head, and the window restarts for it — unless a
        // reader below it is still reading from the window, settled or not. A stray far *above* one
        // the window serves is the speculative reader or the probe AVFoundation makes at the middle
        // of a film — the fifth run counted the window sent to the middle of a sixty-gigabyte file
        // on a spinning disk once per re-seat — and its reads are fetched on their own instead. The
        // play head at the start of a film is served from its first read, so a speculative reader
        // that settles first still waits. The same probe made past the middle is *below* the play
        // head and never restarts anything: the ledger does not settle a reader on a probe's two
        // reads, and a seek shows itself by reading on. After a forward seek the reader left behind
        // falls quiet, and the reader at the new place has nothing served below it; a backward seek
        // has nothing below it at all, and restarts at once. One a little behind the start is not a
        // stray in the first place: the fourth run showed a seek settling by steps of a megabyte or
        // two *backwards*, on the keyframe before its target, and a window discarded at every step.
        // Within a tail, the separate fetches carry it the short way.
        let waiting = Set(live.map { $0.data.requestedOffset })
        let reading = readers.reading(at: now, quiet: quiet, waiting: waiting)
        let served = reading.filter { !isStray($0) }.map(\.last).min()
        let strays = reading.filter { $0.reads > ReaderLedger.probeReads && isStray($0) }
        if let stray = strays.min(by: { $0.last < $1.last }), served.map({ stray.last < $0 }) ?? true {
            // The reader's own read at that offset, when a bigger one begins there too.
            let there = live.filter { $0.owed.lowerBound == stray.last }
            let cause = there.min { $0.data.requestedLength < $1.data.requestedLength }
            lastRestart = Restart(
                windowStart: window.start, windowEnd: window.end, offset: stray.last,
                requestedLength: cause?.data.requestedLength ?? Int(stray.next - stray.last),
                toEnd: cause?.data.requestsAllDataToEndOfResource ?? false)
            Self.log.notice("Window reset: [\(self.window.start), \(self.window.end)) -> \(stray.last) for a reader of \(stray.reads) reads; \(reading.count) reading, pending \(self.pending.count)")
            // A tail before the reader rather than at it, for the same backward steps: a seek's first
            // read is at the target, and the ones that follow are at the keyframe before it.
            restart(at: max(0, stray.last - tail))
            restarts += 1
            readers.keep(only: stray)
        }

        // Behind the lowest reader the window can still serve, minus a tail: one farther below the
        // start than that is a probe of a place passed, or is about to restart the window itself.
        // With no reader known the lowest continuing request inside the window stands in — an
        // open-ended request may be the only consumer of the film, and without this its consumed
        // bytes would fill the budget for ever with no refill. The end is inclusive on purpose: a
        // reader that has consumed everything held stands one past the last byte, and that is
        // exactly the moment the window must move on from it.
        if let lowest = readers.lowest(atOrAbove: window.start - tail) {
            demand = lowest
        } else if let offset = lowestReader, offset >= window.start, offset <= window.end {
            demand = offset
        }

        // Free consumed bytes before deciding whether an ahead request needs a separate fetch.
        do { try window.trim(keepingFrom: demand - retentionTail) }
        catch { abandonDiskCache(); scheduleServing(); return }

        for (request, data, owed) in live {
            // One producer owns a request until its HTTP response completes. A window refill or
            // restart must not advance currentOffset underneath that response.
            guard aside[ObjectIdentifier(request)] == nil else { continue }
            switch window.place(owed.lowerBound, lag: lag) {
            case .held:
                var limit = min(owed.count, 1 << 20)
                if data.requestsAllDataToEndOfResource {
                    // Metered: an open-ended request takes whatever it is given, and the player's
                    // own buffer is where it would go.
                    guard allowance > 0 else { continue }
                    limit = min(limit, allowance)
                }

                let held: Data
                let started = Self.uptime
                do {
                    guard let bytes = try window.read(from: owed.lowerBound, upTo: limit) else { continue }
                    held = bytes
                } catch { abandonDiskCache(); scheduleServing(); return }
                if window is DiskByteWindow {
                    maximumDiskReadMS = max(maximumDiskReadMS, (Self.uptime - started) * 1000)
                }
                cacheReadBytes += Int64(held.count)
                data.respond(with: held)
                delivered += Int64(held.count)
                if data.requestsAllDataToEndOfResource {
                    allowance -= held.count
                }

                if data.currentOffset >= owed.upperBound {
                    request.finishLoading()
                    done.append(request)
                } else if window.holds(data.currentOffset),
                          !data.requestsAllDataToEndOfResource || allowance > 0 {
                    scheduleServing()
                }

            case .ahead:
                // A speculative read beyond a full window cannot wait for a fill that has no room.
                // Fetch it separately rather than moving the window away from the current reader.
                if fetch == nil, fillRoom < refillThreshold {
                    fetchAside(request, owed: owed)
                }

            case .behind, .away:
                // A lagging reader, or a speculative one farther than the fill will reach: fetched on
                // its own, bounded, so it is neither left pending nor allowed to move the window.
                fetchAside(request, owed: owed)
            }
        }

        for request in done {
            aside.removeValue(forKey: ObjectIdentifier(request))?.cancel()
            asideGeneration.removeValue(forKey: ObjectIdentifier(request))
            acceptedAt.removeValue(forKey: ObjectIdentifier(request))
        }
        pending.removeAll { candidate in done.contains { $0 === candidate } }

        ensureFilling(total: total)
        publish()
    }

    /// Whether a reader is somewhere the window neither holds nor will reach by filling, and not
    /// merely a little behind the start.
    private func isStray(_ reader: ReaderLedger.Reader) -> Bool {
        if window.holds(reader.last) { return false }
        let behind = window.start - reader.last
        if behind > 0, behind <= tail { return false }
        return window.place(reader.last, lag: lag) != .ahead
    }

    private func restart(at offset: Int64) {
        fetch?.cancel()
        fetch = nil
        demand = offset
        do { try window.restart(at: offset) }
        catch { abandonDiskCache() }
    }

    /// A request for bytes just behind the window — a reader that lags — fetched on its own, so the
    /// window need not restart and the request is not left pending for bytes it will never hold.
    private func fetchAside(_ request: any LoadingRequest, owed: Range<Int64>) {
        let id = ObjectIdentifier(request)
        guard aside[id] == nil, aside.count < 3 else { return }
        var limit = Self.asideLimit
        if request.loadingData?.requestsAllDataToEndOfResource == true {
            guard allowance > 0 else { return }
            limit = min(limit, allowance)
        }
        let count = min(owed.count, limit)
        if request.loadingData?.requestsAllDataToEndOfResource == true {
            // Reserve the bytes before starting so simultaneous asides cannot bypass the meter.
            allowance -= count
        }
        asides += 1
        if owed.lowerBound < window.start { asideBehind += 1 } else { asideAhead += 1 }
        if count <= 65_536 { asideSmall += 1 }
        asideRequestedBytes += Int64(count)

        let to = owed.lowerBound + Int64(count) - 1
        var ranged = URLRequest(url: origin)
        ranged.setValue("bytes=\(owed.lowerBound)-\(to)", forHTTPHeaderField: "Range")
        ranged.setValue(entityTag, forHTTPHeaderField: "If-Range")
        serverRequests += 1

        let generation = UUID()
        asideGeneration[id] = generation
        let task = session.dataTask(with: ranged) { [weak self] data, response, error in
            guard let self else { return }
            guard self.asideGeneration[id] == generation else { return }
            self.asideGeneration.removeValue(forKey: id)
            self.aside.removeValue(forKey: id)
            self.networkBytes += Int64(data?.count ?? 0)

            // Cancelled or finished while this was out: nothing to give it to.
            guard self.pending.contains(where: { $0 === request }) else { return }

            guard let data, error == nil, data.count == count,
                  Self.validRange(response, from: owed.lowerBound, to: to, total: self.total, entityTag: self.entityTag) else {
                request.finishLoading(with: error ?? URLError(.badServerResponse))
                self.pending.removeAll { $0 === request }
                self.acceptedAt.removeValue(forKey: id)
                self.serve()
                return
            }

            guard request.loadingData?.currentOffset == owed.lowerBound else {
                self.serve()
                return
            }
            request.loadingData?.respond(with: data)
            self.delivered += Int64(data.count)
            self.serve()
        }
        aside[id] = task
        task.resume()
    }

    // MARK: - Filling

    /// How much room must open up before another request is worth making. A quarter of the budget
    /// makes each fetch tens of megabytes rather than whatever drained in the last second, which is
    /// the difference between a few large requests and many medium ones.
    private var refillThreshold: Int { max(1, min(32 << 20, desiredBytes / 4)) }
    private var fillRoom: Int {
        var room = max(0, min(window.room, desiredBytes - window.count))
        if estimatedBytesPerSecond != nil {
            let targetEnd = max(demand, window.start) + Int64(desiredBytes) - retentionTail
            room = min(room, Int(max(0, targetEnd - window.end)))
        }
        return room
    }

    private func scheduleServing() {
        guard !servingScheduled, !stopped else { return }
        servingScheduled = true
        queue.async {
            self.servingScheduled = false
            self.serve()
        }
    }

    private func abandonDiskCache() {
        guard window is DiskByteWindow else { return }
        cacheFailures += 1
        fetch?.cancel()
        fetch = nil
        cancelAsides()
        retentionTail = memoryTail
        desiredBytes = memoryBudget
        estimatedBytesPerSecond = nil
        window = ByteWindow(start: max(0, demand - tail), budget: memoryBudget)
        Self.log.warning("Disk cache failed; resuming with bounded memory")
    }

    private static func validRange(_ response: URLResponse?, from: Int64, to: Int64, total: Int64?, entityTag: String?) -> Bool {
        guard let http = response as? HTTPURLResponse, http.statusCode == 206, let total else { return false }
        if let entityTag, http.value(forHTTPHeaderField: "ETag") != entityTag { return false }
        return http.value(forHTTPHeaderField: "Content-Range") == "bytes \(from)-\(to)/\(total)"
    }

    /// Keeps one bounded fetch running ahead whenever there is room for it. Bounded rather than
    /// open-ended on purpose: a connection left open while the window is full would be one nobody
    /// reads, and the server aborts a response its reader has stopped taking.
    private func ensureFilling(total: Int64) {
        guard fetch == nil, !stopped else { return }

        let from = window.end
        guard from < total else { return }

        let room = min(fillRoom, 32 << 20)
        guard room >= refillThreshold || window.count == 0 else { return }

        let to = min(total, from + Int64(room)) - 1
        var request = URLRequest(url: origin)
        request.setValue("bytes=\(from)-\(to)", forHTTPHeaderField: "Range")
        request.setValue(entityTag, forHTTPHeaderField: "If-Range")
        fetchEnd = to + 1

        let task = session.dataTask(with: request)
        fetch = task
        serverRequests += 1
        task.resume()
    }

    fileprivate func received(_ chunk: Data, for task: URLSessionDataTask) {
        guard task === fetch else { return }
        networkBytes += Int64(chunk.count)
        guard Int64(chunk.count) <= fetchEnd - window.end, chunk.count <= window.room else {
            task.cancel()
            fetch = nil
            fail(with: URLError(.badServerResponse))
            return
        }
        let started = Self.uptime
        do { try window.append(chunk) }
        catch { abandonDiskCache(); serve(); return }
        if window is DiskByteWindow {
            maximumDiskWriteMS = max(maximumDiskWriteMS, (Self.uptime - started) * 1000)
        }
        serve()
    }

    /// Only a 206 is a range answered. A 200 is a server that ignored the range and is sending the
    /// whole film, which would be appended past any budget; anything else is a refusal. Either way
    /// the requests waiting on it are told, rather than left waiting on a fetch that is gone.
    fileprivate func responded(_ response: URLResponse, for task: URLSessionDataTask) -> Bool {
        guard task === fetch else { return false }

        let status = (response as? HTTPURLResponse)?.statusCode ?? -1
        let range = task.originalRequest?.value(forHTTPHeaderField: "Range")?
            .dropFirst(6).split(separator: "-").compactMap { Int64($0) } ?? []
        guard status == 206, range.count == 2,
              Self.validRange(response, from: range[0], to: range[1], total: total, entityTag: entityTag) else {
            Self.log.error("Fetch refused with \(status)")
            fetch = nil
            fail(with: URLError(.badServerResponse))
            retryFilling(after: 2)
            return false
        }

        return true
    }

    fileprivate func completed(_ task: URLSessionDataTask, error: (any Error)?) {
        guard task === fetch else { return }
        fetch = nil

        if let error, (error as? URLError)?.code != .cancelled {
            Self.log.warning("Fetch ended early: \(error.localizedDescription)")
            retryFilling(after: 1)
            return
        }

        serve()
    }

    /// A moment later rather than at once: a server that just dropped a connection is not helped by
    /// another one immediately, and the window still has what it has.
    private func retryFilling(after seconds: Double) {
        queue.asyncAfter(deadline: .now() + seconds) { [weak self] in
            guard let self, let total = self.total else { return }
            self.ensureFilling(total: total)
        }
    }

    private func publish() {
        let waiting = Set(pending.compactMap { $0.loadingData?.requestedOffset })
        let reading = readers.active(at: Self.uptime, quiet: quiet, waiting: waiting)
        var copy = Snapshot(
            windowBytes: window.count,
            aheadBytes: max(0, window.end - max(demand, window.start)),
            serverRequests: serverRequests,
            delivered: delivered,
            outstanding: pending.count,
            totalLength: total,
            restarts: restarts,
            asides: asides,
            asideBehind: asideBehind,
            asideAhead: asideAhead,
            asideSmall: asideSmall,
            asideRequestedBytes: asideRequestedBytes,
            lastRestart: lastRestart,
            readers: reading.count,
            readerSpread: (reading.map(\.last).max() ?? 0) - (reading.map(\.last).min() ?? 0))

        copy.diskCache = window is DiskByteWindow
        copy.cacheBudget = desiredBytes
        copy.behindBytes = max(0, min(demand, window.end) - window.start)
        copy.estimatedAheadSeconds = estimatedBytesPerSecond.map { Double(copy.aheadBytes) / $0 }
        copy.networkBytes = networkBytes
        copy.cacheReadBytes = cacheReadBytes
        copy.cacheFailures = cacheFailures
        copy.cachedRequests = pending.filter { request in
            guard let data = request.loadingData else { return false }
            return window.holds(data.currentOffset)
        }.count
        copy.oldestRequestSeconds = acceptedAt.values.min().map { Self.uptime - $0 } ?? 0
        copy.maximumDiskReadMS = maximumDiskReadMS
        copy.maximumDiskWriteMS = maximumDiskWriteMS
        copy.deliveryPaused = allowance == 0
        shared.withLock { snapshot = copy }
    }

    /// The session's delegate, kept apart so the session's strong reference does not pin the loader.
    private final class Relay: NSObject, URLSessionDataDelegate, @unchecked Sendable {
        weak var loader: RemuxLoader?

        func urlSession(
            _ session: URLSession,
            dataTask: URLSessionDataTask,
            didReceive response: URLResponse,
            completionHandler: @escaping (URLSession.ResponseDisposition) -> Void
        ) {
            completionHandler(loader?.responded(response, for: dataTask) == true ? .allow : .cancel)
        }

        func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
            loader?.received(data, for: dataTask)
        }

        func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: (any Error)?) {
            guard let dataTask = task as? URLSessionDataTask else { return }
            loader?.completed(dataTask, error: error)
        }
    }
}
