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
/// Every piece of state lives on one serial queue: AVFoundation calls in on it, and the session's
/// delegate queue is bound to it, so there is nothing to lock except the snapshot the overlay reads.
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

    private let origin: URL
    private let tail: Int64
    private let relay = Relay()
    private let session: URLSession

    // Everything below is touched on `queue` only.
    private var total: Int64?
    private var learning = false
    private var window: ByteWindow
    private var pending: [AVAssetResourceLoadingRequest] = []
    private var fetch: URLSessionDataTask?
    private var demand: Int64 = 0
    private var delivered: Int64 = 0
    private var serverRequests = 0
    private var stopped = false

    private let snapshotLock = NSLock()
    private var snapshot = Snapshot()

    /// - Parameters:
    ///   - budget: how much may be held ahead. A 4K film at 78 Mbit/s is ten megabytes a second, so
    ///     this is a dozen seconds of it — a starting point read off the overlay, not a decision.
    ///   - tail: how much is kept *behind* the play head, for a reader that lags. The audio reader
    ///     runs a few megabytes behind the video one and must not find its bytes already dropped.
    public init(origin: URL, budget: Int = 128 << 20, tail: Int64 = 8 << 20) {
        self.origin = origin
        self.tail = tail
        self.window = ByteWindow(start: 0, budget: budget)
        self.assetURL = Self.assetURL(for: origin)

        let delivery = OperationQueue()
        delivery.maxConcurrentOperationCount = 1
        delivery.underlyingQueue = queue

        let configuration = URLSessionConfiguration.default
        configuration.httpMaximumConnectionsPerHost = 2
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
        snapshotLock.withLock { snapshot }
    }

    /// Ends every fetch and releases the session. The session holds its delegate strongly until it is
    /// invalidated, so a loader that is not stopped is a loader that never goes away.
    public func stop() {
        queue.async {
            self.stopped = true
            self.fetch?.cancel()
            self.fetch = nil
            self.pending.removeAll()
            self.session.invalidateAndCancel()
        }
    }

    // MARK: - AVAssetResourceLoaderDelegate

    public func resourceLoader(
        _ resourceLoader: AVAssetResourceLoader,
        shouldWaitForLoadingOfRequestedResource request: AVAssetResourceLoadingRequest
    ) -> Bool {
        guard !stopped else { return false }

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
        publish()
    }

    // MARK: - Serving

    /// Answers every pending request with whatever the window holds for it, moves the window when a
    /// request is somewhere else, and keeps the fill running ahead. Called on every event: a new
    /// request, a chunk arriving, a fetch ending.
    private func serve() {
        guard let total, !stopped else { return }

        var done: [AVAssetResourceLoadingRequest] = []
        var lowest: Int64?

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
            demand = max(demand, owed.lowerBound)

            if let held = window.read(from: owed.lowerBound, upTo: Int(clamping: owed.count)) {
                data.respond(with: held)
                delivered += Int64(held.count)

                if data.currentOffset >= owed.upperBound {
                    request.finishLoading()
                    done.append(request)
                }
            } else if !window.reaches(owed.lowerBound, tail: tail) {
                // Somewhere the fill will not arrive at: a seek. Everything held is for a part of the
                // film nobody is watching any more.
                restart(at: owed.lowerBound)
            }
        }

        pending.removeAll { candidate in done.contains { $0 === candidate } }

        // Behind the lowest thing still wanted, minus a tail for a reader that lags. When nothing is
        // pending, the last demand stands in for it.
        window.trim(keepingFrom: (lowest ?? demand) - tail)
        ensureFilling(total: total)
        publish()
    }

    private func restart(at offset: Int64) {
        fetch?.cancel()
        fetch = nil
        window.restart(at: offset)
        demand = offset
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

    fileprivate func responded(_ response: URLResponse, for task: URLSessionDataTask) -> Bool {
        guard task === fetch else { return false }
        guard let http = response as? HTTPURLResponse, http.statusCode == 206 || http.statusCode == 200 else {
            Self.log.error("Fetch refused: \((response as? HTTPURLResponse)?.statusCode ?? -1)")
            fetch = nil
            return false
        }

        return true
    }

    fileprivate func completed(_ task: URLSessionDataTask, error: (any Error)?) {
        guard task === fetch else { return }
        fetch = nil

        if let error, (error as? URLError)?.code != .cancelled {
            Self.log.warning("Fetch ended early: \(error.localizedDescription)")
            // A moment later rather than at once: a server that just dropped a connection is not
            // helped by another one immediately, and the window still has what it has.
            queue.asyncAfter(deadline: .now() + 1) { [weak self] in
                guard let self, let total = self.total else { return }
                self.ensureFilling(total: total)
            }
            return
        }

        if let total {
            ensureFilling(total: total)
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

        snapshotLock.withLock { snapshot = copy }
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
