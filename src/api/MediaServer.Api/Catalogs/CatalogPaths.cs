using MediaServer.Api.Data;

namespace MediaServer.Api.Catalogs;

/// <summary>
/// The resolved on-disk layout of a catalog root: a transient <c>.incoming/</c> staging directory
/// (one subfolder per active download) plus the canonical, published media tree that lives directly at
/// the catalog root. There is no separate <c>library/</c> subtree and no hardlinking — a completed file
/// is <b>moved</b> from <c>.incoming/</c> into its canonical place. Everything outside <c>.incoming/</c>
/// is the durable library and the only subtree the read/scan/Jellyfin surfaces expose.
/// See <c>docs/features/torrents-and-organizer.md</c>.
/// </summary>
public sealed record CatalogPaths(string Root, string IncomingDir)
{
    public const string IncomingDirName = ".incoming";

    public static CatalogPaths For(Catalog catalog) => For(catalog.Root);

    public static CatalogPaths For(string root)
    {
        var full = Path.GetFullPath(root);
        return new CatalogPaths(full, Path.Combine(full, IncomingDirName));
    }

    /// <summary>Absolute staging directory for a single download under <c>.incoming/</c>.</summary>
    public string IncomingFor(Guid downloadId) => Path.Combine(IncomingDir, downloadId.ToString("N"));

    /// <summary>Catalog-root-relative staging directory for a download (posix-style, forward slashes).</summary>
    public static string IncomingRelative(Guid downloadId) => $"{IncomingDirName}/{downloadId:N}";

    /// <summary>True when a catalog-root-relative path is inside the transient <c>.incoming/</c> staging area.</summary>
    public static bool IsIncoming(string relativePath) =>
        relativePath.StartsWith(IncomingDirName + "/", StringComparison.Ordinal) ||
        relativePath.Equals(IncomingDirName, StringComparison.Ordinal);

    /// <summary>Creates the catalog root and its <c>.incoming/</c> staging directory if missing.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(IncomingDir);
    }
}
