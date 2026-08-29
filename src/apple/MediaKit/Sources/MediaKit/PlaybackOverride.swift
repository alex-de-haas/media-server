/// The escape hatch: what a viewer can force when the automatic answer is wrong on their television.
///
/// It exists because one thing genuinely cannot be detected. `VTIsHardwareDecodeSupported` reports what
/// the *box* decodes, not what the *panel* shows, and an Apple TV 4K wired to an SDR display through a
/// receiver that strips the signalling still says yes. The symptom is a washed-out or dark picture, and
/// without a switch it is a bug report with nothing to act on.
///
/// The override narrows the profile before it is sent, so the server's own negotiation does the work and
/// there is no second decision path to keep in step.
public enum DynamicRangeOverride: String, Codable, CaseIterable, Sendable {
    /// Ask for everything the hardware reports. Right for almost everyone.
    case automatic

    /// Drop Dolby Vision, keep HDR10. For a display that engages Dolby Vision badly rather than not at all.
    case hdr10

    /// Drop both. The picture that always works.
    case sdr

    public func apply(to profile: CapabilityProfile) -> CapabilityProfile {
        var narrowed = profile
        switch self {
        case .automatic:
            break
        case .hdr10:
            narrowed.hdrFormats = profile.hdrFormats.filter { $0 != "Dolby Vision" }
        case .sdr:
            narrowed.hdrFormats = ["SDR"]
        }

        return narrowed
    }
}

/// A viewer's stored preferences, applied to the detected profile on every resolve.
///
/// Deliberately not a general "compatibility mode". There is one transport today, so a switch for it
/// would be a control that does nothing — and a setting that does nothing is worse than an absent one,
/// because it becomes the first thing a puzzled viewer changes. When HLS arrives it earns its own case.
public struct PlaybackPreferences: Codable, Equatable, Sendable {
    public var dynamicRange: DynamicRangeOverride
    public var maxAudioChannels: Int?

    /// Whether the player shows what it is doing: position, buffer, stalls, observed rate.
    ///
    /// Off by default and deliberately in the way when on. A television has no console to read and no
    /// file a viewer can reach, so the only diagnostic that gets used is the one on screen.
    public var showDiagnostics: Bool

    public init(
        dynamicRange: DynamicRangeOverride = .automatic,
        maxAudioChannels: Int? = nil,
        showDiagnostics: Bool = false
    ) {
        self.dynamicRange = dynamicRange
        self.maxAudioChannels = maxAudioChannels
        self.showDiagnostics = showDiagnostics
    }

    /// Absent in anything written before a switch existed, which must read as off rather than as a
    /// preference that cannot be decoded — losing a viewer's dynamic-range choice, the one control that
    /// fixes a dark picture, along with it.
    public init(from decoder: any Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        dynamicRange = try container.decodeIfPresent(
            DynamicRangeOverride.self, forKey: .dynamicRange) ?? .automatic
        maxAudioChannels = try container.decodeIfPresent(Int.self, forKey: .maxAudioChannels)
        showDiagnostics = try container.decodeIfPresent(Bool.self, forKey: .showDiagnostics) ?? false
    }

    /// The profile actually sent: what the device reports, narrowed by what the viewer has chosen.
    public func profile(for device: some DeviceCapabilities = SystemCapabilities()) -> CapabilityProfile {
        dynamicRange.apply(
            to: .current(device, maxAudioChannels: maxAudioChannels))
    }
}
