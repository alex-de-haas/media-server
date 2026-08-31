using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Catalogs;

/// <summary>
/// Answers whether a catalog's storage is actually present, by reading from it rather than by asking
/// whether its root directory exists.
/// </summary>
/// <remarks>
/// The directory existing is not evidence: a bind mount that lost its host path still presents as an
/// empty directory inside the container, which is precisely the failure a scan must not mistake for
/// "the operator deleted their library". A catalog sits on one mount, so the question has a clean
/// answer — if any of its files can be read the volume is there, and if none can it is not.
/// </remarks>
public sealed class CatalogFileProbe(MediaServerDbContext database, ICatalogPathSandbox sandbox)
{
    /// <summary>
    /// How many files a probe is willing to stat before giving up. A dead network mount can make every
    /// stat block until its timeout, so a bounded sample keeps a caller on a timer from stalling for a
    /// whole library's worth of them.
    /// </summary>
    public const int MaxProbe = 50;

    /// <summary>
    /// True when at least one of the catalog's known library files can be read — and true for a catalog
    /// with no library files at all, which is not evidence of anything.
    /// </summary>
    public async Task<bool> AnyFileResolvesAsync(Catalog catalog, CancellationToken cancellationToken)
    {
        var paths = await database.MediaSources.AsNoTracking()
            .Where(source => source.MediaItem!.CatalogId == catalog.Id && source.MediaItem.PublicId != null)
            .OrderBy(source => source.CreatedAt)
            .Select(source => source.Path)
            .Take(MaxProbe)
            .ToListAsync(cancellationToken);

        return paths.Count == 0 || paths.Any(path => Resolves(catalog, path));
    }

    /// <summary>Whether one catalog-relative path resolves to a file that is there.</summary>
    public bool Resolves(Catalog catalog, string relativePath) =>
        sandbox.TryResolve(catalog, relativePath, out var absolute) && File.Exists(absolute);
}
