import Foundation
import MediaServerAPI

/// Asks the server how to play a title, and tells it how the watching went.
///
/// The negotiation is the server's: this sends what the device reports it can open, narrowed by whatever
/// the viewer has forced, and does as it is told. Nothing here decides that a file is playable — the one
/// place that knows what can be described is on the other side, and a client second-guessing it is how a
/// container arrives with no sound.
@MainActor
public final class PlaybackService {
    private let session: ServerSession
    private let preferences: PlaybackPreferencesStore
    private let device: any DeviceCapabilities

    /// - Parameter device: what to report as this machine's abilities. The real hardware by default; a
    ///   stated one where the question is what *a television* would be offered, which is not something
    ///   the machine running a diagnostic can answer about itself.
    public init(
        session: ServerSession,
        preferences: PlaybackPreferencesStore = PlaybackPreferencesStore(),
        device: any DeviceCapabilities = SystemCapabilities()
    ) {
        self.session = session
        self.preferences = preferences
        self.device = device
    }

    /// Every copy of a title, each with its own verdict, in the order the server listed them.
    ///
    /// - Parameters:
    ///   - audioStreamId: the track a viewer has just chosen. Absent means "decide for me", and the
    ///     server answers with their stored preference — so opening a film asks for nothing and
    ///     switching a dub is one field.
    ///   - subtitleStreamId: the same for words, with the same "absent decides for me".
    ///   - subtitlesOff: no subtitles, whatever the preference says. A separate field because absent
    ///     and none cannot be one value: a viewer whose preference names a language would otherwise be
    ///     handed it straight back, and the Off row in their picker would do nothing at all.
    public func plans(
        for itemId: String,
        audioStreamId: String? = nil,
        subtitleStreamId: String? = nil,
        subtitlesOff: Bool = false
    ) async throws -> [PlaybackPlan] {
        let profile = preferences.load().profile(for: device)
        let answer = try await session.api().postNativeV1PlaybackResolve(
            body: .json(.init(itemId: itemId, profile: .init(
                containers: profile.containers,
                videoCodecs: profile.videoCodecs,
                audioCodecs: profile.audioCodecs,
                hdrFormats: profile.hdrFormats,
                maxAudioChannels: profile.maxAudioChannels.map(Int32.init)),
                audioStreamId: audioStreamId,
                subtitleStreamId: subtitleStreamId,
                subtitlesOff: subtitlesOff)))

        return PlaybackPlan.all(try answer.ok.body.json, server: session.paired.server)
    }

    /// The copy to play.
    ///
    /// A viewer who picked a version gets that one when it plays — a picker that changes what is listed
    /// and not what happens is worse than no picker. Otherwise the first copy the server said it could
    /// deliver, because a title can hold a 4K copy this device cannot open beside a 1080p one it can.
    ///
    /// When nothing plays, the refusal returned is about the copy that was asked for rather than
    /// whichever happened to be listed first.
    public func plan(
        for itemId: String,
        preferring mediaSourceId: String? = nil,
        audioStreamId: String? = nil,
        subtitleStreamId: String? = nil,
        subtitlesOff: Bool = false
    ) async throws -> PlaybackPlan {
        let plans = try await plans(
            for: itemId, audioStreamId: audioStreamId, subtitleStreamId: subtitleStreamId,
            subtitlesOff: subtitlesOff)
        let requested = mediaSourceId.flatMap { wanted in
            plans.first { $0.mediaSourceId == wanted }
        }

        if let requested, requested.isPlayable {
            return requested
        }

        if let anyPlayable = plans.first(where: \.isPlayable) {
            return anyPlayable
        }

        return requested ?? plans.first ?? .refused(.noFile, source: "")
    }

    /// Opens a playback session. The id it returns is what progress and stop are reported against.
    public func start(
        itemId: String,
        mediaSourceId: String,
        positionSeconds: Double
    ) async throws -> String {
        try await session.api().postNativeV1PlaybackSessionsStart(
            body: .json(.init(
                itemId: itemId,
                mediaSourceId: mediaSourceId,
                deviceId: Self.deviceId,
                positionTicks: Self.ticks(positionSeconds))))
            .ok.body.json.playSessionId
    }

    public func report(itemId: String, playSessionId: String, positionSeconds: Double) async {
        // Progress is a courtesy to the rest of the library, not something playback depends on, so a
        // failure here must never interrupt what the viewer is watching.
        _ = try? await session.api().postNativeV1PlaybackSessionsProgress(
            body: .json(.init(
                itemId: itemId, playSessionId: playSessionId,
                positionTicks: Self.ticks(positionSeconds))))
    }

    public func stop(itemId: String, playSessionId: String, positionSeconds: Double) async {
        _ = try? await session.api().postNativeV1PlaybackSessionsStop(
            body: .json(.init(
                itemId: itemId, playSessionId: playSessionId,
                positionTicks: Self.ticks(positionSeconds))))
    }

    /// Hundred-nanosecond units, which is what the library stores and what Jellyfin's vocabulary uses.
    private static func ticks(_ seconds: Double) -> Int64 {
        Int64((seconds * 10_000_000).rounded())
    }

    /// Stable for the life of the install, so the server can tell this television from another one.
    ///
    /// `identifierForVendor` rather than anything of our own: it survives a relaunch, changes when the
    /// app is removed, and is not a number that follows a person anywhere.
    private static let deviceId: String = {
        #if os(tvOS) || os(iOS)
        return UIDevice.current.identifierForVendor?.uuidString ?? UUID().uuidString
        #else
        return Host.current().localizedName ?? UUID().uuidString
        #endif
    }()
}

#if os(tvOS) || os(iOS)
import UIKit
#endif
