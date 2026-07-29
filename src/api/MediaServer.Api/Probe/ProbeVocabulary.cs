namespace MediaServer.Api.Probe;

/// <summary>
/// The one place either provider's raw words become the library's. Both must land on the same vocabulary or
/// their results cannot be compared — and comparison is what the fallback provider's credibility rests on.
/// </summary>
internal static class ProbeVocabulary
{
    /// <summary>
    /// Container codec identifiers mapped to the names ffprobe reports, which the library already stores.
    /// Matroska spells a codec <c>V_MPEG4/ISO/AVC</c>, MP4 spells the same thing <c>avc1</c>, and ffprobe
    /// calls it <c>h264</c>. Deliberately small: only what real releases carry, with anything unknown passed
    /// through lowercased rather than guessed at.
    /// </summary>
    private static readonly Dictionary<string, string> Codecs = new(StringComparer.OrdinalIgnoreCase)
    {
        // Matroska
        ["V_MPEG4/ISO/AVC"] = "h264",
        ["V_MPEGH/ISO/HEVC"] = "hevc",
        ["V_MPEG4/ISO/ASP"] = "mpeg4",
        ["V_MPEG2"] = "mpeg2video",
        ["V_AV1"] = "av1",
        ["V_VP8"] = "vp8",
        ["V_VP9"] = "vp9",
        ["A_AAC"] = "aac",
        ["A_AC3"] = "ac3",
        ["A_EAC3"] = "eac3",
        ["A_DTS"] = "dts",
        ["A_TRUEHD"] = "truehd",
        ["A_FLAC"] = "flac",
        ["A_OPUS"] = "opus",
        ["A_VORBIS"] = "vorbis",
        ["A_MPEG/L3"] = "mp3",
        ["A_PCM/INT/LIT"] = "pcm_s16le",
        ["S_TEXT/UTF8"] = "subrip",
        ["S_TEXT/ASS"] = "ass",
        ["S_TEXT/SSA"] = "ssa",
        ["S_TEXT/WEBVTT"] = "webvtt",
        ["S_HDMV/PGS"] = "hdmv_pgs_subtitle",
        ["S_VOBSUB"] = "dvd_subtitle",
        // MP4 / QuickTime sample entries
        ["avc1"] = "h264",
        ["avc3"] = "h264",
        ["hvc1"] = "hevc",
        ["hev1"] = "hevc",
        ["av01"] = "av1",
        ["mp4v"] = "mpeg4",
        ["mp4a"] = "aac",
        ["ac-3"] = "ac3",
        ["ec-3"] = "eac3",
        ["alac"] = "alac",
        ["tx3g"] = "mov_text",
        ["c608"] = "eia_608",
    };

    /// <summary>The library's name for a codec, whichever container spelled it.</summary>
    public static string? Codec(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "?")
        {
            return null;
        }

        var trimmed = raw.Trim();
        return Codecs.TryGetValue(trimmed, out var mapped) ? mapped : trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// A three-letter language tag, or null when the container said nothing. Normalization — the ISO 639-1
    /// pair, the terminological spelling, a dropped BCP-47 region — lives in <see cref="LanguageTags"/>, so
    /// what a file claims and what an operator types land on the same vocabulary.
    /// <para>
    /// A tag <see cref="LanguageTags"/> does not know is kept anyway, lowercased. This is where the probe
    /// parts company with operator input: the value is not a typo to reject but what the file says, and
    /// dropping it would silently unlabel a track that is labelled.
    /// </para>
    /// </summary>
    public static string? Language(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var primary = raw.Trim().Split('-', '_')[0];
        if (primary.Length == 0 || primary.Equals("und", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return LanguageTags.Normalize(primary) ?? primary.ToLowerInvariant();
    }

    /// <summary>
    /// The stored HDR label. <c>null</c> means <b>unknown</b> — nobody could tell — while
    /// <see cref="Sdr"/> is a positive statement that the file is not HDR. Keeping them apart is what lets a
    /// badge stay silent about a file the header parser could not read, instead of asserting SDR.
    /// </summary>
    public const string Sdr = "SDR";

    /// <summary>The generic label for PQ content whose exact flavour a container header cannot reveal.</summary>
    public const string Hdr = "HDR";
}
