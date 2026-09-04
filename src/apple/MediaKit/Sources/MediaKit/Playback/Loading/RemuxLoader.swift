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
/// round trips a second. Here those are answered from a **window** held in memory, filled by a few
/// large requests running ahead of the play head. The isolated audio fetches fall inside a window that
/// already holds them.
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
    private let lag: Int64
    private let relay = Relay()
    private let session: URLSession

    /// How much an open-ended request may be given per second while the player is below `target`.
    /// Well above any film's rate, so a healthy player is never starved by its own meter.
    private static let allowancePerSecond = 16 << 20

    /// Before the first reading of the player arrives, so the first frames are not waited for.
    private static let initialAllowance = 32 << 20

    /// The most a lagging reader is fetched on its own in one go.
    private static let asideLimit = 8 << 20

    // Everything below is touched on `queue` only.
    private var total: Int64?
    private var learning = false
    private var window: ByteWindow
    private var pending: [AVAssetResourceLoadingRequest] = []
    private var aside: Set<ObjectIdentifier> = []
    private var fetch: URLSessionDataTask?
    private var demand: Int64 = 0
    private var delivered: Int64 = 0
    private var serverRequests = 0
    private var playerAhead: Double = 0
    private var allowance = RemuxLoader.initialAllowance
    private var stopped = false

    private let shared = NSLock()
    private var snapshot = Snapshot()
    private var stopping = false

    /// - Parameters:
    ///   - budget: how much may be held ahead. A 4K film at 78 Mbit/s is ten megabytes a second, so
    ///     this is a dozen seconds of it — a starting point read off the overlay, not a decision.
    ///   - tail: how much is kept *behind* the lowest outstanding request when trimming, for a reader
    ///     that lags. The audio reader runs a few megabytes behind the video one.
    ///   - lag: how far below the window's start a request still counts as that lagging reader and is
    ///     fetched on its own, rather than as a seek that restarts the window.
    ///   - target: seconds the player may hold ahead before open-ended delivery pauses.
    public init(
        origin: URL,
        budget: Int = 128 << 20,
        tail: Int64 = 8 << 20,
        lag: Int64 = 32 << 20,
        target: Double = 20
    ) {
        self.origin = origin
        self.tail = tail
        self.lag = lag
        self.target = target
        self.window = ByteWindow(start: 0, budget: budget)
        self.assetURL = Self.assetURL(for: origin)

        let delivery = OperationQueue()
        delivery.maxConcurrentOperationCount = 1
        delivery.underlyingQueue = queue

        let configuration = URLSessionConfiguration.default
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
    /// window survives, so the new item's first requests are answered from memory.
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
    public func playerHolds(seconds: Double) {
        queue.async {
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
            self.aside.removeAll()
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
        guard !stopped, !isStopping else { return false }

        pending.append(request)
        if total == nil {
            learn()
        } else {
            serve()
        }

        return true
    }

    public func resourceLoader(
        _ resourceLoader: AVAssetResourceLoader, didCancel request: AVAssetResourceLoadingRequest
    ) {
        pending.removeAll { $0 === request }
        aside.remove(ObjectIdentifier(request))
        publish()
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
            guard let self else { return }
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
            self.serve()
        }.resume()
    }

    private func fail(with error: any Error) {
        for request in pending {
            request.finishLoading(with: error)
        }
        pending.removeAll()
        aside.removeAll()
        publish()
    }

    // MARK: - Serving

    /// Answers every pending request with whatever the window holds for it, fetches a lagging one on
    /// its own, moves the window once if a request is somewhere else, and keeps the fill running
    /// ahead. Called on every event: a new request, a chunk arriving, a fetch ending, a reading of
    /// the player.
    private func serve() {
        guard let total, !stopped else { return }

        var done: [AVAssetResourceLoadingRequest] = []
        var lowest: Int64?
        var seek: Int64?

        for request in pending {
            if let information = request.contentInformationRequest {
                information.contentType = "public.mpeg-4"
                information.contentLength = total
                information.isByteRangeAccessSupported = true
            }

            guard let data = request.dataRequest else {
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

            lowest = min(lowest ?? owed.lowerBound, owed.lowerBound)

            switch window.place(owed.lowerBound, lag: lag) {
            case .held:
                var limit = owed.count
                if data.requestsAllDataToEndOfResource {
                    // Metered: an open-ended request takes whatever it is given, and the player's
                    // own buffer is where it would go.
                    guard allowance > 0 else { continue }
                    limit = min(limit, allowance)
                }

                guard let held = window.read(from: owed.lowerBound, upTo: limit) else { continue }
                data.respond(with: held)
                delivered += Int64(held.count)
                if data.requestsAllDataToEndOfResource {
                    allowance -= held.count
                }

                if data.currentOffset >= owed.upperBound {
                    request.finishLoading()
                    done.append(request)
                }

            case .ahead:
                // The fill will bring it.
                break

            case .behind:
                fetchAside(request, owed: owed)

            case .away:
                // The newest such request wins, and the move happens once, after the loop — moving
                // mid-loop would judge every later request against a window that had just changed.
                seek = owed.lowerBound
            }
        }

        pending.removeAll { candidate in done.contains { $0 === candidate } }

        if let seek {
            restart(at: seek)
        }

        // Behind the lowest thing still wanted, minus a tail for a reader that lags. When nothing is
        // pending, the last demand stands in for it.
        demand = lowest ?? demand
        window.trim(keepingFrom: demand - tail)
        ensureFilling(total: total)
        publish()
    }

    private func restart(at offset: Int64) {
        fetch?.cancel()
        fetch = nil
        window.restart(at: offset)
        demand = offset
    }

    /// A request for bytes just behind the window — a reader that lags — fetched on its own, so the
    /// window need not restart and the request is not left pending for bytes it will never hold.
    private func fetchAside(_ request: AVAssetResourceLoadingRequest, owed: Range<Int64>) {
        let id = ObjectIdentifier(request)
        guard !aside.contains(id) else { return }
        aside.insert(id)

        let to = min(owed.upperBound, owed.lowerBound + Int64(Self.asideLimit)) - 1
        var ranged = URLRequest(url: origin)
        ranged.setValue("bytes=\(owed.lowerBound)-\(to)", forHTTPHeaderField: "Range")
        serverRequests += 1

        session.dataTask(with: ranged) { [weak self] data, response, error in
            guard let self else { return }
            self.aside.remove(id)

            // Cancelled or finished while this was out: nothing to give it to.
            guard self.pending.contains(where: { $0 === request }) else { return }

            guard let data, error == nil, (response as? HTTPURLResponse)?.statusCode == 206 else {
                request.finishLoading(with: error ?? URLError(.badServerResponse))
                self.pending.removeAll { $0 === request }
                self.publish()
                return
            }

            request.dataRequest?.respond(with: data)
            self.delivered += Int64(data.count)
            self.serve()
        }.resume()
    }

    // MARK: - Filling

    /// How much room must open up before another request is worth making. A quarter of the budget
    /// makes each fetch tens of megabytes rather than whatever drained in the last second, which is
    /// the difference between a few large requests and many medium ones.
    private var refillThreshold: Int { window.budget / 4 }

    /// Keeps one bounded fetch running ahead whenever there is room for it. Bounded rather than
    /// open-ended on purpose: a connection left open while the window is full would be one nobody
    /// reads, and the server aborts a response its reader has stopped taking.
    private func ensureFilling(total: Int64) {
        guard fetch == nil, !stopped else { return }

        let from = window.end
        guard from < total else { return }

        let room = window.room
        guard room >= refillThreshold || window.count == 0 else { return }

        let to = min(total, from + Int64(room)) - 1
        var request = URLRequest(url: origin)
        request.setValue("bytes=\(from)-\(to)", forHTTPHeaderField: "Range")

        let task = session.dataTask(with: request)
        fetch = task
        serverRequests += 1
        task.resume()
    }

    fileprivate func received(_ chunk: Data, for task: URLSessionDataTask) {
        guard task === fetch else { return }
        window.append(chunk)
        serve()
    }

    /// Only a 206 is a range answered. A 200 is a server that ignored the range and is sending the
    /// whole film, which would be appended past any budget; anything else is a refusal. Either way
    /// the requests waiting on it are told, rather than left waiting on a fetch that is gone.
    fileprivate func responded(_ response: URLResponse, for task: URLSessionDataTask) -> Bool {
        guard task === fetch else { return false }

        let status = (response as? HTTPURLResponse)?.statusCode ?? -1
        guard status == 206 else {
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

        if let total {
            ensureFilling(total: total)
        }
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
        let copy = Snapshot(
            windowBytes: window.count,
            aheadBytes: max(0, window.end - demand),
            serverRequests: serverRequests,
            delivered: delivered,
            outstanding: pending.count,
            totalLength: total)

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
