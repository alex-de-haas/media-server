import Foundation
import Testing

@testable import MediaKit

@Suite("Playback cache preferences")
struct PlaybackCachePreferencesTests {
    @Test("Fresh and legacy installations keep the memory cache")
    func legacyDefaults() throws {
        #expect(PlaybackPreferences().usesOwnLoader)
        #expect(PlaybackPreferences().cacheStorage == .memory)
        let preferences = try JSONDecoder().decode(PlaybackPreferences.self,
            from: Data(#"{"dynamicRange":"hdr10","showDiagnostics":true}"#.utf8))
        #expect(preferences.usesOwnLoader)
        #expect(preferences.cacheStorage == .memory)
        #expect(preferences.dynamicRange == .hdr10)
        #expect(preferences.showDiagnostics)
    }

    @Test("The old disabled loader setting remains playback without an additional cache")
    func legacyDisabled() throws {
        let preferences = try JSONDecoder().decode(PlaybackPreferences.self,
            from: Data(#"{"usesOwnLoader":false,"dynamicRange":"sdr"}"#.utf8))
        #expect(!preferences.usesOwnLoader)
        #expect(preferences.cacheStorage == .memory)
        #expect(preferences.dynamicRange == .sdr)
    }

    @Test("Storage survives disabling caching and reopening preferences",
          arguments: PlaybackCacheStorage.allCases)
    func persistence(_ storage: PlaybackCacheStorage) throws {
        let suite = "PlaybackCacheTests." + UUID().uuidString
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let store = PlaybackPreferencesStore(defaults: defaults)
        var preferences = PlaybackPreferences(dynamicRange: .hdr10, maxAudioChannels: 2,
            showDiagnostics: true, usesOwnLoader: true, cacheStorage: storage)
        store.save(preferences)
        #expect(store.load() == preferences)

        preferences.usesOwnLoader = false
        store.save(preferences)
        let reopenedStore = PlaybackPreferencesStore(defaults: defaults)
        var reopened = reopenedStore.load()
        #expect(reopened == preferences)
        reopened.usesOwnLoader = true
        reopenedStore.save(reopened)
        #expect(store.load().cacheStorage == storage)
        #expect(store.load().usesOwnLoader)
    }

    @Test("An unknown storage choice falls back without losing other preferences")
    func unknownStorage() throws {
        let preferences = try JSONDecoder().decode(PlaybackPreferences.self,
            from: Data(#"{"cacheStorage":"future-storage","usesOwnLoader":false,"dynamicRange":"sdr","showDiagnostics":true}"#.utf8))
        #expect(preferences.cacheStorage == .memory)
        #expect(!preferences.usesOwnLoader)
        #expect(preferences.dynamicRange == .sdr)
        #expect(preferences.showDiagnostics)
    }
}
