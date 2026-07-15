namespace MediaServer.Api.Mux;

/// <summary>
/// Infers a language tag and a display title for an external audio track from its path, for streams that
/// carry no tags of their own: releases put dubs in folders like <c>Rus Sound [AniLibria]</c> or name
/// files <c>Show.S01E05.rus.mka</c>. Tokens are whole runs of letters (digits and punctuation break a
/// token), so e.g. "rus" never fires inside a real word.
/// </summary>
internal static class AudioTrackLabeler
{
    // ISO 639-2/B output codes — the Matroska convention. Deliberately small: only unambiguous tokens
    // that releases actually use; a miss just leaves the stream untagged. Two-letter codes are excluded
    // on purpose — "it", "de", "en", … appear as ordinary words inside titles and would mis-tag tracks.
    private static readonly Dictionary<string, string> LanguageTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rus"] = "rus", ["russian"] = "rus",
        ["eng"] = "eng", ["english"] = "eng",
        ["jpn"] = "jpn", ["jap"] = "jpn", ["japanese"] = "jpn",
        ["ukr"] = "ukr", ["ukrainian"] = "ukr",
        ["ger"] = "ger", ["deu"] = "ger", ["german"] = "ger",
        ["fre"] = "fre", ["fra"] = "fre", ["french"] = "fre",
        ["spa"] = "spa", ["spanish"] = "spa",
        ["ita"] = "ita", ["italian"] = "ita",
        ["pol"] = "pol", ["polish"] = "pol",
        ["portuguese"] = "por",
        ["kor"] = "kor", ["korean"] = "kor",
        ["chi"] = "chi", ["zho"] = "chi", ["chinese"] = "chi",
    };

    /// <summary>The inferred ISO 639-2 language, or null. The file's own name wins over its folders (most
    /// specific first), and folders are walked nearest-first.</summary>
    public static string? InferLanguage(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments.Reverse())
        {
            foreach (var token in Tokenize(segment))
            {
                if (LanguageTokens.TryGetValue(token, out var language))
                {
                    return language;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// A display title for the track: the audio file's own folder name (e.g. "RUS Sound [AniLibria]" —
    /// it usually names the dub group), but only when the track sits in a different folder than its video;
    /// a track right next to its video has no folder of its own to speak of.
    /// </summary>
    public static string? InferTitle(string audioRelativePath, string videoRelativePath)
    {
        var audioFolder = FolderOf(audioRelativePath);
        return audioFolder is { Length: > 0 } &&
               !string.Equals(audioFolder, FolderOf(videoRelativePath), StringComparison.OrdinalIgnoreCase)
            ? audioFolder.Split('/')[^1]
            : null;
    }

    private static string FolderOf(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : normalized[..lastSlash];
    }

    private static IEnumerable<string> Tokenize(string segment)
    {
        var start = -1;
        for (var index = 0; index <= segment.Length; index++)
        {
            if (index < segment.Length && char.IsLetter(segment[index]))
            {
                if (start < 0)
                {
                    start = index;
                }
            }
            else if (start >= 0)
            {
                yield return segment[start..index];
                start = -1;
            }
        }
    }
}
