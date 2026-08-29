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
    /// AC-3 and E-AC-3, which is what most of this library holds — and E-AC-3 is what Atmos rides on —
    /// plus AAC, which the anime half of it holds and nothing else could play. DTS, TrueHD and FLAC are
    /// out of scope for this client. Each absence is deliberate rather than an oversight — see the plan.
    /// </summary>
    internal static bool CanPackageAudio(string? probeCodec) =>
        probeCodec is not null
        && (probeCodec.Equals("ac3", StringComparison.OrdinalIgnoreCase)
            || probeCodec.Equals("eac3", StringComparison.OrdinalIgnoreCase)
            || probeCodec.Equals("aac", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Text subtitles a <c>tx3g</c> track can carry, named as the probe names them.
    ///
    /// The bitmap kinds — PGS, DVD, DVB — are absent because MP4 has nowhere to put a picture subtitle
    /// that any of this library's clients would draw. Offering one would tick a row in a picker against
    /// words that never appear.
    /// </summary>
    internal static bool CanPackageSubtitle(string? probeCodec) => probeCodec?.ToLowerInvariant() is
        "subrip" or "srt" or "ass" or "ssa" or "webvtt" or "vtt" or "mov_text" or "text";

    /// <summary>
    /// The same answer, for a Matroska <c>CodecID</c> — and stricter for AAC, which is described from
    /// <c>CodecPrivate</c> rather than from a frame.
    ///
    /// The config is not merely required to be present, it is <em>parsed</em>, by the same routine that
    /// would later build the descriptor. Asking a cheaper question — "is there a config at all" — would
    /// answer yes for an explicitly signalled SBR stream that
    /// <see cref="Mp4Writer.DescribeAac"/> then declines, and the track would be walked, chosen, and
    /// finally dropped: a film with a picture and no sound. That is the one failure this type exists to
    /// prevent, so the two ask the identical question.
    /// </summary>
    internal static bool CanPackageAudio(IndexedTrack track) => track.CodecId switch
    {
        "A_AC3" or "A_EAC3" => true,
        "A_AAC" => track.CodecPrivate is { } config && Mp4Writer.DescribeAac(config) is not null,
        _ => false,
    };

    /// <summary>
    /// Video a sample entry can be written for, named as the probe names it.
    ///
    /// HEVC and H.264, which is what <see cref="Mp4Writer.VideoCodec"/> has entries for. AV1 is the
    /// absence that matters: a recent Apple TV decodes it, so nothing on the client's side of the question
    /// rules it out, and it would otherwise be advertised as remuxable right up to the request that fails.
    /// </summary>
    internal static bool CanPackageVideo(string? probeCodec) =>
        probeCodec is not null
        && (probeCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase)
            || probeCodec.Equals("h264", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The same answer, for a Matroska track — and stricter, because by then the configuration record is
    /// either there or it is not, and a codec we can name without one is still nothing we can describe.
    /// </summary>
    internal static bool CanPackageVideo(IndexedTrack track) =>
        Mp4Writer.VideoCodec(track.CodecId) is not null && track.CodecPrivate is not null;

    /// <summary>
    /// Whether a track's samples are worth recording at all — the same question as the two above, asked
    /// once for every kind, at the point the walk decides what to write down.
    ///
    /// A sample table for a track no sample entry can be written for is bytes nobody can ever point at.
    /// That was not obvious until it was measured: on production a single TrueHD track accounted for 96 %
    /// of its film's index, and four files out of 147 held 43 % of the 1.2 GB the library had accumulated.
    ///
    /// The track itself stays in the index. Its ordinal keeps the viewer's stored stream indexes lined up
    /// with the file, and the resolver still has to see it to explain why it cannot be used — only its
    /// frames go unrecorded.
    /// </summary>
    internal static bool WantsSamples(IndexedTrack track) => track.Kind switch
    {
        IndexedTrackKind.Video => CanPackageVideo(track),
        IndexedTrackKind.Audio => CanPackageAudio(track),
        IndexedTrackKind.Subtitle => SubtitleText.IsConvertible(track.CodecId),
        _ => false,
    };
}
