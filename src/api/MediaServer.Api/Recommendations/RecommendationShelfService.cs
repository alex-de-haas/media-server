using System.Collections.Concurrent;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations;

/// <summary>
/// Collapses concurrent shelf rebuilds onto one task per user, and runs the background half of
/// stale-while-revalidate.
/// </summary>
/// <remarks>
/// A singleton, because the thing it guards is shared across requests: Infuse fans
/// <c>Items/Latest</c> out across every library within the same millisecond, so without this a single
/// home-screen refresh would start one rebuild per view. The rebuild itself needs a database context,
/// which is scoped, so a background refresh gets its own scope rather than borrowing the request's —
/// that one is disposed the moment the response is written.
/// </remarks>
public sealed class RecommendationShelfRefresher(
    IServiceScopeFactory scopes, ILogger<RecommendationShelfRefresher> logger)
{
    private readonly ConcurrentDictionary<int, Task> inFlight = new();

    /// <summary>Rebuilds this user's shelf, joining the running rebuild if there already is one.</summary>
    public Task RefreshAsync(int appUserId, CancellationToken cancellationToken)
    {
        // GetOrAdd's factory can run more than once under contention, so the task is created lazily and
        // only the winner's copy is ever started.
        var lazy = new Lazy<Task>(() => RunAsync(appUserId), LazyThreadSafetyMode.ExecutionAndPublication);
        var task = inFlight.GetOrAdd(appUserId, _ => lazy.Value);
        return task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Starts a rebuild without waiting for it. Used when a stale shelf is served as-is: the caller
    /// gets yesterday's answer now rather than today's in a second.
    /// </summary>
    public void RefreshInBackground(int appUserId)
    {
        // Deliberately not awaited. Failures are logged inside RunAsync, and a refresh that does not
        // land simply leaves the stale shelf in place until the next read tries again.
        _ = RefreshAsync(appUserId, CancellationToken.None);
    }

    private async Task RunAsync(int appUserId)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var shelf = scope.ServiceProvider.GetRequiredService<RecommendationShelfService>();
            await shelf.RebuildAsync(appUserId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            // A shelf that cannot be rebuilt is not worth failing a page for: the reader falls back to
            // whatever generation is already stored, or to an absent view.
            logger.LogWarning(exception, "Rebuilding the recommendation shelf for user {UserId} failed.", appUserId);
        }
        finally
        {
            inFlight.TryRemove(appUserId, out _);
        }
    }
}

/// <summary>
/// What a consuming surface needs from the shelf: the titles, and whether there are any.
/// </summary>
/// <remarks>
/// Narrow on purpose. The Jellyfin layer should not have to know how a shelf is built — behind this
/// sit the provider registry, the fusion and the TMDb caches, none of which a view has any business
/// constructing.
/// </remarks>
public interface IRecommendationShelf
{
    /// <summary>The shelf as a client should see it: held, unwatched, unhidden titles in rank order.</summary>
    Task<IReadOnlyList<MediaItem>> GetAsync(int appUserId, int? limit, CancellationToken cancellationToken);

    /// <summary>Whether this user has anything to show at all.</summary>
    Task<bool> AnyAsync(int appUserId, CancellationToken cancellationToken);
}

/// <summary>
/// One user's Jellyfin recommendation shelf: a stored, ranked selection of titles the library holds,
/// filtered down to what is still worth offering at the moment it is read.
/// </summary>
/// <remarks>
/// See <c>docs/features/recommendation-providers/feature.md</c>. The stored rows are candidates, not
/// the finished row — watched and hidden titles are excluded on every read rather than by
/// invalidating the shelf, so a film disappears the moment it is played instead of when the shelf
/// next expires.
/// </remarks>
public sealed class RecommendationShelfService(
    MediaServerDbContext database,
    RecommendationFeedService feed,
    RecommendationShelfRefresher refresher,
    TimeProvider clock,
    ILogger<RecommendationShelfService> logger) : IRecommendationShelf
{
    /// <summary>
    /// How long a generation stands. Long enough that the shelf is not a slot machine — a user who
    /// opens Infuse twice in an evening sees the same titles — and short enough to follow taste.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromDays(1);

    /// <summary>
    /// How many candidates a generation stores: an order of magnitude more than a row shows, so that
    /// read-time filtering still leaves a full row after a heavy watching session.
    /// </summary>
    public const int Capacity = 100;

    /// <summary>
    /// The shelf as a client should see it: held, unwatched, unhidden titles in rank order.
    /// </summary>
    /// <param name="limit">Maximum rows to return; null for everything that survives filtering.</param>
    public async Task<IReadOnlyList<MediaItem>> GetAsync(
        int appUserId, int? limit, CancellationToken cancellationToken)
    {
        var stored = await StoredAsync(appUserId, cancellationToken);

        if (stored.Count == 0)
        {
            // Nothing to serve and nothing to fall back on, so this one is worth waiting for.
            await refresher.RefreshAsync(appUserId, cancellationToken);
            stored = await StoredAsync(appUserId, cancellationToken);
        }
        else if (IsStale(stored[0].GeneratedAt))
        {
            // Serve the old generation and rebuild behind the request: the hourly /UserViews must
            // never pay for a rebuild.
            refresher.RefreshInBackground(appUserId);
        }

        if (stored.Count == 0)
        {
            return [];
        }

        var excluded = await ExcludedAsync(appUserId, stored, cancellationToken);
        IEnumerable<MediaItem> surviving = stored
            .Where(row => !excluded.Contains(row.MediaItemId))
            .Select(row => row.MediaItem!)
            // Published only: an item that lost its public id cannot be addressed by a client.
            .Where(item => item.PublicId != null);

        if (limit is { } wanted)
        {
            surviving = surviving.Take(wanted);
        }

        return [.. surviving];
    }

    /// <summary>Whether this user has anything to show — the question the view list asks.</summary>
    /// <remarks>
    /// Deliberately the full read rather than a cheap <c>Any()</c> against the table: a shelf whose
    /// every title has since been watched is empty in the only sense that matters, and advertising a
    /// library that opens onto nothing is worse than not advertising it.
    /// </remarks>
    public async Task<bool> AnyAsync(int appUserId, CancellationToken cancellationToken) =>
        (await GetAsync(appUserId, limit: 1, cancellationToken)).Count > 0;

    /// <summary>Recomputes this user's generation and replaces the stored one wholesale.</summary>
    public async Task RebuildAsync(int appUserId, CancellationToken cancellationToken)
    {
        var ranked = await feed.BuildShelfAsync(appUserId, Capacity, cancellationToken);
        var generatedAt = clock.GetUtcNow();

        var existing = await database.RecommendationShelfItems
            .Where(row => row.AppUserId == appUserId)
            .ToListAsync(cancellationToken);
        database.RecommendationShelfItems.RemoveRange(existing);

        // Replaced wholesale rather than patched: ranks are positions in one list, so a partial update
        // would leave two generations interleaved, and the unique (user, rank) index would reject it.
        for (var rank = 0; rank < ranked.Count; rank++)
        {
            database.RecommendationShelfItems.Add(new RecommendationShelfItem
            {
                Id = Guid.NewGuid(),
                AppUserId = appUserId,
                Rank = rank,
                MediaItemId = ranked[rank],
                GeneratedAt = generatedAt,
            });
        }

        await database.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Rebuilt the recommendation shelf for user {UserId}: {Count} titles.", appUserId, ranked.Count);
    }

    private bool IsStale(DateTimeOffset generatedAt) => clock.GetUtcNow() - generatedAt >= Ttl;

    private async Task<List<RecommendationShelfItem>> StoredAsync(int appUserId, CancellationToken cancellationToken) =>
        await database.RecommendationShelfItems.AsNoTracking()
            .Include(row => row.MediaItem)
            .Where(row => row.AppUserId == appUserId && row.MediaItem != null)
            .OrderBy(row => row.Rank)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The stored titles this user should not be offered right now: already watched, or dismissed.
    /// </summary>
    private async Task<HashSet<Guid>> ExcludedAsync(
        int appUserId, IReadOnlyList<RecommendationShelfItem> stored, CancellationToken cancellationToken)
    {
        var itemIds = stored.Select(row => row.MediaItemId).ToHashSet();

        var played = await database.UserItemData.AsNoTracking()
            .Where(row => row.AppUserId == appUserId && row.Played && itemIds.Contains(row.MediaItemId))
            .Select(row => row.MediaItemId)
            .ToListAsync(cancellationToken);

        // A series counts as seen once any episode has been played — a part-watched show belongs to
        // Next Up, not here — which is why this joins episodes back to their series.
        var playedSeries = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId)
            .Join(
                database.MediaItems.AsNoTracking(),
                entry => entry.MediaItemId,
                item => item.Id,
                (_, item) => item.Kind == MediaKind.Episode && item.SeriesId != null ? item.SeriesId!.Value : item.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        var excluded = played.Concat(playedSeries).Where(itemIds.Contains).ToHashSet();

        // Hides are keyed by TMDb identity rather than by local item, so they survive a title being
        // removed and re-added; resolving them means reading each stored item's provider id.
        var hides = await database.RecommendationHides.AsNoTracking()
            .Where(hide => hide.AppUserId == appUserId)
            .Select(hide => new { hide.Kind, hide.TmdbId })
            .ToListAsync(cancellationToken);

        if (hides.Count > 0)
        {
            var hidden = hides.Select(hide => new RecommendationIdentity(hide.Kind, hide.TmdbId)).ToHashSet();
            foreach (var row in stored)
            {
                var item = row.MediaItem!;
                if (RecommendationSeedSelector.TmdbIdOf(item) is not { } tmdbId)
                {
                    continue;
                }

                var kind = item.Kind == MediaKind.Movie ? RecommendationKind.Movie : RecommendationKind.Series;
                if (hidden.Contains(new RecommendationIdentity(kind, tmdbId)))
                {
                    excluded.Add(row.MediaItemId);
                }
            }
        }

        return excluded;
    }
}
