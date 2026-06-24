using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Collections;

/// <summary>
/// Persists the franchise/collection of a single movie from a cached provider payload: upserts the
/// <see cref="MovieCollection"/> by <c>(Provider, ProviderId)</c> so it is shared across its movies, then
/// points <see cref="MediaItem.CollectionId"/> at it. Idempotent — re-running with the same payload is a
/// no-op, a re-fetch that changes the franchise re-points the link, and one that drops it clears the link.
/// Non-movies and movies outside any collection converge to a null link. Mirrors <c>PersonSyncService</c>.
/// </summary>
public sealed class CollectionSyncService(MediaServerDbContext database)
{
    /// <summary>
    /// Syncs the collection link for <paramref name="mediaItemId"/> from <paramref name="raw"/> (a TMDb movie
    /// detail payload). Returns true when the movie is linked to a collection, false otherwise.
    /// </summary>
    public async Task<bool> SyncAsync(Guid mediaItemId, string provider, string? raw, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.FirstOrDefaultAsync(entity => entity.Id == mediaItemId, cancellationToken);
        if (item is null || item.Kind != MediaKind.Movie)
        {
            return false;
        }

        var info = CollectionMetadata.Parse(raw);
        if (info is null)
        {
            // The movie is in no collection (or the payload lost it): converge to unlinked.
            if (item.CollectionId is not null)
            {
                item.CollectionId = null;
                await database.SaveChangesAsync(cancellationToken);
            }

            return false;
        }

        var collection = await UpsertCollectionAsync(provider, info, cancellationToken);
        if (item.CollectionId != collection.Id)
        {
            item.CollectionId = collection.Id;
            await database.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Upserts the <see cref="MovieCollection"/> for <paramref name="info"/>. Only writes when it is new or an
    /// attribute actually changed, so a re-enrich does not churn <c>UpdatedAt</c> for unchanged collections.
    /// Retries once if a concurrent sync inserts the shared collection first and trips the
    /// <c>(Provider, ProviderId)</c> unique index.
    /// </summary>
    private async Task<MovieCollection> UpsertCollectionAsync(string provider, CollectionInfo info, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var collection = await database.MovieCollections
                .FirstOrDefaultAsync(entity => entity.Provider == provider && entity.ProviderId == info.ProviderId, cancellationToken);
            var now = DateTimeOffset.UtcNow;

            if (collection is null)
            {
                collection = new MovieCollection
                {
                    Id = Guid.NewGuid(),
                    Provider = provider,
                    ProviderId = info.ProviderId,
                    Name = info.Name,
                    PosterPath = info.PosterPath,
                    PosterUrl = CollectionMetadata.ImageUrl(info.PosterPath),
                    BackdropPath = info.BackdropPath,
                    BackdropUrl = CollectionMetadata.ImageUrl(info.BackdropPath),
                    UpdatedAt = now,
                };
                database.MovieCollections.Add(collection);
            }
            else if (collection.Name != info.Name || collection.PosterPath != info.PosterPath || collection.BackdropPath != info.BackdropPath)
            {
                collection.Name = info.Name;
                collection.PosterPath = info.PosterPath;
                collection.PosterUrl = CollectionMetadata.ImageUrl(info.PosterPath);
                collection.BackdropPath = info.BackdropPath;
                collection.BackdropUrl = CollectionMetadata.ImageUrl(info.BackdropPath);
                collection.UpdatedAt = now;
            }

            try
            {
                await database.SaveChangesAsync(cancellationToken);
                return collection;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // A concurrent sync inserted this (Provider, ProviderId) collection first and the unique index
                // rejected ours. Detach just our losing insert and retry once, reloading to adopt the winner.
                var entry = database.Entry(collection);
                if (entry.State == EntityState.Added)
                {
                    entry.State = EntityState.Detached;
                }
            }
        }
    }
}
