using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>What reconciliation would do to one work, in the user's terms.</summary>
public enum FavoriteSyncAction
{
    /// <summary>Favorited here since the last reconciliation — send it to the provider.</summary>
    AddRemotely,

    /// <summary>Favorited at the provider since then — flag it here.</summary>
    AddLocally,

    /// <summary>Unfavorited here — remove it at the provider.</summary>
    RemoveRemotely,

    /// <summary>Unfavorited at the provider — clear it here.</summary>
    RemoveLocally,

    /// <summary>Favorited at the provider, but this library holds no such title — nothing to flag.</summary>
    SkippedNotInLibrary,
}

/// <summary>One line of the favorites plan.</summary>
public sealed record FavoriteSyncEntry(string Title, FavoriteSyncAction Action);

/// <summary>
/// The two-way favorites plan. <paramref name="RemoteCount"/> and <paramref name="Capacity"/> report
/// how full the provider's list is, so a user near the cap learns it here rather than from a failed
/// write.
/// </summary>
public sealed record FavoritesSyncPlan(
    IReadOnlyList<FavoriteSyncEntry> Entries,
    IReadOnlyDictionary<FavoriteSyncAction, int> Counts,
    int? RemoteCount,
    int? Capacity);

/// <summary>
/// Reconciles favorites between this library and a connected provider, inside the same explicit sync
/// the watched history uses. Nothing here runs on a timer: sync is the only inbound path.
/// </summary>
/// <remarks>
/// The comparison is three-way, not two: local now, remote now, and what the last reconciliation saw
/// (<see cref="WatchHistoryFavoriteState"/>). Without that memory a work favorited here and absent
/// there is indistinguishable from one unfavorited there and still flagged here, and reconciliation
/// would have to guess which side to follow.
/// </remarks>
public sealed class FavoritesSyncService(
    MediaServerDbContext database,
    IWatchHistoryProviderRegistry registry,
    TimeProvider time,
    ILogger<FavoritesSyncService> logger)
{
    public async Task<WatchHistoryResult<FavoritesSyncPlan>> PreviewAsync(
        int appUserId, string providerKey, CancellationToken cancellationToken) =>
        await ReconcileAsync(appUserId, providerKey, apply: false, cancellationToken);

    public async Task<WatchHistoryResult<FavoritesSyncPlan>> ApplyAsync(
        int appUserId, string providerKey, CancellationToken cancellationToken) =>
        await ReconcileAsync(appUserId, providerKey, apply: true, cancellationToken);

    private async Task<WatchHistoryResult<FavoritesSyncPlan>> ReconcileAsync(
        int appUserId, string providerKey, bool apply, CancellationToken cancellationToken)
    {
        // Keyed by the provider the route named, not merely by the user: the schema allows one
        // connection per provider, so picking "the user's connection" would reconcile one provider's
        // favorites against another's account the day a second provider exists.
        var connection = await database.WatchHistoryConnections
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.ProviderKey == providerKey, cancellationToken);
        if (connection is null)
        {
            return WatchHistoryResult<FavoritesSyncPlan>.Failed(
                WatchHistoryFailure.AuthenticationRequired, $"'{providerKey}' is not connected.");
        }

        var provider = registry.FindFavorites(connection.ProviderKey);
        if (provider is null)
        {
            return WatchHistoryResult<FavoritesSyncPlan>.Failed(
                WatchHistoryFailure.Unsupported, $"'{connection.ProviderKey}' does not carry favorites.");
        }

        var snapshot = await provider.GetFavoritesAsync(appUserId, cancellationToken);
        if (!snapshot.Succeeded)
        {
            return WatchHistoryResult<FavoritesSyncPlan>.Failed(snapshot.Failure!.Value, snapshot.Detail, snapshot.RetryAfter);
        }

        // Local side: every movie/series this library can name to a provider, published or tombstoned.
        // Tombstones are included on purpose — a deleted-but-loved title keeps its favorite, and an
        // inbound favorite for one lands back on its ghost rather than being reported as missing.
        var works = await database.MediaItems.AsNoTracking()
            .Where(item => (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series) &&
                item.IdentityProvider != null && item.IdentityProviderId != null)
            .ToListAsync(cancellationToken);
        var favoriteItemIds = await database.UserItemData.AsNoTracking()
            .Where(data => data.AppUserId == appUserId && data.IsFavorite)
            .Select(data => data.MediaItemId)
            .ToListAsync(cancellationToken);
        var favorites = favoriteItemIds.ToHashSet();

        var localByKey = new Dictionary<string, LocalWork>(StringComparer.Ordinal);
        foreach (var item in works)
        {
            if (WatchHistoryIdentityMapper.FavoriteIdentityOf(item) is not { } identity)
            {
                continue;
            }

            var key = KeyOf(identity);
            // A work can still be two rows until the single-catalog audit repairs a pre-existing pair;
            // it counts as favorited when any copy is.
            if (localByKey.TryGetValue(key, out var existing))
            {
                localByKey[key] = existing with
                {
                    ItemIds = [.. existing.ItemIds, item.Id],
                    IsFavorite = existing.IsFavorite || favorites.Contains(item.Id),
                };
                continue;
            }

            localByKey[key] = new LocalWork(identity, item.Title, [item.Id], favorites.Contains(item.Id));
        }

        var remoteByKey = snapshot.Value!.Favorites
            .Where(favorite => favorite.Identity.IsResolvable)
            .GroupBy(favorite => KeyOf(favorite.Identity), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var states = await database.WatchHistoryFavoriteStates
            .Where(state => state.ConnectionId == connection.Id)
            .ToListAsync(cancellationToken);
        var stateByKey = states.ToDictionary(
            state => $"{state.Kind}:{state.IdentityProvider}:{state.IdentityProviderId}", StringComparer.Ordinal);

        var entries = new List<FavoriteSyncEntry>();
        var now = time.GetUtcNow();

        foreach (var (key, local) in localByKey)
        {
            var remote = remoteByKey.TryGetValue(key, out var found) ? found : null;
            var previous = stateByKey.GetValueOrDefault(key);
            var action = Decide(local.IsFavorite, remote is not null, previous);
            if (action is null)
            {
                if (apply)
                {
                    // Both sides agree. Remember it only while there is something to remember: a work
                    // favorited nowhere needs no memory, and writing one per library title would store a
                    // row for every film the user never marked.
                    UpsertState(connection.Id, local.Identity, remote, local.IsFavorite,
                        remotePresent: remote is not null, now, previous, keep: local.IsFavorite);
                }

                continue;
            }

            entries.Add(new FavoriteSyncEntry(local.Title, action.Value));
            if (!apply)
            {
                continue;
            }

            switch (action.Value)
            {
                case FavoriteSyncAction.AddRemotely:
                case FavoriteSyncAction.RemoveRemotely:
                    // Outbound work goes through the outbox like every other provider write, so a
                    // failure retries (or turns terminal) with the rest of it. The remembered remote
                    // side stays what the snapshot actually showed — claiming the write already landed
                    // would make the next reconciliation read the unchanged provider as a fresh remote
                    // edit and propose undoing the user's own favorite.
                    await QueueAsync(connection, appUserId, local, action.Value == FavoriteSyncAction.AddRemotely, now, cancellationToken);
                    UpsertState(connection.Id, local.Identity, remote, local.IsFavorite, remotePresent: remote is not null, now, previous);
                    break;

                case FavoriteSyncAction.AddLocally:
                case FavoriteSyncAction.RemoveLocally:
                    var nowFavorite = action.Value == FavoriteSyncAction.AddLocally;
                    await SetLocalAsync(appUserId, local.ItemIds, nowFavorite, cancellationToken);
                    UpsertState(connection.Id, local.Identity, remote, nowFavorite, remotePresent: remote is not null, now, previous);
                    break;
            }
        }

        // Remote favorites this library cannot hold: reported with a count rather than silently dropped.
        foreach (var (key, remote) in remoteByKey)
        {
            if (localByKey.ContainsKey(key))
            {
                continue;
            }

            entries.Add(new FavoriteSyncEntry(DescribeRemote(remote.Identity), FavoriteSyncAction.SkippedNotInLibrary));
        }

        if (apply)
        {
            connection.FavoritesRemoteCount = snapshot.Value!.RemoteCount;
            connection.FavoritesCapacity = snapshot.Value!.Capacity;
            await database.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Favorites sync applied {Count} change(s).", entries.Count);
        }

        var counts = entries.GroupBy(entry => entry.Action).ToDictionary(group => group.Key, group => group.Count());
        return WatchHistoryResult<FavoritesSyncPlan>.Success(
            new FavoritesSyncPlan(entries, counts, snapshot.Value!.RemoteCount, snapshot.Value!.Capacity));
    }

    /// <summary>
    /// The three-way decision. With no memory of a previous reconciliation, a one-sided favorite is
    /// treated as an addition on that side — the conservative reading, because the alternative would
    /// silently clear a flag the user set before favorites sync existed.
    /// </summary>
    private static FavoriteSyncAction? Decide(bool local, bool remote, WatchHistoryFavoriteState? previous)
    {
        if (local == remote)
        {
            return null;
        }

        if (previous is null)
        {
            return local ? FavoriteSyncAction.AddRemotely : FavoriteSyncAction.AddLocally;
        }

        if (local && !remote)
        {
            // It was there last time: the provider lost it since, so the removal is what is new.
            return previous.RemotePresent ? FavoriteSyncAction.RemoveLocally : FavoriteSyncAction.AddRemotely;
        }

        // Remote holds it and this library does not.
        return previous.LocalFavorite ? FavoriteSyncAction.RemoveRemotely : FavoriteSyncAction.AddLocally;
    }

    private async Task SetLocalAsync(int appUserId, IReadOnlyList<Guid> itemIds, bool favorite, CancellationToken cancellationToken)
    {
        foreach (var itemId in itemIds)
        {
            var row = await database.UserItemData
                .FirstOrDefaultAsync(data => data.AppUserId == appUserId && data.MediaItemId == itemId, cancellationToken);
            if (row is null)
            {
                if (!favorite)
                {
                    continue;
                }

                row = new UserItemData { Id = Guid.NewGuid(), AppUserId = appUserId, MediaItemId = itemId };
                database.UserItemData.Add(row);
            }

            row.IsFavorite = favorite;
        }
    }

    /// <summary>
    /// Queues an outbound favorite change discovered by reconciliation. Written here rather than through
    /// <see cref="FavoritesRecorder"/> because that one speaks for a user's click on one item, while
    /// this speaks for a work the sync decided about.
    /// </summary>
    private async Task QueueAsync(
        WatchHistoryProviderConnection connection, int appUserId, LocalWork local, bool favorite,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var operation = favorite ? WatchHistoryOutboxOperation.AddFavorite : WatchHistoryOutboxOperation.RemoveFavorite;
        var snapshot = FavoritesRecorder.Snapshot(local.Identity);

        var superseded = await database.WatchHistoryOutboxEvents
            .Where(candidate => candidate.ConnectionId == connection.Id &&
                candidate.Status == WatchHistoryOutboxStatus.Pending &&
                (candidate.Operation == WatchHistoryOutboxOperation.AddFavorite ||
                 candidate.Operation == WatchHistoryOutboxOperation.RemoveFavorite) &&
                candidate.IdentitySnapshot == snapshot)
            .ToListAsync(cancellationToken);
        database.WatchHistoryOutboxEvents.RemoveRange(superseded);

        database.WatchHistoryOutboxEvents.Add(new WatchHistoryOutboxEvent
        {
            Id = Guid.NewGuid(),
            ConnectionId = connection.Id,
            AppUserId = appUserId,
            MediaItemId = local.ItemIds[0],
            Operation = operation,
            IdentitySnapshot = snapshot,
            OccurredAt = now,
            IdempotencyKey = $"sync:{connection.Id:N}:{KeyOf(local.Identity)}:{operation}:{now.UtcDateTime:O}",
            Status = WatchHistoryOutboxStatus.Pending,
            CreatedAt = now,
            NextAttemptAt = now,
        });
    }

    private void UpsertState(
        Guid connectionId, FavoriteIdentity identity, ProviderFavorite? remote, bool local,
        bool remotePresent, DateTimeOffset now, WatchHistoryFavoriteState? existing, bool keep = true)
    {
        if (!keep)
        {
            // Nothing to remember any more: an absent row reads as "never favorited", which is exactly
            // the state both sides are now in.
            if (existing is not null)
            {
                database.WatchHistoryFavoriteStates.Remove(existing);
            }

            return;
        }

        var (provider, providerId) = ProviderPair(identity);
        if (existing is null)
        {
            existing = new WatchHistoryFavoriteState
            {
                Id = Guid.NewGuid(),
                ConnectionId = connectionId,
                Kind = identity.Kind == FavoriteWorkKind.Movie ? MediaKind.Movie : MediaKind.Series,
                IdentityProvider = provider,
                IdentityProviderId = providerId,
            };
            database.WatchHistoryFavoriteStates.Add(existing);
        }

        // The local side records what this reconciliation just made true; the remote side records only
        // what the provider actually showed. Queued work is not yet a fact about the provider.
        existing.RemotePresent = remotePresent;
        existing.RemoteFavoritedAt = remote?.FavoritedAt ?? existing.RemoteFavoritedAt;
        existing.LocalFavorite = local;
        existing.ReconciledAt = now;
    }

    private static string KeyOf(FavoriteIdentity identity)
    {
        var (provider, providerId) = ProviderPair(identity);
        return $"{(identity.Kind == FavoriteWorkKind.Movie ? MediaKind.Movie : MediaKind.Series)}:{provider}:{providerId}";
    }

    private static (string Provider, string ProviderId) ProviderPair(FavoriteIdentity identity) =>
        identity.TmdbId is { } tmdb ? ("tmdb", tmdb.ToString()) : ("imdb", identity.ImdbId ?? string.Empty);

    private static string DescribeRemote(FavoriteIdentity identity) =>
        identity.TmdbId is { } tmdb ? $"TMDb {tmdb}" : identity.ImdbId ?? "unknown title";

    private sealed record LocalWork(
        FavoriteIdentity Identity, string Title, IReadOnlyList<Guid> ItemIds, bool IsFavorite);
}
