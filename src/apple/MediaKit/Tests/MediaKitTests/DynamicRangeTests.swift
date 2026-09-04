import MediaServerAPI
import Testing

@testable import MediaKit

/// How a stream's dynamic range reaches the title screen: the profile on the Dolby Vision badge when the
/// server recorded it, one badge per format the probe named, and the note a dual-layer profile 7 earns on
/// this device — none of which a view should have to work out for itself.
struct DynamicRangeTests {
    private let profile7 = DolbyVisionDetail(profile: 7, level: 6, blCompatibilityId: 6, enhancementLayer: true)
    private let profile81 = DolbyVisionDetail(profile: 8, level: 6, blCompatibilityId: 1, enhancementLayer: false)
    private let profile5 = DolbyVisionDetail(profile: 5, level: 6, blCompatibilityId: 0, enhancementLayer: false)

    @Test func theLabelNamesProfile8ByItsBaseLayerAndTheOthersByProfile() {
        #expect(DynamicRange.label(for: profile81) == "Dolby Vision 8.1")
        #expect(DynamicRange.label(for: profile7) == "Dolby Vision 7")
        #expect(DynamicRange.label(for: profile5) == "Dolby Vision 5")
        #expect(DynamicRange.label(for: nil) == "Dolby Vision")
    }

    @Test func oneBadgePerFormatTheProbeNamed() {
        // Production holds "Dolby Vision · HDR10" — what a profile 8.1 file honestly is.
        #expect(DynamicRange.badges(hdrFormat: "Dolby Vision · HDR10", dolbyVision: profile81) == ["Dolby Vision 8.1", "HDR10"])
        #expect(DynamicRange.badges(hdrFormat: "Dolby Vision", dolbyVision: profile7) == ["Dolby Vision 7"])
        #expect(DynamicRange.badges(hdrFormat: "HDR10+", dolbyVision: nil) == ["HDR10+"])
        #expect(DynamicRange.badges(hdrFormat: "Dolby Vision", dolbyVision: nil) == ["Dolby Vision"])
    }

    @Test func nothingForSdrOrAnUnknownRange() {
        #expect(DynamicRange.badges(hdrFormat: "SDR", dolbyVision: nil).isEmpty)
        #expect(DynamicRange.badges(hdrFormat: nil, dolbyVision: nil).isEmpty)
    }

    @Test func onlyADualLayerEarnsTheNote() {
        #expect(DynamicRange.note(for: profile7) == "Plays as HDR10 on this device")
        #expect(DynamicRange.note(for: profile81) == nil)
        #expect(DynamicRange.note(for: profile5) == nil)
        #expect(DynamicRange.note(for: nil) == nil)
    }

    @Test func theTrackDecodesTheRecordAndItsAbsence() {
        let recorded = TitleTrack(Components.Schemas.MediaStreamDto(
            id: "v", _type: "Video", index: 0, codec: "hevc", hdrFormat: "Dolby Vision",
            isDefault: true, isForced: false, isExternal: false,
            dolbyVision: .init(profile: 7, level: 6, blCompatibilityId: 6, enhancementLayer: true)))
        #expect(recorded.dolbyVision == profile7)
        #expect(recorded.dynamicRangeBadges == ["Dolby Vision 7"])
        #expect(recorded.dolbyVisionNote == "Plays as HDR10 on this device")

        let unrecorded = TitleTrack(Components.Schemas.MediaStreamDto(
            id: "v", _type: "Video", index: 0, codec: "hevc", hdrFormat: "Dolby Vision",
            isDefault: true, isForced: false, isExternal: false))
        #expect(unrecorded.dolbyVision == nil)
        #expect(unrecorded.dynamicRangeBadges == ["Dolby Vision"])
        #expect(unrecorded.dolbyVisionNote == nil)
    }
}
