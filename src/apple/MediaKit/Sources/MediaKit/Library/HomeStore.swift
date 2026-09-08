import Foundation
import MediaServerAPI
import Observation

public enum HomeSection: String, CaseIterable, Sendable {
    case continueWatching, nextUp, recommendations

    public var title: String {
        switch self {
        case .continueWatching: "Continue Watching"
        case .nextUp: "Next Up"
        case .recommendations: "Recommended for You"
        }
    }
}

public struct HomeCard: Identifiable, Equatable, Sendable {
    public let id: String
    public let destination: LibraryTitle
    public let subtitle: String

    init?(_ dto: Components.Schemas.LibraryRailItemDto) {
        guard let destination = LibraryTitle(.init(id: dto.navId, catalogId: "", kind: dto.navKind,
            title: dto.title, posterUrl: dto.posterUrl)) else { return nil }
        self.id = dto.id
        self.destination = destination
        let seconds = Double(dto.userData?.playbackPositionTicks ?? 0) / 10_000_000
        self.subtitle = [dto.subtitle, seconds > 0 ? "Resume from \(PlaybackPosition.label(seconds))" : nil]
            .compactMap { $0 }.joined(separator: " · ")
    }

    init?(_ dto: Components.Schemas.RecommendationDto) {
        guard dto.inLibrary, let id = dto.mediaItemId,
              let destination = LibraryTitle(.init(id: id, catalogId: "", kind: dto.kind,
                title: dto.title, year: dto.year, posterUrl: dto.posterUrl)) else { return nil }
        self.id = id
        self.destination = destination
        let detail = dto.reason?.detail
        switch dto.reason?.kind {
        case "seed": self.subtitle = detail.map { "Because you watched \($0)" } ?? "Matches your taste"
        case "rated-seed": self.subtitle = detail.map { "Because you liked \($0)" } ?? "Matches your taste"
        case "franchise": self.subtitle = detail.map { "More from \($0)" } ?? "Continue a collection"
        case "person": self.subtitle = detail.map { "Featuring \($0)" } ?? "Matches your taste"
        default: self.subtitle = "From your library · Matches your taste"
        }
    }
}

public enum HomeRailState: Equatable, Sendable {
    case idle, loading, loaded, unsupported
    case failed(String)
}

public struct HomeRail: Equatable, Sendable {
    public internal(set) var state: HomeRailState = .idle
    public internal(set) var items: [HomeCard] = []

    public init(state: HomeRailState = .idle, items: [HomeCard] = []) {
        self.state = state
        self.items = items
    }
}

/// Each rail publishes as soon as its own request finishes. Discovery never blocks resuming a film.
@MainActor @Observable
public final class HomeStore {
    public private(set) var rails: [HomeSection: HomeRail] = [:]
    private let session: ServerSession

    public init(session: ServerSession) { self.session = session }

    public func load() async {
        async let resume: Void = load(.continueWatching)
        async let next: Void = load(.nextUp)
        async let recommendations: Void = load(.recommendations)
        _ = await (resume, next, recommendations)
    }

    public func load(_ section: HomeSection) async {
        guard rails[section]?.state != .loading else { return }
        rails[section, default: HomeRail()].state = .loading
        do {
            let cards: [HomeCard]
            switch section {
            case .continueWatching:
                let response = try await session.api().getNativeHomeResume(query: .init(limit: 20))
                if case .undocumented(statusCode: 404, _) = response {
                    rails[section] = HomeRail(state: .unsupported); return
                }
                cards = try response.ok.body.json.compactMap(HomeCard.init)
            case .nextUp:
                let response = try await session.api().getNativeHomeNextUp(query: .init(limit: 20))
                if case .undocumented(statusCode: 404, _) = response {
                    rails[section] = HomeRail(state: .unsupported); return
                }
                cards = try response.ok.body.json.compactMap(HomeCard.init)
            case .recommendations:
                let response = try await session.api().getNativeV1Recommendations(query: .init(limit: 60))
                if case .undocumented(statusCode: 404, _) = response {
                    rails[section] = HomeRail(state: .unsupported); return
                }
                cards = Array(try response.ok.body.json.items.compactMap(HomeCard.init).prefix(20))
            }
            guard !Task.isCancelled else {
                rails[section, default: HomeRail()].state = .idle; return
            }
            rails[section] = HomeRail(state: .loaded, items: cards)
        } catch {
            rails[section, default: HomeRail()].state = Task.isCancelled ? .idle : .failed("Could not load this row.")
        }
    }
}
