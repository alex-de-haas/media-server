using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>
/// Turns an explicit favorite or unfavorite into outbound work for a connected provider.
/// </summary>
/// <remarks>
/// Two rules shape everything here. First, only an <b>explicit action</b> queues anything: a row that
/// merely disappears — a tombstone's full purge, a bulk cleanup — says nothing about the work, so this
/// is called from the favorite endpoints rather than a <c>SaveChanges</c> hook, which bulk deletes
/// bypass anyway. Second, only <b>movies and series</b> sync: providers hold favorites for works, and a
/// favorited season or episode stays local rather than being approximated by its series.
/// </remarks>
public sealed class FavoritesRecorder(
    MediaServerDbContext database,
    IWatchHistoryProviderRegistry registry,
    TimeProvider time,
    ILogger<FavoritesRecorder> logger)
{
    /// <summary>
    /// Stages the provider-side consequence of a favorite change. Does not save — the caller's own
    /// <c>SaveChangesAsync</c> commits the flag and this event together, so a queued push can never
    /// outlive the local change it describes.
    /// </summary>
    public async Task StageAsync(int appUserId, MediaItem item, bool favorite, CancellationToken cancellationToken)
    {
        var identity = WatchHistoryIdentityMapper.FavoriteIdentityOf(item);
        if (identity is null)
        {
            return; // A season, an episode, an extra, or a work with no external id: nothing to send.
        }

        var connection = await database.WatchHistoryConnections.FirstOrDefaultAsync(
            link => link.AppUserId == appUserId && link.Status == WatchHistoryConnectionStatus.Connected,
            cancellationToken);
        if (connection is null)
        {
            return;
        }

        if (registry.FindFavorites(connection.ProviderKey) is null)
        {
            return; // The connected provider does not carry favorites.
        }

        // While a work can still be two library items (a duplicate pair the single-catalog audit has not
        // repaired yet), the provider holds one favorite for it: it is favorited there when *any* copy
        // is favorited here, so an unfavorite only travels once the last copy is cleared.
        if (!favorite && await AnotherCopyIsFavoritedAsync(appUserId, item, cancellationToken))
        {
            logger.LogDebug("Not un-favoriting remotely: another library copy of this work is still favorited.");
            return;
        }

        var operation = favorite
            ? WatchHistoryOutboxOperation.AddFavorite
            : WatchHistoryOutboxOperation.RemoveFavorite;

        // Two clicks that leave the work in the same state are the same change; a later opposite change
        // must queue its own event, so the toggle's direction and the moment it happened both key it.
        var discriminator = time.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var idempotencyKey = string.Join(
            ':',
            connection.Id.ToString("N"),
            IdentityKey(identity),
            operation.ToString(),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(discriminator))));

        // Supersede queued work for the same identity: a favorite followed by an unfavorite before
        // either was delivered must not send both, in whichever order the worker happened to pick them.
        var pending = await database.WatchHistoryOutboxEvents
            .Where(candidate => candidate.ConnectionId == connection.Id &&
                candidate.Status == WatchHistoryOutboxStatus.Pending &&
                (candidate.Operation == WatchHistoryOutboxOperation.AddFavorite ||
                 candidate.Operation == WatchHistoryOutboxOperation.RemoveFavorite) &&
                candidate.IdentitySnapshot == Snapshot(identity))
            .ToListAsync(cancellationToken);
        database.WatchHistoryOutboxEvents.RemoveRange(pending);

        database.WatchHistoryOutboxEvents.Add(new WatchHistoryOutboxEvent
        {
            Id = Guid.NewGuid(),
            ConnectionId = connection.Id,
            AppUserId = appUserId,
            MediaItemId = item.Id,
            Operation = operation,
            IdentitySnapshot = Snapshot(identity),
            OccurredAt = time.GetUtcNow(),
            IdempotencyKey = idempotencyKey,
            Status = WatchHistoryOutboxStatus.Pending,
            CreatedAt = time.GetUtcNow(),
            NextAttemptAt = time.GetUtcNow(),
        });
    }

    /// <summary>
    /// Whether another library item for the same work still carries this user's favorite. Only relevant
    /// until the single-catalog audit clears the last pre-existing duplicate pair.
    /// </summary>
    private async Task<bool> AnotherCopyIsFavoritedAsync(int appUserId, MediaItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.IdentityProvider) || string.IsNullOrWhiteSpace(item.IdentityProviderId))
        {
            return false;
        }

        return await database.UserItemData.AsNoTracking()
            .Where(data => data.AppUserId == appUserId && data.IsFavorite && data.MediaItemId != item.Id)
            .Join(database.MediaItems.AsNoTracking(), data => data.MediaItemId, other => other.Id, (_, other) => other)
            .AnyAsync(other => other.Kind == item.Kind &&
                other.IdentityProvider == item.IdentityProvider &&
                other.IdentityProviderId == item.IdentityProviderId, cancellationToken);
    }

    /// <summary>
    /// Frozen at the moment of the change, like the watch-history snapshot: delivery runs later, and by
    /// then the item may have been deleted, re-identified, or moved to another catalog.
    /// </summary>
    internal static string Snapshot(FavoriteIdentity identity) => JsonSerializer.Serialize(identity);

    internal static FavoriteIdentity? Deserialize(string? snapshot) =>
        string.IsNullOrWhiteSpace(snapshot) ? null : JsonSerializer.Deserialize<FavoriteIdentity>(snapshot);

    private static string IdentityKey(FavoriteIdentity identity) =>
        $"{identity.Kind}:{identity.TmdbId?.ToString(CultureInfo.InvariantCulture) ?? identity.ImdbId}";
}
