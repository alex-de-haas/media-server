import Foundation
import MediaServerAPI
import Observation

public struct MovieCollection: Identifiable, Equatable, Sendable {
    public let id: String
    public let name: String
    public let posterPath: String?
    public let itemCount: Int
}

public struct MovieCollectionDetail: Equatable, Sendable {
    public let id: String
    public let name: String
    public let backdropPath: String?
    public let items: [LibraryTitle]
}

public enum CollectionLoadState: Equatable, Sendable {
    case loading, loaded, unsupported
    case failed(String)
}

public enum CollectionError: Error { case missing }

/// Collections use the same credential refresh and generated contract as ordinary titles.
@MainActor @Observable
public final class CollectionStore {
    public private(set) var items: [MovieCollection] = []
    public private(set) var state: CollectionLoadState = .loading
    private let session: ServerSession

    public init(session: ServerSession) { self.session = session }

    public func load() async {
        state = .loading
        do {
            switch try await session.api().listNativeCollections() {
            case .ok(let response):
                items = try response.body.json.map {
                    MovieCollection(id: $0.id, name: $0.name, posterPath: $0.posterUrl, itemCount: Int($0.itemCount))
                }
                state = .loaded
            case .undocumented(statusCode: 404, _): state = .unsupported
            case .undocumented(let code, _): state = .failed("The server returned HTTP \(code).")
            }
        } catch {
            state = .failed(error.localizedDescription)
        }
    }

    public func detail(id: String) async throws -> MovieCollectionDetail {
        let response = try await session.api().getNativeCollection(path: .init(id: id))
        if case .notFound = response { throw CollectionError.missing }
        let dto = try response.ok.body.json
        return MovieCollectionDetail(id: dto.id, name: dto.name, backdropPath: dto.backdropUrl,
                                     items: dto.items.compactMap(LibraryTitle.init))
    }
}
