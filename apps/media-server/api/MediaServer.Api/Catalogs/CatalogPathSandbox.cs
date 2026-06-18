using MediaServer.Api.Data;

namespace MediaServer.Api.Catalogs;

/// <summary>
/// Confines every file operation to a catalog root. Resolves and normalizes catalog-relative paths,
/// rejects <c>..</c> traversal, absolute inputs, and symlink escapes. See
/// <c>docs/planning/file-directory-management.md</c> and <c>docs/planning/security.md</c>.
/// </summary>
public interface ICatalogPathSandbox
{
    /// <summary>
    /// Resolves <paramref name="relativePath"/> (relative to the catalog root) to a contained absolute
    /// path. Returns false for traversal, absolute paths, or symlink escapes.
    /// </summary>
    bool TryResolve(Catalog catalog, string relativePath, out string absolutePath);
}

public sealed class CatalogPathSandbox : ICatalogPathSandbox
{
    public bool TryResolve(Catalog catalog, string relativePath, out string absolutePath)
    {
        absolutePath = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        // Reject absolute inputs and explicit parent-directory traversal up front.
        if (Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is ".."))
        {
            return false;
        }

        var root = Path.GetFullPath(catalog.Root);
        var rootWithSeparator = EnsureTrailingSeparator(root);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));

        // Lexical containment: the normalized path must sit under the root.
        if (!combined.Equals(root, StringComparison.Ordinal) &&
            !combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            return false;
        }

        // Symlink-escape guard: resolve link targets of the deepest existing ancestor and re-check
        // containment, so a symlink inside the root pointing outside it is rejected.
        if (!ResolvedRealPathIsContained(combined, root, rootWithSeparator))
        {
            return false;
        }

        absolutePath = combined;
        return true;
    }

    private static bool ResolvedRealPathIsContained(string combined, string root, string rootWithSeparator)
    {
        var existing = combined;
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || parent.Equals(existing, StringComparison.Ordinal))
            {
                // Nothing on disk yet (e.g. a not-yet-created library target); lexical check already passed.
                return true;
            }

            existing = parent;
        }

        var real = ResolveRealPath(existing);
        return real.Equals(root, StringComparison.Ordinal) ||
               real.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }

    private static string ResolveRealPath(string path)
    {
        try
        {
            var info = Directory.Exists(path) ? new DirectoryInfo(path) : (FileSystemInfo)new FileInfo(path);
            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            return Path.GetFullPath(target?.FullName ?? path);
        }
        catch (IOException)
        {
            return Path.GetFullPath(path);
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
