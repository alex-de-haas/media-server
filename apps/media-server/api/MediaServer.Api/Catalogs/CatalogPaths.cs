using MediaServer.Api.Data;

namespace MediaServer.Api.Catalogs;

/// <summary>
/// The resolved on-disk layout of a catalog root: sibling <c>files/</c> (download/seed target) and
/// <c>library/</c> (clean hardlinks, the only subtree scanned/exposed). Both live on one filesystem.
/// </summary>
public sealed record CatalogPaths(string Root, string FilesDir, string LibraryDir)
{
    public const string FilesDirName = "files";
    public const string LibraryDirName = "library";

    public static CatalogPaths For(Catalog catalog) => For(catalog.Root);

    public static CatalogPaths For(string root)
    {
        var full = Path.GetFullPath(root);
        return new CatalogPaths(full, Path.Combine(full, FilesDirName), Path.Combine(full, LibraryDirName));
    }

    /// <summary>Creates <c>files/</c> and <c>library/</c> if missing.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(FilesDir);
        Directory.CreateDirectory(LibraryDir);
    }
}
