using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Collections;

/// <summary>
/// Read model for the internal <c>/api/library/collections</c> surface: groups published movies by their
/// <see cref="MovieCollection"/> and projects each franchise and its members for the UI. Membership is the
/// persisted <see cref="MediaItem.CollectionId"/> link (written at enrich time by <see cref="CollectionSyncService"/>);
/// poster/title resolution for members is reused from <see cref="LibraryReadService"/>.
/// </summary>
public sealed class CollectionReadService(MediaServerDbContext database, LibraryReadService library)
{
    // A single owned movie is not a browsable "franchise" — only surface a collection once at least this many
    // of its movies are in the library, so the page is franchises rather than a wall of one-offs.
    private const int MinMovies = 2;

    /// <summary>Every collection with at least <see cref="MinMovies"/> owned movies, by name.</summary>
    public async Task<IReadOnlyList<CollectionSummaryDto>> ListAsync(CancellationToken cancellationToken)
    {
        var counts = await database.MediaItems.AsNoTracking()
            .Where(item => item.PublicId != null && item.Kind == MediaKind.Movie && item.CollectionId != null)
            .GroupBy(item => item.CollectionId!.Value)
            .Select(group => new { CollectionId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        var eligible = counts.Where(entry => entry.Count >= MinMovies).ToList();
        if (eligible.Count == 0)
        {
            return [];
        }

        var ids = eligible.Select(entry => entry.CollectionId).ToList();
        var countById = eligible.ToDictionary(entry => entry.CollectionId, entry => entry.Count);
        var collections = await database.MovieCollections.AsNoTracking()
            .Where(collection => ids.Contains(collection.Id))
            .ToListAsync(cancellationToken);
        var posterFallback = await PosterFallbackAsync(ids, cancellationToken);

        return collections
            .Select(collection => new CollectionSummaryDto(
                collection.Id,
                collection.Name,
                collection.PosterUrl ?? posterFallback.GetValueOrDefault(collection.Id),
                countById[collection.Id]))
            .OrderBy(dto => dto.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>A single collection with its in-library movies (chronological), or null if it has none.</summary>
    public async Task<CollectionDetailDto?> GetAsync(Guid id, int? appUserId, CancellationToken cancellationToken)
    {
        var collection = await database.MovieCollections.AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.Id == id, cancellationToken);
        if (collection is null)
        {
            return null;
        }

        var movies = await database.MediaItems.AsNoTracking()
            .Where(item => item.PublicId != null && item.Kind == MediaKind.Movie && item.CollectionId == id)
            .ToListAsync(cancellationToken);
        if (movies.Count == 0)
        {
            return null;
        }

        var cards = await library.ProjectCardsAsync(movies, appUserId, cancellationToken);
        var ordered = cards
            .OrderBy(card => card.Year ?? int.MaxValue)
            .ThenBy(card => card.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var poster = collection.PosterUrl ?? ordered.FirstOrDefault(card => card.PosterUrl != null)?.PosterUrl;
        return new CollectionDetailDto(collection.Id, collection.Name, poster, collection.BackdropUrl, ordered);
    }

    // A representative poster per collection (the earliest member's), used when the collection itself has no
    // artwork. One query joins the member movies to their primary images; the earliest year wins.
    private async Task<Dictionary<Guid, string>> PosterFallbackAsync(IReadOnlyList<Guid> collectionIds, CancellationToken cancellationToken)
    {
        var rows = await database.MediaItems.AsNoTracking()
            .Where(item => item.PublicId != null && item.Kind == MediaKind.Movie &&
                item.CollectionId != null && collectionIds.Contains(item.CollectionId.Value))
            .Join(
                database.ImageAssets.AsNoTracking().Where(image => image.ImageType == ImageType.Primary),
                item => item.Id,
                image => image.MediaItemId,
                (item, image) => new { CollectionId = item.CollectionId!.Value, item.Year, image.RemotePath, image.SortOrder })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.CollectionId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(row => row.Year ?? int.MaxValue).ThenBy(row => row.SortOrder).First().RemotePath);
    }
}
