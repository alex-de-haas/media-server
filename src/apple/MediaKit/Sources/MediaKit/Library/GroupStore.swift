import Foundation
import MediaServerAPI
import Observation

public struct LibraryGroup: Identifiable, Equatable, Sendable {
    public let id: String
    public let name: String
    public let kind: String
    public let catalogType: String
    public let itemCount: Int

    public var typeLabel: String {
        switch catalogType {
        case "movie": "Movies"
        case "anime": "Anime"
        default: "Series"
        }
    }
}

public struct LibraryGroupPage: Equatable, Sendable {
    public let name: String
    public let items: [LibraryTitle]
    public let total: Int
    public let offset: Int
    public let limit: Int
    public var hasNext: Bool { offset + items.count < total && !items.isEmpty }
}

public enum GroupLoadState: Equatable, Sendable {
    case loading, loaded, unsupported
    case failed(String)
}
public enum GroupError: Error { case missing }

/// Reads current group definitions and pages through the same authenticated client as movie details.
@MainActor @Observable
public final class GroupStore {
    public private(set) var items: [LibraryGroup] = []
    public private(set) var state: GroupLoadState = .loading
    private let session: ServerSession
    @ObservationIgnored private var isLoading = false

    public init(session: ServerSession) { self.session = session }

    public func load() async {
        guard !isLoading else { return }
        isLoading = true
        defer { isLoading = false }
        state = .loading
        do {
            switch try await session.api().listNativeGroups() {
            case .ok(let response):
                items = try response.body.json.map {
                    LibraryGroup(id: $0.id, name: $0.name, kind: $0.kind, catalogType: $0.catalogType, itemCount: Int($0.itemCount))
                }
                state = .loaded
            case .undocumented(statusCode: 404, _): state = .unsupported
            case .undocumented(let code, _): state = .failed("The server returned HTTP \(code).")
            }
        } catch { state = .failed(error.localizedDescription) }
    }

    public func page(id: String, offset: Int = 0) async throws -> LibraryGroupPage {
        let response = try await session.api().getNativeGroup(path: .init(id: id), query: .init(limit: 60, offset: Int32(clamping: max(0, offset))))
        if case .notFound = response { throw GroupError.missing }
        let dto = try response.ok.body.json
        return LibraryGroupPage(name: dto.name, items: dto.items.compactMap(LibraryTitle.init),
                                total: Int(dto.total), offset: Int(dto.offset), limit: Int(dto.limit))
    }
}
