namespace MediaServer.Api.Configuration;

/// <summary>
/// Turns an absolute library path into the (mount label, relative path) pair the external engine addresses
/// files by. Both apps bind the same host paths, so a file only reaches the engine when its catalog root is
/// among the mounts bound into it — otherwise the caller has to do without.
/// </summary>
public static class CatalogMounts
{
    private static readonly StringComparison Comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>True when the path sits under a configured mount root, with the label and the path relative
    /// to it. A null label is the unlabeled single-mount case the engine also accepts.</summary>
    public static bool TryResolve(MediaServerSettings settings, string absolutePath, out string? label, out string relative)
    {
        var full = Path.GetFullPath(absolutePath);
        foreach (var mount in settings.CatalogMountRoots)
        {
            var root = Path.GetFullPath(mount.Path);
            // Don't double-append a separator when the root already ends with one (a filesystem root like
            // "/"), which would break the descendant check.
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (string.Equals(full, root, Comparison) || full.StartsWith(prefix, Comparison))
            {
                label = string.IsNullOrEmpty(mount.Label) ? null : mount.Label;
                relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                return true;
            }
        }

        label = null;
        relative = string.Empty;
        return false;
    }
}
