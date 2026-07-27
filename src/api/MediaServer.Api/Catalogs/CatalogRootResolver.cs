using MediaServer.Api.Configuration;

namespace MediaServer.Api.Catalogs;

/// <summary>
/// Translates between a catalog's durable identity (the Hosty mount <c>Label</c> plus a path relative to
/// that mount) and the absolute path it has in the current runtime. Hosty injects host paths for a mount
/// under the <c>dev</c> runtime and container paths under <c>docker</c>
/// (<c>HOSTY_MOUNT_CATALOGROOTS</c>), so the absolute path is runtime state while the label is not —
/// which is why catalogs are anchored to labels and re-resolved at startup
/// (see <see cref="CatalogAnchorService"/>).
///
/// The same containment rules as <see cref="CatalogPathSandbox"/> and
/// <see cref="MediaServer.Api.Torrents.RemoteTorrentEngine.ToMountRelative"/>: paths are compared
/// case-insensitively on Windows only, and a relative path may never escape its mount.
/// </summary>
public static class CatalogRootResolver
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Resolves a mount label + mount-relative path to the absolute root for this runtime. Returns null
    /// when the runtime injects no mount with that label (the mount was removed or renamed — the catalog
    /// is *unanchored*, and no path is guessed for it), or when the relative path would escape the mount.
    /// </summary>
    public static string? Resolve(IReadOnlyList<CatalogMount> mounts, string? label, string? relativePath) =>
        ResolveAnchor(mounts, label, relativePath)?.Root;

    /// <summary>
    /// As <see cref="Resolve"/>, but also returns the mount's <b>own</b> label rather than the caller's
    /// spelling of it. Labels are matched case-insensitively, so storing the caller's casing would let
    /// <c>media</c> and <c>MEDIA</c> — one and the same mount — be recorded as two different anchors and
    /// slip past the uniqueness check. Everything that persists an anchor stores this canonical label.
    /// </summary>
    public static (string Label, string Root)? ResolveAnchor(
        IReadOnlyList<CatalogMount> mounts, string? label, string? relativePath)
    {
        if (string.IsNullOrEmpty(label))
        {
            return null;
        }

        // Labels are identifiers, not paths: match them case-insensitively on every OS so a casing
        // difference between what Core injects and what was stored can't silently unanchor a catalog.
        var mount = mounts.FirstOrDefault(candidate =>
            string.Equals(candidate.Label, label, StringComparison.OrdinalIgnoreCase));
        if (mount is null)
        {
            return null;
        }

        var root = Path.GetFullPath(mount.Path);
        if (Normalize(relativePath) is not { } relative)
        {
            return null;
        }

        if (relative.Length == 0)
        {
            return (mount.Label, root);
        }

        var combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return IsContained(combined, root) ? (mount.Label, combined) : null;
    }

    /// <summary>
    /// The inverse: finds the mount holding <paramref name="absolutePath"/> and returns its label plus the
    /// posix-style path within it (empty when the path *is* the mount root). Returns null when no mount
    /// contains the path — for a catalog that means it stays a free-text standalone root.
    /// </summary>
    public static (string Label, string Relative)? ToMountRelative(IReadOnlyList<CatalogMount> mounts, string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return null;
        }

        var full = Path.GetFullPath(absolutePath);
        foreach (var mount in mounts)
        {
            if (string.IsNullOrEmpty(mount.Label))
            {
                continue;
            }

            var root = Path.GetFullPath(mount.Path);
            if (!IsContained(full, root))
            {
                continue;
            }

            // Both sides are already canonical absolute paths and containment is established, so the
            // relative path cannot climb out and Normalize cannot fail here.
            var relative = string.Equals(full, root, PathComparison)
                ? string.Empty
                : Normalize(Path.GetRelativePath(root, full)) ?? string.Empty;
            return (mount.Label, relative);
        }

        return null;
    }

    /// <summary>
    /// Normalizes an operator-supplied or stored mount-relative path to the canonical posix-style form:
    /// forward slashes, no leading/trailing separator, and <c>.</c>/<c>..</c> segments resolved away.
    /// Canonical matters beyond tidiness — this string *is* the catalog's stored location, so
    /// <c>films</c> and <c>movies/../films</c> have to reduce to one value or two catalogs could claim
    /// the same directory through the uniqueness check. Returns null when the path climbs out of the
    /// mount, which the caller reports rather than silently clamping.
    /// </summary>
    public static string? Normalize(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        var segments = new List<string>();
        foreach (var segment in relativePath.Trim().Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;
                case ".." when segments.Count == 0:
                    return null; // Climbs above the mount root.
                case "..":
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                default:
                    segments.Add(segment);
                    continue;
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or sits underneath it.</summary>
    private static bool IsContained(string path, string root)
    {
        if (string.Equals(path, root, PathComparison))
        {
            return true;
        }

        var withSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(withSeparator, PathComparison);
    }
}
