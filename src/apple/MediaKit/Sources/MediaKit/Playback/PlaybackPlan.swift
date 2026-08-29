import Foundation
import MediaServerAPI

/// How the server decided this title should be delivered.
public enum PlaybackDecision: String, Sendable {
    /// The file as it is, served by byte range.
    case directPlay

    /// The same streams in a container this client can open, computed over the untouched file.
    case remux
}

/// A stream this client may open, and what the server chose to make it.
public struct PlayableStream: Equatable, Sendable, Identifiable {
    /// The source, because that is what makes one stream a different one: resolving a title twice is the
    /// same playback, and a viewer switching copy is not.
    public var id: String { mediaSourceId }

    public let url: URL
    public let mediaSourceId: String
    public let versionName: String?
    public let decision: PlaybackDecision

    /// `dvh1` when Dolby Vision was asked for, `hvc1` when the cross-compatible entry was, and nothing
    /// on the direct-play path — there the sample entry is whatever was written on disk, and promising
    /// a choice would be a promise nothing keeps.
    public let signalling: String?

    /// What the source actually is, as opposed to what is being sent: `Dolby Vision`, `HDR10`, `SDR`.
    public let sourceDynamicRange: String?

    /// The tracks this URL carries, as the server decided them — which is not always what was asked
    /// for: a stored preference answers when nothing was picked, and a picked track belonging to
    /// another edition is refused. A picker ticks these rather than its own last request.
    ///
    /// Null on the direct-play path, where the file is served as it stands and the choice was never
    /// the server's to make.
    public let audioStreamId: String?
    public let subtitleStreamId: String?
}

/// Why a title cannot be played, in terms a screen can say out loud.
///
/// Machine-readable on the wire so a client can say "this copy's only audio track is DTS" rather than
/// failing silently, and unknown reasons are carried rather than flattened: an older client meeting a
/// newer server must not turn a specific answer into "cannot play this".
public enum PlaybackRefusal: Equatable, Sendable {
    case unsupportedVideoCodec
    case unsupportedAudioCodec
    case unsupportedDynamicRange
    case noAudioTrack
    case packagingUnavailable

    /// Not yet. The walk that makes a source playable has not reached it, and on a spinning disk that is
    /// minutes rather than seconds. Retrying later works, which makes this a state and not an error.
    case packagingPending

    case packagingUnsupportedAudio
    case packagingUnsupportedVideo
    case noFile
    case unknown(String)

    init(_ reason: String?) {
        switch reason {
        case "unsupported_video_codec": self = .unsupportedVideoCodec
        case "unsupported_audio_codec": self = .unsupportedAudioCodec
        case "unsupported_dynamic_range": self = .unsupportedDynamicRange
        case "no_audio_track": self = .noAudioTrack
        case "packaging_unavailable": self = .packagingUnavailable
        case "packaging_pending": self = .packagingPending
        case "packaging_unsupported_audio": self = .packagingUnsupportedAudio
        case "packaging_unsupported_video": self = .packagingUnsupportedVideo
        case "no_file": self = .noFile
        case let other: self = .unknown(other ?? "unspecified")
        }
    }

    /// Whether waiting is the remedy. Only one answer means "not yet" rather than "no".
    public var isPending: Bool { self == .packagingPending }
}

/// What the server answered about **one copy** of a title.
///
/// A refusal carries the copy it is about, not only the reason. Without that there is no way to tell a
/// viewer why the version they picked will not play, or to honour their pick at all — which is exactly
/// the bug this shape was introduced to fix.
public enum PlaybackPlan: Equatable, Sendable {
    case play(PlayableStream)
    case refused(PlaybackRefusal, source: String)

    public var mediaSourceId: String {
        switch self {
        case .play(let stream): stream.mediaSourceId
        case .refused(_, let source): source
        }
    }

    public var isPlayable: Bool {
        if case .play = self { return true }
        return false
    }
}

extension PlaybackPlan {
    /// The plan for each of a title's copies, in the order the server listed them.
    ///
    /// `resolve` answers **per source**, not once: a title can hold a 4K copy this device cannot open
    /// beside a 1080p one it can, and collapsing that to a single verdict would hide the copy that
    /// works. The caller takes the first that plays.
    static func all(_ dto: Components.Schemas.NativePlaybackResolutionResponse, server: URL) -> [PlaybackPlan] {
        dto.sources.map { PlaybackPlan($0, server: server) }
    }

    /// Built from one source's resolution, and refused when it does not amount to something openable.
    init(_ dto: Components.Schemas.NativePlaybackResolution, server: URL) {
        guard dto.decision != .unsupported else {
            self = .refused(PlaybackRefusal(dto.reason), source: dto.mediaSourceId)
            return
        }

        // A decision that is not "unsupported" but carries no URL is a server contradicting itself.
        // Treating it as playable would fail inside AVFoundation, where the reason is lost.
        guard let path = dto.url, let url = URL(string: path, relativeTo: server)?.absoluteURL else {
            self = .refused(PlaybackRefusal(dto.reason), source: dto.mediaSourceId)
            return
        }

        // HLS is deliberately unbuilt on the server, so a client meeting it has met a server newer than
        // itself. Saying so beats handing AVFoundation a playlist this build cannot reason about.
        guard dto.transport != .hls else {
            self = .refused(.unknown("transport_hls"), source: dto.mediaSourceId)
            return
        }

        self = .play(PlayableStream(
            url: url,
            mediaSourceId: dto.mediaSourceId,
            versionName: dto.versionName,
            decision: dto.decision == .directPlay ? .directPlay : .remux,
            signalling: dto.signalling,
            sourceDynamicRange: dto.sourceDynamicRange,
            audioStreamId: dto.audioStreamId,
            subtitleStreamId: dto.subtitleStreamId))
    }
}
