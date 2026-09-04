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

    /// What the probe said this stream's dynamic range is.
    ///
    /// Usually one name — `SDR`, `HDR10`, `Dolby Vision`, or the generic `HDR` a container header cannot
    /// be more precise than — but it can name **several**, separated by a middle dot or a comma:
    /// production holds `Dolby Vision · HDR10`, which is what a profile 8.1 file honestly is.
    ///
    /// Carried because a refusal saying "unsupported dynamic range" is unactionable without it, exactly
    /// as one about a codec is.
    public let hdrFormat: String?

    /// The Dolby Vision configuration record, beside the flat `hdrFormat`: what tells a dual-layer profile 7
    /// — a UHD Blu-ray remux, which this device plays as HDR10 — from a single-layer 8.1, which it plays as
    /// Dolby Vision. Nil for anything that is not Dolby Vision, and for a stream the server probed before it
    /// recorded the profile.
    public let dolbyVision: DolbyVisionDetail?

    /// Beside the video rather than inside it — a dubbed track or a subtitle file this library carries
    /// and no other client of it can play.
    public let isExternal: Bool

    /// The dynamic-range badges the title screen shows for this track: one per format the probe named,
    /// the Dolby Vision one carrying its profile when recorded. See `DynamicRange.badges`.
    public var dynamicRangeBadges: [String] {
        DynamicRange.badges(hdrFormat: hdrFormat, dolbyVision: dolbyVision)
    }

    /// The one thing a viewer needs to know about a profile 7 file on this device, or nil.
    public var dolbyVisionNote: String? {
        DynamicRange.note(for: dolbyVision)
    }
}

/// A Dolby Vision configuration record as the server reports it: the profile (5, 7 or 8), its level, the
/// base-layer compatibility id (1 HDR10, 2 SDR, 4 HLG, 6 a UHD Blu-ray's HDR10 under profile 7) and whether
/// an enhancement layer is present — the mark of profile 7's dual layer.
public struct DolbyVisionDetail: Equatable, Sendable {
    public let profile: Int
    public let level: Int
    public let blCompatibilityId: Int
    public let enhancementLayer: Bool

    public init(profile: Int, level: Int, blCompatibilityId: Int, enhancementLayer: Bool) {
        self.profile = profile
        self.level = level
        self.blCompatibilityId = blCompatibilityId
        self.enhancementLayer = enhancementLayer
    }
}

/// How a stream's dynamic range is shown, kept out of the views so it can be tested.
public enum DynamicRange {
    /// "Dolby Vision 8.1" for profile 8 by its base layer, the bare profile otherwise ("Dolby Vision 7"),
    /// and the bare name while the profile is not recorded. The level is left out: it is 6 on nearly every
    /// film and tells a viewer nothing the profile does not.
    public static func label(for detail: DolbyVisionDetail?) -> String {
        guard let detail else { return "Dolby Vision" }
        return detail.profile == 8
            ? "Dolby Vision 8.\(detail.blCompatibilityId)"
            : "Dolby Vision \(detail.profile)"
    }

    /// One badge per format the probe named — "Dolby Vision · HDR10", what a profile 8.1 file honestly is,
    /// yields two — with nothing for SDR or an unknown range: a missing badge beats a false one.
    public static func badges(hdrFormat: String?, dolbyVision: DolbyVisionDetail?) -> [String] {
        guard let hdrFormat else { return [] }
        return hdrFormat
            .split(whereSeparator: { $0 == "·" || $0 == "," })
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty && $0.uppercased() != "SDR" }
            .map { $0.localizedCaseInsensitiveContains("Dolby Vision") ? label(for: dolbyVision) : $0 }
    }

    /// A dual layer is what no Apple device decodes, so this device plays the HDR10 base layer — said on the
    /// client, which knows what the device does where the server does not.
    public static func note(for detail: DolbyVisionDetail?) -> String? {
        guard let detail, detail.profile == 7 || detail.enhancementLayer else { return nil }
        return "Plays as HDR10 on this device"
    }
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
        self.hdrFormat = dto.hdrFormat
        self.dolbyVision = dto.dolbyVision.map {
            DolbyVisionDetail(
                profile: Int($0.profile), level: Int($0.level),
                blCompatibilityId: Int($0.blCompatibilityId), enhancementLayer: $0.enhancementLayer)
        }
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
