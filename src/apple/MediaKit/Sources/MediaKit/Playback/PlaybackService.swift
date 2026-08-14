import Foundation
import MediaServerAPI
import Observation

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

    public init(session: ServerSession, preferences: PlaybackPreferencesStore = PlaybackPreferencesStore()) {
        self.session = session
        self.preferences = preferences
    }

    /// Every copy of a title, each with its own verdict, in the order the server listed them.
    public func plans(for itemId: String) async throws -> [PlaybackPlan] {
        let profile = preferences.load().profile()
        let answer = try await session.api().postNativeV1PlaybackResolve(
            body: .json(.init(itemId: itemId, profile: .init(
                containers: profile.containers,
                videoCodecs: profile.videoCodecs,
                audioCodecs: profile.audioCodecs,
                hdrFormats: profile.hdrFormats,
                maxAudioChannels: profile.maxAudioChannels.map(Int32.init)))))

        return PlaybackPlan.all(try answer.ok.body.json, server: session.paired.server)
    }

    /// The copy to play: the first the server said it could deliver.
    ///
    /// When none can be, the refusal returned is the first one — the default copy's, since that is the
    /// order the server lists them in, and it is the answer about the copy a viewer would have got.
    public func plan(for itemId: String) async throws -> PlaybackPlan {
        let plans = try await plans(for: itemId)
        if let playable = plans.first(where: { if case .play = $0 { return true } else { return false } }) {
            return playable
        }

        return plans.first ?? .refused(.noFile)
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
