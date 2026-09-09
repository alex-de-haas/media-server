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
    public let language: String?
    public let codec: String?
    public let title: String?
    public let channels: Int?
    /// Server-defined nominal resolution, absent when dimensions are unknown or on older servers.
    public let resolutionLabel: String?

    /// Language leads so identically named "full" or "forced" tracks remain distinguishable.
    public func menuTitle(locale: Locale = .current) -> String {
        let code = language?.trimmingCharacters(in: .whitespacesAndNewlines)
        let languageName: String
        if let code, !code.isEmpty, code.lowercased() != "und" {
            languageName = locale.localizedString(forIdentifier: code.replacingOccurrences(of: "_", with: "-")) ?? code
        } else {
            languageName = "Unknown language"
        }
        guard let title, !title.isEmpty,
              title.caseInsensitiveCompare(languageName) != .orderedSame,
              title.caseInsensitiveCompare(code ?? "") != .orderedSame else { return languageName }
        return "\(languageName) · \(title)"
    }

    /// Channel count alone does not establish a layout such as 5.1 or 7.1.
    public var audioMenuDetails: String? {
        let rawCodec = codec?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        let codecName = rawCodec.map { name in
            switch name {
            case "ac3": "AC-3"
            case "eac3": "E-AC-3"
            default: name.uppercased()
            }
        }
        let channelCount = channels.flatMap { $0 > 0 ? "\($0) ch" : nil }
        let parts = [codecName, channelCount].compactMap { $0 }.filter { !$0.isEmpty }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

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

    /// The HDR10 fallback notice for source configurations this client cannot play as Dolby Vision.
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

    /// Match the server's HDR10 fallback for profile 7, enhancement layers, and profile 8 with
    /// compatibility ID 6. The server only signals single-layer profile 8 as Dolby Vision for IDs 1 or 4.
    public static func note(for detail: DolbyVisionDetail?) -> String? {
        guard let detail else { return nil }
        let profile86 = detail.profile == 8 && detail.blCompatibilityId == 6
        guard detail.profile == 7 || detail.enhancementLayer || profile86 else { return nil }
        return "Plays as HDR10 on this device"
    }
}

/// A cast credit in the server's billing order.
public struct TitleCastMember: Identifiable, Equatable, Sendable {
    public let id: String
    public let name: String
    public let character: String?
    public let profileURL: URL?
}

public struct TitleCrewMember: Identifiable, Equatable, Sendable {
    public let id: String
    public let name: String
    public let job: String
    public let profileURL: URL?
}

/// Everything a title's own screen shows.
public struct TitleDetail: Equatable, Sendable {
    public let kind: String
    public let seasons: [TitleSeason]
    public var isSeries: Bool { kind == "Series" }
    public let id: String
    public let title: String
    public let year: Int?
    public let overview: String?
    public let tagline: String?
    public let genres: [String]
    public let runtimeSeconds: Double?
    public let communityRating: Double?
    public let officialRating: String?
    public let cast: [TitleCastMember]
    public let crew: [TitleCrewMember]
    public let directors: [String]
    public let creators: [String]

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
        self.kind = detail.kind
        self.seasons = (detail.seasons ?? []).filter { $0.episodeCount > 0 }.map {
            TitleSeason(id: $0.id, number: $0.seasonNumber.map(Int.init), title: $0.title, episodeCount: Int($0.episodeCount))
        }.sorted { ($0.number ?? Int.max, $0.id) < ($1.number ?? Int.max, $1.id) }
        self.id = detail.id
        self.title = detail.title
        self.year = detail.year.map(Int.init)
        self.overview = detail.overview
        self.tagline = detail.tagline
        self.genres = detail.genres ?? []
        self.runtimeSeconds = detail.runtimeTicks.map { Double($0) / 10_000_000 }
        self.communityRating = detail.communityRating
        self.officialRating = detail.officialRating
        self.cast = detail.cast.map { credit in
            TitleCastMember(id: "\(credit.provider):\(credit.providerId)", name: credit.name,
                            character: credit.character, profileURL: credit.profileUrl.flatMap(URL.init(string:)))
        }
        self.directors = detail.directors
        self.creators = detail.creators
        var crew = (detail.crew ?? []).map {
            TitleCrewMember(id: $0.id, name: $0.name, job: $0.job ?? $0.department ?? "Crew",
                            profileURL: $0.profileUrl.flatMap(URL.init(string:)))
        }
        // Older servers and unenriched person records can still provide these name-only credits.
        for (job, names) in [("Director", detail.directors), ("Creator", detail.creators)] {
            for name in names where !crew.contains(where: { $0.name == name && $0.job == job }) {
                crew.append(TitleCrewMember(id: "legacy-\(job)-\(name)", name: name, job: job, profileURL: nil))
            }
        }
        self.crew = crew
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
        self.title = dto.title?.trimmingCharacters(in: .whitespacesAndNewlines)
        self.channels = dto.channels.map(Int.init)
        self.resolutionLabel = dto.resolutionLabel
        self.hdrFormat = dto.hdrFormat
        self.dolbyVision = dto.dolbyVision.map {
            DolbyVisionDetail(
                profile: Int($0.profile), level: Int($0.level),
                blCompatibilityId: Int($0.blCompatibilityId), enhancementLayer: $0.enhancementLayer)
        }
        self.isExternal = dto.isExternal ?? false
    }
}
