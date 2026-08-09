namespace MediaServer.Api.Remux;

/// <summary>
/// What packaging can actually describe, in both vocabularies that ask.
///
/// The two must agree, which is why they live together. The resolver decides whether to offer a remux at
/// all, and it reasons in the library's probe vocabulary; the synthesiser writes the sample entries, and it
/// reasons in Matroska's. When they drifted apart the result was the worst kind of failure: a source the
/// resolver advertised as remuxable, whose only audio track the synthesiser then quietly declined to
/// describe, producing a playable-looking file with no sound.
/// </summary>
internal static class RemuxCodecs
{
    /// <summary>
    /// Audio a sample entry can be written for, named as the probe names it.
    ///
    /// AC-3 and E-AC-3, which is what this library actually holds — and E-AC-3 is what Atmos rides on.
    /// AAC would need <c>mp4a</c> with an <c>esds</c>; DTS and TrueHD are out of scope for this client
    /// entirely. Each absence is deliberate rather than an oversight — see the plan.
    /// </summary>
    internal static bool CanPackageAudio(string? probeCodec) =>
        probeCodec is not null
        && (probeCodec.Equals("ac3", StringComparison.OrdinalIgnoreCase)
            || probeCodec.Equals("eac3", StringComparison.OrdinalIgnoreCase));

    /// <summary>The same answer, for a Matroska <c>CodecID</c>.</summary>
    internal static bool CanPackageAudio(IndexedTrack track) =>
        track.CodecId is "A_AC3" or "A_EAC3";

    /// <summary>Video a sample entry can be written for.</summary>
    internal static bool CanPackageVideo(IndexedTrack track) =>
        Mp4Writer.VideoCodec(track.CodecId) is not null && track.CodecPrivate is not null;
}
