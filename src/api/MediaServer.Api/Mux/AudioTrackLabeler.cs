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
    /// Display titles for one video's companion tracks, one per input path, taken from whatever identifies
    /// each beyond the video itself.
    /// <para>
    /// The whole set is labelled together on purpose. A title has to <b>distinguish</b> the tracks, and only
    /// the siblings reveal what does: releases label either by folder — one per dub group, every file
    /// carrying the video's own name — or by file name, with "Гаврилов.ac3" and "Сербин.dts" dropped beside
    /// the film. Seen one at a time both look alike, and picking the wrong one gives every track the release
    /// name and no way to tell them apart.
    /// </para>
    /// <para>
    /// The caller passes <b>one cohort</b> — the tracks of a single kind and language, which is the group a
    /// title actually has to tell apart. Mixing kinds would let a subtitle named unlike a release's dubs
    /// decide the question for them: its name alone makes names look like the varying component, and every
    /// dub then falls back to the same label.
    /// </para>
    /// <para>
    /// Within a name, the label is what it carries beyond the video's own name (<c>Movie.rus.AniDUB.mka</c>
    /// — also the shape this app writes, so its output reads back on a later scan), or the whole name when
    /// it shares nothing with the video's. Either way language and flag tokens drop out, as does anything
    /// that merely restates the release or names a bucket ("RUS Subs", a file called <c>dub.ac3</c>).
    /// </para>
    /// </summary>
    public static IReadOnlyList<string?> InferTitles(
        IReadOnlyList<string> companionRelativePaths, string videoRelativePath)
    {
        var names = companionRelativePaths
            .Select(path => TitleFromName(path, videoRelativePath) ?? TitleFromOwnName(path, videoRelativePath))
            .ToList();
        var folders = companionRelativePaths
            .Select(path => TitleFromFolder(path, videoRelativePath))
            .ToList();

        // Whichever of the two actually varies across this video's companions is the one carrying the
        // labels; the other is repeating the release. Releases do it both ways — one nests a folder per dub
        // group and gives every file the video's name, another drops "Гаврилов.ac3" and "Сербин.dts" beside
        // the film — and a single path cannot tell which, because in isolation both look like a label.
        var useNames = companionRelativePaths.Count == 1
            ? names.Any(name => name is not null)
            : Varies(names);

        return useNames || !Varies(folders)
            // Falling back to the folder for a lone companion, or when neither varies: still better than
            // nothing when the name said nothing.
            ? [.. names.Select((name, index) => name ?? folders[index])]
            : folders;
    }

    /// <summary>True when the values are not all the same — the test for "this is what tells them apart".</summary>
    private static bool Varies(IReadOnlyList<string?> values) =>
        values.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

    /// <summary>The nearest folder, when it names a group rather than a bucket or the release itself.</summary>
    private static string? TitleFromFolder(string companionRelativePath, string videoRelativePath)
    {
        var companionFolder = FolderOf(companionRelativePath);
        if (companionFolder.Length == 0 ||
            string.Equals(companionFolder, FolderOf(videoRelativePath), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var nearest = companionFolder.Split('/')[^1];
        return NamesSomething(nearest) && !RestatesVideo(nearest, videoRelativePath) ? nearest : null;
    }

    /// <summary>
    /// What the companion's own name says about it, minus language and flag tokens; null when nothing
    /// meaningful is left.
    /// <para>
    /// A name that builds on the video's — <c>Movie.rus.AniDUB.mka</c> — contributes only the part past it.
    /// A name that shares nothing with the video's is a label in its entirety: a release that keeps
    /// everything in one folder and calls its tracks <c>Гаврилов.ac3</c> and <c>Сербин.dts</c> is naming
    /// each by its author, and that is the only thing telling them apart.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// The companion's whole file name as a label, for a release that keeps everything in one folder and
    /// names each track by its author — <c>Гаврилов.ac3</c>, <c>Сербин.dts</c> beside the film they dub.
    /// There the name is the only thing telling the tracks apart.
    /// <para>
    /// Refused when the name merely restates the video's own — a companion called
    /// <c>Some.Movie.2020.mka</c> next to <c>Some Movie (2020).mkv</c> carries no information, and the two
    /// differ only in punctuation, so the comparison is on word tokens rather than on the strings.
    /// </para>
    /// </summary>
    private static string? TitleFromOwnName(string companionRelativePath, string videoRelativePath)
    {
        var companion = Path.GetFileNameWithoutExtension(companionRelativePath.Replace('\\', '/'))
            .Trim('.', ' ', '-', '_');
        if (companion.Length == 0)
        {
            return null;
        }

        // Language and flag parts go first: they describe the track, they are already carried by the
        // language field, and leaving them in would make "Movie.rus" look like it says something the video's
        // own name does not.
        var kept = companion
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !IsLanguageOnly(part) && !FlagTokens.Contains(part))
            .ToList();
        if (kept.Count == 0)
        {
            return null;
        }

        var label = string.Join(' ', kept);
        return NamesSomething(label) && !RestatesVideo(label, videoRelativePath) ? label : null;
    }

    /// <summary>
    /// True when a candidate label identifies a particular track rather than classifying it. "Дубляж" and
    /// "[AniDUB]" do; "RUS Subs" and a file called <c>dub.ac3</c> do not — they name the bucket the track
    /// sits in, which the language field already carries.
    /// </summary>
    private static bool NamesSomething(string candidate) =>
        Tokenize(candidate).Any(token => !LanguageTokens.ContainsKey(token) && !CategoryTokens.Contains(token));

    /// <summary>
    /// True when a candidate label says nothing the video's own name does not. Compared on word tokens
    /// rather than on the strings, because a release folder and the organized file differ in punctuation
    /// far more often than in words — "Some.Movie.2020" against "Some Movie (2020)".
    /// </summary>
    private static bool RestatesVideo(string candidate, string videoRelativePath)
    {
        var videoTokens = Tokenize(Path.GetFileNameWithoutExtension(videoRelativePath.Replace('\\', '/')))
            .Select(token => token.ToLowerInvariant())
            .ToHashSet();
        var candidateTokens = Tokenize(candidate).Select(token => token.ToLowerInvariant()).ToList();
        return candidateTokens.Count > 0 && candidateTokens.All(videoTokens.Contains);
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
