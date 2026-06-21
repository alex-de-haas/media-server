using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Jellyfin;

/// <summary>
/// Synthesizes artwork for catalogs (Jellyfin collection folders), which carry no images of their own.
/// A catalog borrows the backdrop of its most recently added top-level title so Infuse renders a wide
/// "back plate" on the library tile instead of a blank placeholder. The same backdrop is served when the
/// client requests the collection folder's image by id.
/// </summary>
public sealed class JellyfinCatalogArtwork(MediaServerDbContext database)
{
    /// <summary>Resolves a collection-folder public id back to its catalog id, or null if it is not a catalog.</summary>
    public async Task<Guid?> ResolveCatalogIdAsync(string publicId, CancellationToken cancellationToken)
    {
        var catalogIds = await database.Catalogs.AsNoTracking()
            .Select(catalog => catalog.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in catalogIds)
        {
            if (JellyfinIds.Catalog(id) == publicId)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// The backdrop of the catalog's most recently added top-level title (movie/series), or null when no
    /// such title has a backdrop yet. Older titles are used as a fallback so a freshly added item that has
    /// not been enriched does not blank out the whole library.
    /// </summary>
    public async Task<ImageAsset?> GetLatestBackdropAsync(Guid catalogId, CancellationToken cancellationToken) =>
        await database.ImageAssets.AsNoTracking()
            .Where(image => image.ImageType == ImageType.Backdrop
                && image.MediaItem!.CatalogId == catalogId
                && image.MediaItem.PublicId != null
                && image.MediaItem.ParentId == null
                && (image.MediaItem.Kind == MediaKind.Movie || image.MediaItem.Kind == MediaKind.Series))
            .OrderByDescending(image => image.MediaItem!.AddedAt)
            .ThenBy(image => image.SortOrder)
            .FirstOrDefaultAsync(cancellationToken);
}
