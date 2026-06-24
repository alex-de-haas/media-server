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

    // Cap on IN-list parameters per query so a large library never exceeds SQLite's 999-parameter limit
    // (mirrors LibraryReadService.PostersAsync).
    private const int ChunkSize = 500;

    /// <summary>Every collection with at least <see cref="MinMovies"/> owned movies, by name.</summary>
    public async Task<IReadOnlyList<CollectionSummaryDto>> ListAsync(CancellationToken cancellationToken)
    {
        // Count owned movies per collection and apply the threshold in SQL (HAVING), so ineligible
        // collections never leave the database.
        var counts = await database.MediaItems.AsNoTracking()
            .Where(item => item.PublicId != null && item.Kind == MediaKind.Movie && item.CollectionId != null)
            .GroupBy(item => item.CollectionId!.Value)
            .Where(group => group.Count() >= MinMovies)
            .Select(group => new { CollectionId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);

        if (counts.Count == 0)
        {
            return [];
        }

        var ids = counts.Select(entry => entry.CollectionId).ToList();
        var countById = counts.ToDictionary(entry => entry.CollectionId, entry => entry.Count);
        var collections = await CollectionsByIdAsync(ids, cancellationToken);
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

    // Loads the collection rows for the eligible ids, chunked so the IN-list never exceeds SQLite's limit.
    private async Task<List<MovieCollection>> CollectionsByIdAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var collections = new List<MovieCollection>(ids.Count);
        foreach (var chunk in ids.Chunk(ChunkSize))
        {
            collections.AddRange(await database.MovieCollections.AsNoTracking()
                .Where(collection => chunk.Contains(collection.Id))
                .ToListAsync(cancellationToken));
        }

        return collections;
    }

    // A representative poster per collection (the earliest member's), used when the collection itself has no
    // artwork. Joins member movies to their primary images, chunked over the collection ids; the earliest
    // year (then sort order) wins, merged across chunks.
    private async Task<Dictionary<Guid, string>> PosterFallbackAsync(IReadOnlyList<Guid> collectionIds, CancellationToken cancellationToken)
    {
        var best = new Dictionary<Guid, (int Year, int SortOrder, string RemotePath)>();
        foreach (var chunk in collectionIds.Chunk(ChunkSize))
        {
            var rows = await database.MediaItems.AsNoTracking()
                .Where(item => item.PublicId != null && item.Kind == MediaKind.Movie &&
                    item.CollectionId != null && chunk.Contains(item.CollectionId.Value))
                .Join(
                    database.ImageAssets.AsNoTracking().Where(image => image.ImageType == ImageType.Primary),
                    item => item.Id,
                    image => image.MediaItemId,
                    (item, image) => new { CollectionId = item.CollectionId!.Value, Year = item.Year ?? int.MaxValue, image.RemotePath, image.SortOrder })
                .ToListAsync(cancellationToken);

            foreach (var row in rows)
            {
                if (!best.TryGetValue(row.CollectionId, out var current) ||
                    row.Year < current.Year ||
                    (row.Year == current.Year && row.SortOrder < current.SortOrder))
                {
                    best[row.CollectionId] = (row.Year, row.SortOrder, row.RemotePath);
                }
            }
        }

        return best.ToDictionary(entry => entry.Key, entry => entry.Value.RemotePath);
    }
}
