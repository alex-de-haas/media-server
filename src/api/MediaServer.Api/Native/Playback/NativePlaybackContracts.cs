namespace MediaServer.Api.Native.Playback;

/// <summary>
/// What a client says it can open. A request body rather than a stored entity, so a fifth axis can be
/// added later without a migration.
///
/// The axes are the ones the Apple TV spike showed actually decide playback — container, video codec,
/// audio codec, dynamic range, channel count. A coarse "device class" was rejected: it ages badly and
/// cannot express the one distinction the spike proved is real, which is whether a client engages
/// Dolby Vision. See <c>docs/features/native-playback/feature.md</c>.
/// </summary>
public sealed record NativeCapabilityProfile(
    IReadOnlyList<string> Containers,
    IReadOnlyList<string> VideoCodecs,
    IReadOnlyList<string> AudioCodecs,
    IReadOnlyList<string> HdrFormats,
    int? MaxAudioChannels = null);

public enum NativePlaybackDecision
{
    /// <summary>The original file, served by byte range.</summary>
    DirectPlay,

    /// <summary>The same streams repackaged into a container this client can open.</summary>
    Remux,

    /// <summary>Nothing this client can play, with a reason it can show a viewer.</summary>
    Unsupported,
}

/// <summary>
/// Machine-readable so a client can say "this copy's only audio track is DTS" rather than failing
/// silently. Strings rather than an enum on the wire: an unknown reason must not break an older client.
/// </summary>
public static class NativePlaybackReasons
{
    public const string UnsupportedVideoCodec = "unsupported_video_codec";
    public const string UnsupportedAudioCodec = "unsupported_audio_codec";
    public const string UnsupportedDynamicRange = "unsupported_dynamic_range";
    public const string NoAudioTrack = "no_audio_track";
    public const string PackagingUnavailable = "packaging_unavailable";
    public const string NoFile = "no_file";
}

/// <summary>
/// Which HEVC sample entry a client is served, which the spike proved is not cosmetic: a
/// <c>dvh1</c> entry engages Dolby Vision on a device that supports it and breaks one that does not,
/// while the cross-compatible form reads as HDR10 everywhere.
/// </summary>
public static class NativeSignalling
{
    /// <summary>Cross-compatible: <c>hvc1</c> with a <c>dvvC</c> box, which a non-DV client reads as HDR10.</summary>
    public const string CrossCompatible = "hvc1";

    /// <summary>Dolby Vision proper. Only ever offered to a client that reported DV support.</summary>
    public const string DolbyVision = "dvh1";
}

public sealed record NativePlaybackResolution(
    Guid MediaSourceId,
    string? VersionName,
    NativePlaybackDecision Decision,
    string? Url,
    string? Signalling,
    string? Reason);

public sealed record NativePlaybackResolutionResponse(
    Guid ItemId,
    IReadOnlyList<NativePlaybackResolution> Sources);
