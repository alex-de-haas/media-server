import AVFoundation
import VideoToolbox

/// What this device can open, in the shape the server's `/native/v1/playback/resolve` expects.
///
/// Five axes, and they are the server's own — container, video codec, audio codec, dynamic range and
/// channel count. See `docs/features/native-playback/feature.md`.
public struct CapabilityProfile: Codable, Equatable, Sendable {
    public var containers: [String]
    public var videoCodecs: [String]
    public var audioCodecs: [String]
    public var hdrFormats: [String]
    public var maxAudioChannels: Int?

    public init(
        containers: [String],
        videoCodecs: [String],
        audioCodecs: [String],
        hdrFormats: [String],
        maxAudioChannels: Int? = nil
    ) {
        self.containers = containers
        self.videoCodecs = videoCodecs
        self.audioCodecs = audioCodecs
        self.hdrFormats = hdrFormats
        self.maxAudioChannels = maxAudioChannels
    }
}

/// What the running hardware actually reports, as opposed to what its model number suggests.
///
/// The distinction that matters is Dolby Vision, and it is not a property of "Apple TV" — a 4K box has
/// it and an older one does not, and the same binary runs on both. Asking the model would be a table
/// that ages every autumn; asking VideoToolbox is the answer for the device the code is running on right
/// now. The one thing that cannot be asked is the *display*: an Apple TV 4K plugged into an SDR panel
/// still reports Dolby Vision decode support, which is why the escape hatches exist.
public protocol DeviceCapabilities: Sendable {
    /// Whether the hardware decodes Dolby Vision HEVC.
    var decodesDolbyVision: Bool { get }

    /// Whether the current output chain is eligible for HDR at all.
    var presentsHDR: Bool { get }
}

/// The real device, asked directly.
public struct SystemCapabilities: DeviceCapabilities {
    public init() {}

    public var decodesDolbyVision: Bool {
        VTIsHardwareDecodeSupported(kCMVideoCodecType_DolbyVisionHEVC)
    }

    public var presentsHDR: Bool {
        #if os(tvOS) || os(iOS)
        return AVPlayer.eligibleForHDRPlayback
        #else
        // macOS has no `eligibleForHDRPlayback`, and the honest answer would come from the screen the
        // window is on — `NSScreen.maximumPotentialExtendedDynamicRangeColorComponentValue` — which is
        // main-actor state this synchronous property has no business reaching for, and which is
        // meaningless before there is a window.
        //
        // So: false, until there is a macOS client to ask it properly. Under-claiming costs an SDR
        // picture that always works; over-claiming asks the server for signalling the display cannot
        // present, and the spike established that such a track does not degrade — it breaks.
        return false
        #endif
    }
}

extension CapabilityProfile {
    /// Containers AVFoundation opens. Matroska is deliberately absent — that it is absent is the entire
    /// reason the server repackages.
    static let appleContainers = ["mp4", "m4v", "mov"]

    /// What every target device decodes. AV1 is left out on purpose: recent hardware decodes it, but the
    /// server has no sample entry for it, and claiming it here would earn a refusal at the request
    /// rather than an honest `unsupported` at resolve time.
    static let appleVideoCodecs = ["hevc", "h264"]

    /// AAC, AC-3 and E-AC-3 — which is what the server can package, and Atmos rides on the last.
    static let appleAudioCodecs = ["aac", "ac3", "eac3"]

    /// Built from what the device reports rather than from what it is called.
    public static func current(
        _ device: some DeviceCapabilities = SystemCapabilities(),
        maxAudioChannels: Int? = nil
    ) -> CapabilityProfile {
        var hdr = ["SDR"]
        if device.presentsHDR {
            hdr.append("HDR10")
            // Dolby Vision only when both hold. Decode without an HDR-eligible output would ask the
            // server for a `dvh1` entry the display cannot show, and the spike established that a
            // `dvh1` track on a device that cannot present it does not degrade — it breaks.
            if device.decodesDolbyVision {
                hdr.append("Dolby Vision")
            }
        }

        return CapabilityProfile(
            containers: appleContainers,
            videoCodecs: appleVideoCodecs,
            audioCodecs: appleAudioCodecs,
            hdrFormats: hdr,
            maxAudioChannels: maxAudioChannels
        )
    }
}
