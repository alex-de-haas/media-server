import Foundation
import MediaServerAPI
import Observation

/// One title, in the terms a screen needs rather than the terms the wire uses.
///
/// Named `LibraryTitle` and not `LibraryItem` because `DeveloperToolsSupport` — which every file using
/// `#Preview` pulls in — exports a `LibraryItem` of its own, and the collision makes the name ambiguous
/// at every call site rather than only here.
public struct LibraryTitle: Identifiable, Equatable, Sendable {
    public let id: String
    public let catalogId: String
    public let kind: LibraryKind
    public let title: String
    public let year: Int?
    public let posterPath: String?

    /// Where the viewer got to, in seconds. Zero for something never started.
    public let resumeSeconds: Double
    public let played: Bool

    /// Artwork **from this instance**, not from the metadata provider's CDN.
    ///
    /// `posterUrl` on the wire is the provider's own URL, which is what the web UI has always used. A
    /// television is pointed at the server's copy instead, for the two reasons `NativeImageEndpoints`
    /// gives: a client on the same network keeps working with no internet at all, and browsing a library
    /// stops being visible to TMDb. The cost is our bandwidth for something a CDN does well.
    ///
    /// No `tag`: it is a content hash and only sharpens caching, and the sync feed does not carry one.
    /// Without it the route still serves whatever the current image is.
    public func artworkURL(on server: URL) -> URL? {
        URL(string: "native/v1/items/\(id)/images/primary", relativeTo: server)?.absoluteURL
    }

    /// Whether the metadata provider has a poster at all. The route answers 404 when it does not, so
    /// this saves a request rather than changing the answer.
    public var hasArtwork: Bool { posterPath != nil }
}

/// The kinds this client shows. `Season`, `Episode` and `Video` exist on the server and are reached
/// through a series rather than listed beside one, so they are not cases here.
public enum LibraryKind: String, Sendable, CaseIterable {
    case movie = "Movie"
    case series = "Series"
}

public enum LibraryLoadState: Equatable, Sendable {
    case idle
    case loading
    case loaded
    case failed(String)
}

/// Holds the library in memory, read from the delta-sync feed.
///
/// There is no "list the library" route on this surface — `items/{id}` fetches one, and everything else
/// goes through `sync`. So browsing means draining that feed: no cursor, page until it says there is no
/// more, and keep the result for as long as the app is running.
///
/// Nothing is persisted, which is the deliberate half of the deferred local mirror. The expensive part
/// of a mirror is the storage — the schema, the cursor kept between launches, the reset when a cursor
/// goes stale, the tombstones — and none of it is here. Re-reading a few hundred titles at launch costs
/// a couple of requests. When that starts to be felt, storage goes underneath this without the screens
/// noticing.
@MainActor
@Observable
public final class LibraryStore {
    public private(set) var state: LibraryLoadState = .idle
    public private(set) var items: [LibraryTitle] = []

    /// Guards against a runaway feed: a cursor that never stops advancing would otherwise page for ever.
    private static let pageLimit = 200

    private let session: ServerSession

    public init(session: ServerSession) {
        self.session = session
    }

    public var movies: [LibraryTitle] { items.filter { $0.kind == .movie } }
    public var series: [LibraryTitle] { items.filter { $0.kind == .series } }

    public var server: URL { session.paired.server }

    /// One title in full, fetched when its screen opens rather than carried by the feed.
    ///
    /// The sync feed deliberately carries a poster and a name and nothing else — versions, tracks and an
    /// overview for every title in a library would be most of a database sent to browse a grid.
    public func detail(for id: String) async throws -> TitleDetail {
        TitleDetail(try await session.api().getNativeV1ItemsId(path: .init(id: id)).ok.body.json)
    }

    public func load() async {
        guard state != .loading else { return }
        state = .loading

        do {
            let client = session.api()
            var collected: [LibraryTitle] = []
            var cursor: String?

            for _ in 0..<Self.pageLimit {
                let page = try await client.getNativeV1Sync(query: .init(cursor: cursor)).ok.body.json
                collected.append(contentsOf: page.items.compactMap(LibraryTitle.init))

                guard page.hasMore else { break }
                // A page that says there is more but does not move the cursor would loop for ever.
                guard page.cursor != cursor else { break }
                cursor = page.cursor
            }

            items = collected.sorted {
                $0.title.localizedStandardCompare($1.title) == .orderedAscending
            }
            state = .loaded
        } catch {
            state = .failed(String(describing: error))
        }
    }
}

extension LibraryTitle {
    /// Built from the generated DTO, and `nil` for a kind this client does not list.
    init?(_ dto: Components.Schemas.LibraryItemDto) {
        guard let kind = LibraryKind(rawValue: dto.kind) else { return nil }

        self.id = dto.id
        self.catalogId = dto.catalogId
        self.kind = kind
        self.title = dto.title
        self.year = dto.year.map(Int.init)
        self.posterPath = dto.posterUrl

        // Ticks are hundred-nanosecond units, which is what the library stores and what Jellyfin's
        // vocabulary uses; a screen wants seconds.
        let ticks = dto.userData?.playbackPositionTicks ?? 0
        self.resumeSeconds = Double(ticks) / 10_000_000
        self.played = dto.userData?.played ?? false
    }
}
