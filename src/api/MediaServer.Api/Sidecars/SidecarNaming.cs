using System.Buffers;
using MediaServer.Api.Data;
using MediaServer.Api.Media;

namespace MediaServer.Api.Sidecars;

/// <summary>
/// One companion waiting to be named, reduced to what the rule actually argues over.
/// <para>
/// <paramref name="Id"/> is opaque here — a caller's own handle, given back on the matching
/// <see cref="SidecarName"/>. It is a <see cref="SourceFile"/> id when ingest places a file a release
/// shipped, and a <see cref="MediaStream"/> id when a track is extracted out of the container; the naming
/// rule has no reason to know which, and one implementation has to serve both or the two drift on the slug.
/// </para>
/// </summary>
/// <param name="Id">The caller's handle for this companion.</param>
/// <param name="Extension">The file extension it will carry, leading dot included.</param>
/// <param name="IsAudio">Audio or subtitle — the two do not crowd each other.</param>
public sealed record SidecarCandidate(Guid Id, string Extension, bool IsAudio, string? Language, string? Title);

/// <summary>
/// A companion that is <b>already</b> beside the video. It is not being renamed — files on disk stay as
/// they are — but it takes part in both questions the rule asks: its name is taken, and it counts towards
/// the cohort that decides whether a new companion needs a slug to be told apart from it.
/// </summary>
public sealed record PlacedSidecar(string FileName, bool IsAudio, string? Language);

/// <summary>One companion file as it should land beside its video.</summary>
/// <param name="Id">The <see cref="SidecarCandidate.Id"/> this name belongs to.</param>
/// <param name="FileName">Its canonical name, next to the video.</param>
public sealed record SidecarName(Guid Id, string FileName);

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
    /// <para>
    /// <paramref name="placed"/> is what already sits beside the video — sidecars a release shipped, or
    /// tracks extracted earlier. Passing them is what keeps a second Russian dub from being handed
    /// <c>&lt;video&gt;.rus.2.mka</c> while its own group name goes unused: the name is taken, so
    /// <see cref="Unique"/> would fall back to a numeric suffix, and the cohort test would not have seen the
    /// collision that the slug rule exists to answer. Nothing already on disk is renamed, so an existing lone
    /// track keeps the plain form and only the newcomer carries a slug — asymmetric, and the only option that
    /// does not rewrite files a client may already be reading.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SidecarName> For(
        string videoFileName,
        IReadOnlyList<SidecarCandidate> companions,
        IReadOnlyList<PlacedSidecar>? placed = null,
        IReadOnlyList<string>? reserved = null)
    {
        var baseName = Path.GetFileNameWithoutExtension(videoFileName);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { videoFileName };
        foreach (var sidecar in placed ?? [])
        {
            used.Add(sidecar.FileName);
        }

        // Names that are taken but say nothing about cohorts — a file sitting in the folder with no row of
        // its own. It happens through supported routes: dropping a sidecar's entry while keeping its file,
        // or an operator copying one in by hand. Its language is unknown, so it cannot be counted towards the
        // crowding test, but handing its name out again would have the writer overwrite it.
        foreach (var name in reserved ?? [])
        {
            used.Add(name);
        }

        var named = new List<SidecarName>(companions.Count);

        // A slug only earns its place when something else would collide with it: same kind, same language —
        // counting what is already beside the video as well as what is being named now.
        var cohorts = new Dictionary<(string Kind, string? Language), int>(StringTupleComparer.Instance);
        foreach (var key in companions.Select(companion => (KindOf(companion.IsAudio), companion.Language))
            .Concat((placed ?? []).Select(sidecar => (KindOf(sidecar.IsAudio), sidecar.Language))))
        {
            cohorts[key] = cohorts.GetValueOrDefault(key) + 1;
        }

        var ordinal = 0;
        foreach (var companion in companions)
        {
            ordinal++;
            var extension = companion.Extension.ToLowerInvariant();
            var parts = new List<string> { baseName };
            if (companion.Language is { Length: > 0 } language)
            {
                parts.Add(language);
            }

            if (cohorts[(KindOf(companion.IsAudio), companion.Language)] > 1)
            {
                // Something has to tell these apart. The label is preferred; an untitled track falls back to
                // its position, which is stable because the caller orders companions deterministically.
                parts.Add(Slug(companion.Title) ?? ordinal.ToString());
            }

            named.Add(new SidecarName(companion.Id, Unique(string.Join('.', parts), extension, used)));
        }

        return named;
    }

    /// <summary>Audio and subtitle companions are named the same way but must not crowd each other: a lone
    /// Russian dub and a lone Russian subtitle are not a collision.</summary>
    private static string KindOf(bool isAudio) => isAudio ? "audio" : "subtitle";

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
