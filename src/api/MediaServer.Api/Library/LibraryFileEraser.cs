using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;

namespace MediaServer.Api.Library;

/// <summary>
/// Deletes a catalog-relative media file from disk, confined to the catalog root and refusing the
/// transient <c>.incoming/</c> staging area, then prunes the directories it empties (stopping at the
/// catalog root). Shared by <see cref="LibraryDeleteService"/> and the download purge so the path-safety
/// rules live in one place.
/// </summary>
public sealed class LibraryFileEraser(ICatalogPathSandbox sandbox, ILogger<LibraryFileEraser> logger)
{
    public void Erase(Catalog catalog, string relativePath)
    {
        // Defense in depth: never delete inside the transient staging area.
        if (CatalogPaths.IsIncoming(relativePath))
        {
            logger.LogWarning("Refusing to delete {Path}: inside the transient .incoming/ staging area", relativePath);
            return;
        }

        if (!sandbox.TryResolve(catalog, relativePath, out var absolute))
        {
            logger.LogWarning("Skipping delete of unresolved library path {Path} in catalog {Catalog}", relativePath, catalog.Id);
            return;
        }

        var root = CatalogPaths.For(catalog).Root;
        try
        {
            if (File.Exists(absolute))
            {
                File.Delete(absolute);
                CleanEmptyParents(Path.GetDirectoryName(absolute), root);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to delete library file {Path}", absolute);
        }
    }

    private static void CleanEmptyParents(string? directory, string root)
    {
        // Compare with a trailing separator so a sibling like "root-other" can't match "root", and stop
        // at the catalog root itself.
        var stop = EnsureTrailingSeparator(Path.GetFullPath(root));
        var current = directory is null ? null : Path.GetFullPath(directory);
        while (!string.IsNullOrEmpty(current) &&
               EnsureTrailingSeparator(current).StartsWith(stop, StringComparison.Ordinal) &&
               !EnsureTrailingSeparator(current).Equals(stop, StringComparison.Ordinal))
        {
            try
            {
                if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                {
                    break;
                }

                Directory.Delete(current);
                current = Path.GetDirectoryName(current);
            }
            catch (IOException)
            {
                break;
            }
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
}
