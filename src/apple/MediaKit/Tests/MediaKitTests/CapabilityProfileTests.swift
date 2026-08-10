import Foundation
import Testing

@testable import MediaKit

private struct StubDevice: DeviceCapabilities {
    var decodesDolbyVision: Bool
    var presentsHDR: Bool
}

@Suite("Capability profile")
struct CapabilityProfileTests {
    @Test("An Apple TV 4K offers Dolby Vision because the hardware says so, not because of its name")
    func dolbyVisionWhenBothHold() {
        let profile = CapabilityProfile.current(
            StubDevice(decodesDolbyVision: true, presentsHDR: true))

        #expect(profile.hdrFormats == ["SDR", "HDR10", "Dolby Vision"])
    }

    @Test("Decoding Dolby Vision without an HDR-eligible output is not enough")
    func decodeAloneIsNotEnough() {
        // The spike established that a dvh1 track on a device that cannot present it does not degrade
        // gracefully — it breaks. So both answers have to hold before we ask for one.
        let profile = CapabilityProfile.current(
            StubDevice(decodesDolbyVision: true, presentsHDR: false))

        #expect(profile.hdrFormats == ["SDR"])
    }

    @Test("An older box gets HDR10 and no more")
    func hdr10Only() {
        let profile = CapabilityProfile.current(
            StubDevice(decodesDolbyVision: false, presentsHDR: true))

        #expect(profile.hdrFormats == ["SDR", "HDR10"])
    }

    @Test("Matroska is never claimed, which is the whole reason the server repackages")
    func noMatroska() {
        let profile = CapabilityProfile.current(StubDevice(decodesDolbyVision: true, presentsHDR: true))

        #expect(!profile.containers.contains("mkv"))
        #expect(profile.containers == ["mp4", "m4v", "mov"])
    }

    @Test("AV1 is not claimed even though recent hardware decodes it")
    func noAv1() {
        // The server has no sample entry for AV1. Claiming it here would earn a refusal at the request
        // instead of an honest `unsupported` at resolve time.
        let profile = CapabilityProfile.current(StubDevice(decodesDolbyVision: true, presentsHDR: true))

        #expect(!profile.videoCodecs.contains("av1"))
    }

    @Test("Audio is what the server can package")
    func packageableAudio() {
        let profile = CapabilityProfile.current(StubDevice(decodesDolbyVision: false, presentsHDR: false))

        #expect(profile.audioCodecs == ["aac", "ac3", "eac3"])
        #expect(!profile.audioCodecs.contains("dts"))
        #expect(!profile.audioCodecs.contains("truehd"))
        #expect(!profile.audioCodecs.contains("flac"))
    }

    @Test("The wire shape is the server's own field names")
    func wireShape() throws {
        let encoded = try JSONEncoder().encode(
            CapabilityProfile(
                containers: ["mp4"], videoCodecs: ["hevc"], audioCodecs: ["aac"],
                hdrFormats: ["SDR"], maxAudioChannels: 6))
        let json = try #require(
            try JSONSerialization.jsonObject(with: encoded) as? [String: Any])

        #expect(json.keys.sorted() == [
            "audioCodecs", "containers", "hdrFormats", "maxAudioChannels", "videoCodecs",
        ])
    }

    @Test("An unstated channel limit is absent from the body rather than sent as zero")
    func channelsOmitted() throws {
        let encoded = try JSONEncoder().encode(
            CapabilityProfile(
                containers: ["mp4"], videoCodecs: ["hevc"], audioCodecs: ["aac"], hdrFormats: ["SDR"]))
        let json = try #require(try JSONSerialization.jsonObject(with: encoded) as? [String: Any])

        #expect(json["maxAudioChannels"] == nil)
    }
}

@Suite("Playback preferences")
struct PlaybackPreferencesTests {
    private let capable = StubDevice(decodesDolbyVision: true, presentsHDR: true)

    @Test("Automatic asks for everything the hardware reports")
    func automatic() {
        let profile = PlaybackPreferences().profile(for: capable)

        #expect(profile.hdrFormats == ["SDR", "HDR10", "Dolby Vision"])
    }

    @Test("Forcing HDR10 drops Dolby Vision and keeps the rest")
    func forceHdr10() {
        let profile = PlaybackPreferences(dynamicRange: .hdr10).profile(for: capable)

        #expect(profile.hdrFormats == ["SDR", "HDR10"])
    }

    @Test("Forcing SDR is the picture that always works")
    func forceSdr() {
        let profile = PlaybackPreferences(dynamicRange: .sdr).profile(for: capable)

        #expect(profile.hdrFormats == ["SDR"])
    }

    @Test("An override narrows the profile and never widens it")
    func neverWidens() {
        // A device with no HDR at all must not be given HDR10 by a viewer choosing it in a menu.
        let limited = StubDevice(decodesDolbyVision: false, presentsHDR: false)

        for override in DynamicRangeOverride.allCases {
            let profile = PlaybackPreferences(dynamicRange: override).profile(for: limited)
            #expect(profile.hdrFormats == ["SDR"])
        }
    }

    @Test("A channel limit travels with the profile")
    func channelLimit() {
        let profile = PlaybackPreferences(maxAudioChannels: 2).profile(for: capable)

        #expect(profile.maxAudioChannels == 2)
    }
}

@Suite("Preferences store")
struct PlaybackPreferencesStoreTests {
    private func store() -> (PlaybackPreferencesStore, UserDefaults) {
        let suite = "MediaKitTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        return (PlaybackPreferencesStore(defaults: defaults), defaults)
    }

    @Test("A choice survives the relaunch it exists to survive")
    func roundTrip() {
        let (subject, _) = store()

        subject.save(PlaybackPreferences(dynamicRange: .sdr, maxAudioChannels: 2))
        let loaded = subject.load()

        #expect(loaded.dynamicRange == .sdr)
        #expect(loaded.maxAudioChannels == 2)
    }

    @Test("A fresh install gets the automatic answer")
    func freshInstall() {
        let (subject, _) = store()

        #expect(subject.load() == PlaybackPreferences())
    }

    @Test("Something stored in a shape this version cannot read falls back rather than throwing")
    func unreadable() {
        let (subject, defaults) = store()
        defaults.set(Data([0x00, 0x01, 0x02]), forKey: "playback.preferences")

        #expect(subject.load() == PlaybackPreferences())
    }

    @Test("Clearing returns the device to automatic")
    func clearing() {
        let (subject, _) = store()
        subject.save(PlaybackPreferences(dynamicRange: .hdr10))

        subject.clear()

        #expect(subject.load().dynamicRange == .automatic)
    }
}
