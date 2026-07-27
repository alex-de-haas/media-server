using System.Buffers;
using MediaServer.Api.Data;
using MediaServer.Api.Media;

namespace MediaServer.Api.Sidecars;

/// <summary>One companion file as it should land beside its video.</summary>
/// <param name="Source">The file being placed.</param>
/// <param name="FileName">Its canonical name, next to the video.</param>
public sealed record SidecarName(SourceFile Source, string FileName);

/// <summary>
/// Names the companion files that land beside a library file.
/// <para>
/// A slug is added <b>only when it disambiguates</b> — when the video has more than one companion of the
/// same kind and language. One Russian subtitle track therefore keeps the plain
/// <c>&lt;video&gt;.rus.srt</c> that clients match on, while three Russian dubs each get their group name.
/// The conventional form is the default and the slug is the exception, rather than every file paying for
/// the rare collision.
/// </para>
/// </summary>
public static class SidecarNaming
{
    /// <summary>A file name may not exceed 255 bytes on the filesystems this runs on, and Cyrillic costs
    /// two bytes per character — so the budget is counted in bytes, not characters.</summary>
    private const int MaxFileNameBytes = 255;

    /// <summary>
    /// Names every companion of one video. <paramref name="videoFileName"/> is the organized file's name;
    /// each companion keeps its own extension.
    /// </summary>
    public static IReadOnlyList<SidecarName> For(
        string videoFileName,
        IReadOnlyList<(SourceFile File, string? Language, string? Title)> companions)
    {
        var baseName = Path.GetFileNameWithoutExtension(videoFileName);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { videoFileName };
        var named = new List<SidecarName>(companions.Count);

        // A slug only earns its place when something else would collide with it: same kind, same language.
        var crowded = companions
            .GroupBy(companion => (Kind: KindOf(companion.File.RelativePath), companion.Language),
                StringTupleComparer.Instance)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(companion => companion.File.Id))
            .ToHashSet();

        var ordinal = 0;
        foreach (var (file, language, title) in companions)
        {
            ordinal++;
            var extension = Path.GetExtension(file.RelativePath).ToLowerInvariant();
            var parts = new List<string> { baseName };
            if (language is { Length: > 0 })
            {
                parts.Add(language);
            }

            if (crowded.Contains(file.Id))
            {
                // Something has to tell these apart. The label is preferred; an untitled track falls back to
                // its position, which is stable because the caller orders companions deterministically.
                parts.Add(Slug(title) ?? ordinal.ToString());
            }

            named.Add(new SidecarName(file, Unique(string.Join('.', parts), extension, used)));
        }

        return named;
    }

    /// <summary>Audio and subtitle companions are named the same way but must not crowd each other: a lone
    /// Russian dub and a lone Russian subtitle are not a collision.</summary>
    private static string KindOf(string relativePath) =>
        MediaFormats.IsCompanionAudio(relativePath) ? "audio" : "subtitle";

    /// <summary>
    /// Characters a name must not contain, whatever this process happens to be running on.
    /// <c>Path.GetInvalidFileNameChars()</c> answers for the <b>runtime</b>, and on Linux — which is what
    /// the container is — that is only <c>/</c> and NUL. But the catalog root is the operator's media
    /// library: it may be exFAT or SMB, and it may be opened from Windows later. A name this app writes has
    /// to survive all of those, so the forbidden set is fixed here rather than inherited from the host.
    /// <para>
    /// The dot is included because it separates the name's own parts, and the whole reason this exists is
    /// real titles: one release in the development library labels a track <c>DUB | DD5.1 @ 640 kbps</c>.
    /// </para>
    /// </summary>
    private static readonly SearchValues<char> ForbiddenInNames =
        SearchValues.Create("/\\:*?\"<>|.\0\r\n\t");

    /// <summary>A file-name-safe label, or null when nothing usable is left of the title.</summary>
    internal static string? Slug(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var runtimeInvalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. title.Trim().Trim('[', ']', '(', ')')
            .Where(character =>
                !ForbiddenInNames.Contains(character) &&
                !runtimeInvalid.Contains(character) &&
                !char.IsControl(character))]);
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        // Windows also refuses a name ending in a dot or a space, and the trailing-dot case can arrive here
        // from a title like "Vol. 2.".
        cleaned = cleaned.TrimEnd('.', ' ');
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Fits the name inside the byte budget and makes it unique in its folder. Truncation trims the label
    /// rather than the video's own name, so a shortened sidecar still sits next to its file alphabetically;
    /// a collision that survives that gets a numeric suffix.
    /// </summary>
    private static string Unique(string stem, string extension, HashSet<string> used)
    {
        var name = Fit(stem, extension);
        if (used.Add(name))
        {
            return name;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = Fit($"{stem}.{suffix}", extension);
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Fit(string stem, string extension)
    {
        var name = stem + extension;
        if (System.Text.Encoding.UTF8.GetByteCount(name) <= MaxFileNameBytes)
        {
            return name;
        }

        var budget = MaxFileNameBytes - System.Text.Encoding.UTF8.GetByteCount(extension);
        var trimmed = stem;
        while (trimmed.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(trimmed) > budget)
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.TrimEnd('.', ' ') + extension;
    }

    /// <summary>Compares the (kind, language) pair case-insensitively, so "RUS" and "rus" crowd each other.</summary>
    private sealed class StringTupleComparer : IEqualityComparer<(string Kind, string? Language)>
    {
        public static readonly StringTupleComparer Instance = new();

        public bool Equals((string Kind, string? Language) left, (string Kind, string? Language) right) =>
            string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(left.Language, right.Language, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Kind, string? Language) value) =>
            HashCode.Combine(
                value.Kind.ToLowerInvariant(),
                value.Language?.ToLowerInvariant());
    }
}
