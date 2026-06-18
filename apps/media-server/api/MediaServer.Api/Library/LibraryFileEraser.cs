using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;

namespace MediaServer.Api.Library;

/// <summary>
/// Deletes a catalog-relative library file from disk, confined to the catalog's <c>library/</c> subtree
/// (never <c>files/</c> or anywhere else under the root), and prunes the directories it empties. Shared
/// by <see cref="LibraryDeleteService"/> and the download purge so the path-safety rules live in one place.
/// </summary>
public sealed class LibraryFileEraser(ICatalogPathSandbox sandbox, ILogger<LibraryFileEraser> logger)
{
    public void Erase(Catalog catalog, string relativePath)
    {
        if (!sandbox.TryResolve(catalog, relativePath, out var absolute))
        {
            logger.LogWarning("Skipping delete of unresolved library path {Path} in catalog {Catalog}", relativePath, catalog.Id);
            return;
        }

        // Defense in depth: only ever delete inside library/, never files/ or elsewhere under the root.
        var libraryDir = EnsureTrailingSeparator(CatalogPaths.For(catalog).LibraryDir);
        if (!absolute.StartsWith(libraryDir, StringComparison.Ordinal))
        {
            logger.LogWarning("Refusing to delete {Path}: outside the library/ subtree", absolute);
            return;
        }

        try
        {
            if (File.Exists(absolute))
            {
                File.Delete(absolute);
                CleanEmptyParents(Path.GetDirectoryName(absolute), CatalogPaths.For(catalog).LibraryDir);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to delete library file {Path}", absolute);
        }
    }

    private static void CleanEmptyParents(string? directory, string libraryDir)
    {
        // Compare with a trailing separator so a sibling like "library-other" can't match "library".
        var stop = EnsureTrailingSeparator(Path.GetFullPath(libraryDir));
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
