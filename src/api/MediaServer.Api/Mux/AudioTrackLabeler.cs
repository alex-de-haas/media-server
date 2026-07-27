namespace MediaServer.Api.Mux;

/// <summary>
/// Infers a language tag and a display title for an external audio or subtitle track from its path, for
/// streams that carry no tags of their own: releases put dubs in folders like <c>Rus Sound [AniLibria]</c>
/// or name files <c>Show.S01E05.rus.mka</c>. Tokens are whole runs of letters (digits and punctuation
/// break a token), so e.g. "rus" never fires inside a real word.
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
    /// Words that classify a folder rather than name a dub group: a folder called only "RUS Subs" or
    /// "Sound" says what is inside, not who made it. Combined with <see cref="LanguageTokens"/>, they
    /// decide whether a folder name carries a title at all.
    /// </summary>
    private static readonly HashSet<string> CategoryTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "sound", "audio", "track", "tracks", "dub", "dubs", "voice",
        "sub", "subs", "subtitle", "subtitles", "sign", "signs",
    };

    /// <summary>
    /// Suffix tokens that describe a subtitle track rather than name it — the same set Jellyfin's own
    /// resolver reads out of a sidecar file name, so they must never be mistaken for a title.
    /// </summary>
    private static readonly HashSet<string> FlagTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "forced", "sdh", "hi", "cc", "default", "full",
    };

    /// <summary>
    /// A display title for the companion track, taken from whatever identifies it beyond its video.
    /// <para>
    /// The file name is consulted first: releases that keep everything in one folder put the label in a
    /// suffix (<c>Movie.rus.AniDUB.mka</c>, <c>Movie.Гаврилов.mka</c>), and this is also the shape this
    /// app writes, so its own output reads back on a later scan. Whatever the companion's name carries
    /// beyond the video's base name is split on dots, and language and flag tokens drop out; what is left
    /// is the label.
    /// </para>
    /// <para>
    /// Only when the name carries nothing extra — releases that instead nest per-group folders and reuse
    /// the video's exact file name — does the folder speak, as in "Rus Sound [AniLibria]". A folder made
    /// up entirely of language and category words ("RUS Subs") names a bucket rather than a track, and
    /// yields no title.
    /// </para>
    /// </summary>
    public static string? InferTitle(string companionRelativePath, string videoRelativePath)
    {
        if (TitleFromName(companionRelativePath, videoRelativePath) is { } fromName)
        {
            return fromName;
        }

        var companionFolder = FolderOf(companionRelativePath);
        if (companionFolder.Length == 0 ||
            string.Equals(companionFolder, FolderOf(videoRelativePath), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var nearest = companionFolder.Split('/')[^1];
        return Tokenize(nearest).Any(token => !LanguageTokens.ContainsKey(token) && !CategoryTokens.Contains(token))
            ? nearest
            : null;
    }

    /// <summary>The part of the companion's name past the video's base name, minus language and flag
    /// tokens; null when the names match or nothing meaningful is left.</summary>
    private static string? TitleFromName(string companionRelativePath, string videoRelativePath)
    {
        var companion = Path.GetFileNameWithoutExtension(companionRelativePath.Replace('\\', '/'));
        var video = Path.GetFileNameWithoutExtension(videoRelativePath.Replace('\\', '/'));
        if (video.Length == 0 || !companion.StartsWith(video, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var suffix = companion[video.Length..].Trim('.', ' ', '-', '_');
        if (suffix.Length == 0)
        {
            return null;
        }

        var kept = suffix
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !IsLanguageOnly(part) && !FlagTokens.Contains(part))
            .ToList();
        return kept.Count == 0 ? null : string.Join(' ', kept);
    }

    /// <summary>True when the part says nothing but a language — "rus", "Russian", but not "Russian DUB".</summary>
    private static bool IsLanguageOnly(string part)
    {
        var tokens = Tokenize(part).ToList();
        return tokens.Count > 0 && tokens.All(LanguageTokens.ContainsKey);
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
