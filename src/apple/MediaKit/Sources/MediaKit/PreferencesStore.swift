import Foundation

/// Where a viewer's playback choices survive a relaunch.
///
/// The escape hatch is worthless without this. Someone whose television shows a dark picture sets it to
/// SDR, closes the app, and the fault is back — which is worse than having no switch at all, because now
/// they have tried the switch and it did not work.
///
/// `UserDefaults` rather than the Keychain: nothing here is a secret, and a preference that survives
/// reinstall is not wanted — a viewer reinstalling to fix a picture should get the automatic answer back.
public struct PlaybackPreferencesStore: Sendable {
    private static let key = "playback.preferences"

    /// `UserDefaults` is documented as thread-safe but is not marked `Sendable`, so the guarantee has to
    /// be asserted here rather than inferred.
    private nonisolated(unsafe) let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    public func load() -> PlaybackPreferences {
        guard let data = defaults.data(forKey: Self.key),
              let stored = try? JSONDecoder().decode(PlaybackPreferences.self, from: data)
        else {
            // Nothing stored, or something stored by a version that wrote a different shape. Either way
            // the automatic answer is the right fallback, and it is what a fresh install gets.
            return PlaybackPreferences()
        }

        return stored
    }

    public func save(_ preferences: PlaybackPreferences) {
        guard let data = try? JSONEncoder().encode(preferences) else {
            return
        }

        defaults.set(data, forKey: Self.key)
    }

    public func clear() {
        defaults.removeObject(forKey: Self.key)
    }
}
