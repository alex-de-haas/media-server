import Foundation
import MediaServerAPI

/// One playable copy of a title, and what is in it.
public struct TitleVersion: Identifiable, Equatable, Sendable {
    public let id: String

    /// What an operator called this copy — "Director's Cut", "4K" — or nothing when there is only one.
    public let versionName: String?
    public let container: String
    public let sizeBytes: Int64
    public let durationSeconds: Double

    /// Every video stream, in the file's own order.
    ///
    /// All of them rather than the first, because a file can carry a cover image the muxer never flagged
    /// as attached art, and it is a video stream in every way a database can see. Kept at all because a
    /// refusal saying "unsupported video codec" is unactionable without it — a viewer cannot tell a
    /// correct answer about a disc rip from a bug in the negotiation, and neither could I.
    public let videos: [TitleTrack]

    /// The one that is actually the film: the first that is not a still image.
    ///
    /// The same rule the server applies when it decides what to judge, because a cover can sit at a
    /// lower index than the picture and then the two would disagree about the film — which is the exact
    /// case this was written for.
    public var video: TitleTrack? {
        videos.first { !TitleTrack.stillImages.contains(($0.codec ?? "").lowercased()) } ?? videos.first
    }

    public let audio: [TitleTrack]
    public let subtitles: [TitleTrack]
}

public struct TitleTrack: Identifiable, Equatable, Sendable {
    /// Codecs that are a picture rather than a film — cover art the muxer never flagged as attached.
    /// Kept in step with the server's own list; the two answering differently is the defect.
    static let stillImages: Set<String> = ["mjpeg", "png", "bmp", "gif", "webp"]

    public let id: String
    public let label: String
    public let language: String?
    public let codec: String?

    /// Beside the video rather than inside it — a dubbed track or a subtitle file this library carries
    /// and no other client of it can play.
    public let isExternal: Bool
}

/// Everything a title's own screen shows.
public struct TitleDetail: Equatable, Sendable {
    public let id: String
    public let title: String
    public let year: Int?
    public let overview: String?
    public let tagline: String?
    public let genres: [String]
    public let runtimeSeconds: Double?
    public let communityRating: Double?
    public let officialRating: String?

    public let resumeSeconds: Double
    public let played: Bool

    /// Ordered as the server ordered them, with the default first when it named one.
    public let versions: [TitleVersion]

    /// Backdrop and logo as URLs into this instance, already carrying their cache tags.
    public let backdropPath: String?

    public func backdropURL(on server: URL) -> URL? {
        guard let backdropPath else { return nil }
        return URL(string: backdropPath.trimmingPrefix("/").description, relativeTo: server)?.absoluteURL
    }
}

extension TitleDetail {
    init(_ dto: Components.Schemas.NativeItemDto) {
        let detail = dto.detail
        self.id = detail.id
        self.title = detail.title
        self.year = detail.year.map(Int.init)
        self.overview = detail.overview
        self.tagline = detail.tagline
        self.genres = detail.genres ?? []
        self.runtimeSeconds = detail.runtimeTicks.map { Double($0) / 10_000_000 }
        self.communityRating = detail.communityRating
        self.officialRating = detail.officialRating
        self.resumeSeconds = Double(detail.userData?.playbackPositionTicks ?? 0) / 10_000_000
        self.played = detail.userData?.played ?? false
        self.backdropPath = dto.images.backdrop

        var versions = (detail.mediaSources ?? []).map(TitleVersion.init)

        // The default copy leads, because it is the one that plays when nobody chooses.
        if let defaultId = detail.defaultSourceId,
           let at = versions.firstIndex(where: { $0.id == defaultId }) {
            versions.insert(versions.remove(at: at), at: 0)
        }

        self.versions = versions
    }
}

extension TitleVersion {
    init(_ dto: Components.Schemas.MediaSourceDto) {
        self.id = dto.id
        self.versionName = dto.versionName
        self.container = dto.container
        self.sizeBytes = dto.sizeBytes
        self.durationSeconds = Double(dto.durationTicks) / 10_000_000

        let streams = dto.streams ?? []
        self.videos = streams.filter { $0._type.lowercased() == "video" }.map(TitleTrack.init)
        self.audio = streams.filter { $0._type.lowercased() == "audio" }.map(TitleTrack.init)
        self.subtitles = streams.filter { $0._type.lowercased() == "subtitle" }.map(TitleTrack.init)
    }

    /// A size a person can read. Films are gigabytes, so nothing smaller is worth spelling out.
    public var sizeDescription: String {
        ByteCountFormatter.string(fromByteCount: sizeBytes, countStyle: .file)
    }
}

extension TitleTrack {
    init(_ dto: Components.Schemas.MediaStreamDto) {
        self.id = dto.id
        self.language = dto.language
        self.codec = dto.codec
        self.isExternal = dto.isExternal ?? false

        // The server's own title when it has one — "Commentary", "Forced" — and otherwise something
        // assembled, because a list of blank rows is not a choice.
        if let title = dto.title, !title.isEmpty {
            self.label = title
        } else {
            self.label = [dto.language, dto.codec]
                .compactMap { $0 }
                .filter { !$0.isEmpty }
                .joined(separator: " · ")
        }
    }
}
