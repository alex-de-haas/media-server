namespace MediaServer.Api.Probe;

/// <summary>What a container header says a track is. Deliberately close to the bytes: mapping onto the
/// library's own vocabulary happens in <see cref="HeaderMediaProbe"/>.</summary>
internal enum HeaderTrackKind
{
    Video,
    Audio,
    Subtitle,
    /// <summary>Data, attachments, embedded cover art — kept so indexes stay comparable with ffprobe's.</summary>
    Other,
}

/// <summary>
/// How a video track carries brightness, to the extent a header can say. <see cref="Unknown"/> and
/// <see cref="Sdr"/> are distinct: the first means the container stated nothing, the second that it stated
/// a non-HDR transfer function. Collapsing them would let a missing field read as a claim.
/// </summary>
internal enum HeaderHdr
{
    Unknown,
    Sdr,
    /// <summary>PQ, but a header cannot tell HDR10 from HDR10+.</summary>
    Hdr,
    Hlg,
    DolbyVision,
}

/// <summary>One track as its container describes it.</summary>
internal sealed record HeaderTrack(
    int Index,
    HeaderTrackKind Kind,
    string Codec,
    string? Language,
    string? Title,
    bool IsDefault,
    bool IsForced,
    int? Width,
    int? Height,
    double? FrameRate,
    int? BitDepth,
    HeaderHdr Hdr,
    int? Channels,
    int? SampleRate,
    DolbyVisionDetail? DolbyVision = null)
{
    /// <summary>
    /// Stands in for the stream ffprobe synthesizes from embedded artwork. It is reported as video because
    /// that is what ffprobe calls it, so the two providers agree on the whole stream list and not merely on
    /// the indexes of the tracks around it.
    /// </summary>
    public static HeaderTrack CoverArt(int index) =>
        new(index, HeaderTrackKind.Video, "mjpeg", null, null, false, false, null, null, null, null, HeaderHdr.Unknown, null, null);
}
