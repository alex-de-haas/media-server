import Foundation
import MediaServerAPI
import Observation

public struct TitleSeason: Identifiable, Equatable, Sendable {
    public let id: String
    public let number: Int?
    public let title: String
    public let episodeCount: Int

    public var label: String {
        number.map { $0 == 0 ? "Specials" : "Season \($0)" } ?? title
    }
}

public struct TitleEpisode: Identifiable, Equatable, Sendable {
    public let id: String
    public let number: Int?
    public let numberEnd: Int?
    public let title: String
    public let overview: String?
    public let durationSeconds: Double?
    public let airDate: Date?
    public let stillPath: String?
    /// Best available dynamic range across versions, independent of the default source.
    public let videoFormatBadge: String?
    public let played: Bool
    public let resumeSeconds: Double

    public var numberLabel: String {
        guard let number else { return "Episode" }
        if let numberEnd, numberEnd > number { return "Episodes \(number)–\(numberEnd)" }
        return "Episode \(number)"
    }

    public var progress: Double {
        guard let durationSeconds, durationSeconds > 0 else { return 0 }
        return min(1, max(0, resumeSeconds / durationSeconds))
    }

    public func artworkURL(on server: URL, fallback: URL?) -> URL? {
        guard let stillPath else { return fallback }
        return URL(string: String(stillPath.trimmingPrefix("/")), relativeTo: server)?.absoluteURL ?? fallback
    }

    init(_ dto: Components.Schemas.NativeEpisodeDto) {
        let episode = dto.episode
        id = episode.id
        number = episode.episodeNumber.map(Int.init)
        numberEnd = episode.episodeNumberEnd.map(Int.init)
        title = episode.title
        overview = episode.overview
        durationSeconds = dto.durationTicks.map { Double($0) / 10_000_000 }
        airDate = episode.airDate
        stillPath = dto.still
        let formats = Set(episode.media?.videoFormats ?? [])
        if formats.contains("Dolby Vision") {
            videoFormatBadge = "Dolby Vision"
        } else if !formats.isDisjoint(with: ["HDR", "HDR10", "HDR10+", "HLG"]) {
            videoFormatBadge = "HDR"
        } else {
            videoFormatBadge = nil
        }
        played = episode.userData?.played ?? false
        resumeSeconds = Double(episode.userData?.playbackPositionTicks ?? 0) / 10_000_000
    }
}

public enum EpisodeLoadError: Error { case missingOrUnsupported }
public enum EpisodeLoadState: Equatable {
    case loading, loaded, missingOrUnsupported
    case failed(String)
}

/// Only the most recently selected season may publish a response, even if transport ignores cancellation.
@MainActor @Observable
public final class SeriesEpisodeStore {
    public private(set) var selectedSeasonID: String?
    public private(set) var episodes: [TitleEpisode] = []
    public private(set) var state: EpisodeLoadState = .loading
    private var generation = 0
    private let fetch: @MainActor (String) async throws -> [TitleEpisode]

    public init(fetch: @escaping @MainActor (String) async throws -> [TitleEpisode]) { self.fetch = fetch }

    public func select(_ seasonID: String) async {
        generation += 1
        let request = generation
        let changed = selectedSeasonID != seasonID
        selectedSeasonID = seasonID
        if changed { episodes = [] }
        state = .loading
        do {
            let result = try await fetch(seasonID)
            guard generation == request, !Task.isCancelled else { return }
            episodes = result.sorted {
                if $0.number != $1.number { return ($0.number ?? Int.max) < ($1.number ?? Int.max) }
                return $0.id < $1.id
            }
            state = .loaded
        } catch {
            guard generation == request, !Task.isCancelled else { return }
            state = error is EpisodeLoadError ? .missingOrUnsupported : .failed(error.localizedDescription)
        }
    }
}
