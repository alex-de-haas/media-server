import Foundation

/// Fetches artwork from the server, carrying the credential that route requires.
///
/// `AsyncImage` cannot be used: it issues its own requests and has nowhere to put an `Authorization`
/// header, and the image route is bearer-authenticated on purpose — only `AVPlayer`'s self-issued ranged
/// requests need a signed URL, and artwork is fetched by the client's own networking layer where setting
/// a header is trivial.
///
/// The cache is in memory and bounded by count rather than bytes. A poster grid revisits the same
/// hundred images all evening, and a television has no business holding a library's worth of artwork on
/// disk when the server is one request away.
public actor ArtworkLoader {
    private let session: URLSession
    private let token: @Sendable () async -> String?
    private let refresh: @Sendable () async -> String?
    private var cache: [URL: Data] = [:]
    private var order: [URL] = []
    private var inFlight: [URL: Task<Data?, Never>] = [:]

    private static let capacity = 240

    public init(
        token: @escaping @Sendable () async -> String?,
        refresh: @escaping @Sendable () async -> String? = { nil }
    ) {
        let configuration = URLSessionConfiguration.default
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.timeoutIntervalForRequest = 20
        self.session = URLSession(configuration: configuration)
        self.token = token
        self.refresh = refresh
    }

    public func image(at url: URL) async -> Data? {
        if let hit = cache[url] {
            return hit
        }

        // A grid scrolling past the same poster twice must not ask for it twice.
        if let running = inFlight[url] {
            return await running.value
        }

        let task = Task<Data?, Never> { [session, token, refresh] in
            func fetch(_ bearer: String?) async -> (Data, Int)? {
                var request = URLRequest(url: url)
                if let bearer {
                    request.setValue("Bearer \(bearer)", forHTTPHeaderField: "Authorization")
                }

                guard let (data, response) = try? await session.data(for: request),
                      let http = response as? HTTPURLResponse
                else {
                    return nil
                }

                return (data, http.statusCode)
            }

            guard var answer = await fetch(await token()) else { return nil }

            // A poster can easily be the first authenticated request after tvOS resumes the app, and
            // treating its 401 as "no image" would leave a grid blank until something unrelated
            // happened to refresh the credential. The refresh is the session's, so it is the same
            // serialised one the API requests use rather than a second race.
            if answer.1 == 401, let fresh = await refresh(), let retried = await fetch(fresh) {
                answer = retried
            }

            // A 404 is ordinary: the provider had no poster for this title.
            return answer.1 == 200 && !answer.0.isEmpty ? answer.0 : nil
        }

        inFlight[url] = task
        let data = await task.value
        inFlight[url] = nil

        if let data {
            remember(url, data)
        }

        return data
    }

    private func remember(_ url: URL, _ data: Data) {
        if cache[url] == nil {
            order.append(url)
        }

        cache[url] = data

        while order.count > Self.capacity {
            cache.removeValue(forKey: order.removeFirst())
        }
    }
}
