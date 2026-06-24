using System.Security.Cryptography;
using System.Text;
using MediaServer.Api.Collections;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Jellyfin;

/// <summary>
/// Backing data for the Jellyfin "Collections" surface: the eligible movie franchises (a collection with at
/// least <see cref="CollectionMetadata.MinOwnedMovies"/> owned movies), their member movies, and the public-id
/// resolution + artwork tags shared by <see cref="JellyfinLibraryService"/> (browsing) and
/// <see cref="JellyfinImageService"/> (artwork). Pure read model over the EF domain — no HTTP, so it stays
/// trivial to unit test. The image bytes are fetched/cached by <see cref="JellyfinImageService"/>.
/// </summary>
public sealed class JellyfinCollectionService(MediaServerDbContext database)
{
    /// <summary>True when the id is the single synthetic "Collections" view.</summary>
    public static bool IsView(string publicId) => publicId == JellyfinIds.CollectionsView();

    /// <summary>
    /// The franchises worth surfacing — collections with at least the threshold of owned movies — with their
    /// member counts, by name. Uses a server-side subquery for the id filter, so no IN-list parameters.
    /// </summary>
    public async Task<IReadOnlyList<(MovieCollection Collection, int Count)>> EligibleAsync(CancellationToken cancellationToken)
    {
        var counts = await database.MediaItems.AsNoTracking()
            .Where(item => item.PublicId != null && item.Kind == MediaKind.Movie && item.CollectionId != null)
            .GroupBy(item => item.CollectionId!.Value)
            .Where(group => group.Count() >= CollectionMetadata.MinOwnedMovies)
            .Select(group => new { CollectionId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        if (counts.Count == 0)
        {
            return [];
        }

        // Reuse the same grouping as a subquery so the collection load filters in SQL (no id IN-list params).
        var eligibleIds = database.MediaItems.AsNoTracking()
            .Where(item => item.PublicId != null && item.Kind == MediaKind.Movie && item.CollectionId != null)
            .GroupBy(item => item.CollectionId!.Value)
            .Where(group => group.Count() >= CollectionMetadata.MinOwnedMovies)
            .Select(group => group.Key);
        var collections = await database.MovieCollections.AsNoTracking()
            .Where(collection => eligibleIds.Contains(collection.Id))
            .ToListAsync(cancellationToken);

        var countById = counts.ToDictionary(entry => entry.CollectionId, entry => entry.Count);
        return collections
            .OrderBy(collection => collection.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(collection => (collection, countById[collection.Id]))
            .ToList();
    }

    /// <summary>
    /// The published member movies of a collection (unordered; the caller applies the shared item ordering so
    /// BoxSet members sort like any other movie listing on the Jellyfin surface).
    /// </summary>
    public IQueryable<MediaItem> MemberMovies(Guid collectionId) =>
        database.MediaItems.AsNoTracking()
            .Where(item => item.PublicId != null && item.Kind == MediaKind.Movie && item.CollectionId == collectionId);

    /// <summary>How many published movies a collection has in the library.</summary>
    public async Task<int> MemberCountAsync(Guid collectionId, CancellationToken cancellationToken) =>
        await database.MediaItems.AsNoTracking()
            .CountAsync(item => item.PublicId != null && item.Kind == MediaKind.Movie && item.CollectionId == collectionId, cancellationToken);

    /// <summary>Resolves a BoxSet public id back to its collection, or null if it is not one.</summary>
    public async Task<MovieCollection?> ResolveAsync(string publicId, CancellationToken cancellationToken)
    {
        // The public id is a one-way hash of the collection id, so match over ids (mirrors catalog resolution).
        var collections = await database.MovieCollections.AsNoTracking().ToListAsync(cancellationToken);
        return collections.FirstOrDefault(collection => JellyfinIds.Collection(collection.Id) == publicId);
    }

    /// <summary>The Primary-image tag advertised for a BoxSet; null when the collection has no poster.</summary>
    public static string? PrimaryTag(MovieCollection collection) => Tag(collection.Id, "primary", collection.PosterUrl);

    /// <summary>The Backdrop-image tag advertised for a BoxSet; null when the collection has no backdrop.</summary>
    public static string? BackdropTag(MovieCollection collection) => Tag(collection.Id, "backdrop", collection.BackdropUrl);

    // A stable tag that changes when the underlying art changes, so a swapped poster busts client/CDN caches.
    private static string? Tag(Guid collectionId, string slot, string? url) =>
        string.IsNullOrEmpty(url)
            ? null
            : Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"{collectionId:N}|{slot}|{url}")))[..16];
}
