using System.Globalization;
using System.Text.Json;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>
/// Records per-play history and the outbound intent that follows from it.
/// </summary>
/// <remarks>
/// Every method here <b>stages</b> changes on the caller's <see cref="MediaServerDbContext"/> and
/// never saves. That is the whole point: the local state change, the history entry and the outbox
/// event have to commit together, or a crash between them leaves the app believing it delivered
/// something it never enqueued — or enqueued something that never happened.
///
/// History is recorded whether or not a provider is connected: it is the local source of truth the
/// aggregate counters are projected from, and it is what a later connection has to export. Outbox
/// events are only staged when there is a connection to deliver them to.
/// </remarks>
public sealed class WatchHistoryRecorder(
    MediaServerDbContext database,
    WatchHistoryIdentityMapper identities,
    TimeProvider time,
    ILogger<WatchHistoryRecorder> logger)
{
    /// <summary>
    /// Records a proven completion: one exact play, linked to the session that observed it.
    /// </summary>
    /// <returns>The staged entry, so the caller can link its session gate to it.</returns>
    public async Task<PlaybackHistoryEntry?> StageCompletionAsync(
        int appUserId, MediaItem item, UserItemData row, string? playSessionId, DateTimeOffset watchedAt,
        CancellationToken cancellationToken)
    {
        // The session gate already decided this is a first crossing; the unique index on
        // (user, item, session) is the backstop if two reports somehow race here.
        if (playSessionId is not null && await database.PlaybackHistoryEntries.AnyAsync(
                entry => entry.AppUserId == appUserId
                    && entry.MediaItemId == item.Id
                    && entry.PlaySessionId == playSessionId,
                cancellationToken))
        {
            return null;
        }

        var identity = await identities.MapAsync(item, cancellationToken);
        var entry = new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(),
            AppUserId = appUserId,
            MediaItemId = item.Id,
            CreatedAt = time.GetUtcNow(),
            WatchedAt = watchedAt,
            Origin = PlaybackHistoryOrigin.LocalPlayback,
            PlaySessionId = playSessionId,
            IdentitySnapshot = Snapshot(identity),
            LinkStatus = PlaybackHistoryLinkStatus.None,
        };
        database.PlaybackHistoryEntries.Add(entry);

        await StageOutboxAsync(
            appUserId, item, row, entry, WatchHistoryOutboxOperation.AddExactWatch, identity, watchedAt, playSessionId, cancellationToken);

        return entry;
    }

    /// <summary>
    /// Records an explicit "I watched this": at most one timeless entry, and only when there is no
    /// history at all.
    /// </summary>
    /// <remarks>
    /// A toggle back to watched is not a new viewing — the flag says nothing about how many times
    /// something was seen. Adding an entry per toggle would inflate the count and, worse, export a
    /// second play to the provider for a click.
    /// </remarks>
    public async Task StageManualWatchedAsync(
        int appUserId, MediaItem item, UserItemData row, CancellationToken cancellationToken)
    {
        var hasHistory = await database.PlaybackHistoryEntries.AnyAsync(
            entry => entry.AppUserId == appUserId && entry.MediaItemId == item.Id, cancellationToken);

        var identity = await identities.MapAsync(item, cancellationToken);
        if (!hasHistory)
        {
            database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
            {
                Id = Guid.NewGuid(),
                AppUserId = appUserId,
                MediaItemId = item.Id,
                CreatedAt = time.GetUtcNow(),
                // Null, not "now": a manual mark says the item was watched, not when.
                WatchedAt = null,
                Origin = PlaybackHistoryOrigin.Manual,
                IdentitySnapshot = Snapshot(identity),
                LinkStatus = PlaybackHistoryLinkStatus.None,
            });
        }

        // The provider still has to be told, even when local history already existed: it may hold
        // nothing for this item. The worker decides that with a read-before-write.
        await StageOutboxAsync(
            appUserId, item, row, entry: null, WatchHistoryOutboxOperation.EnsureTimelessWatched,
            identity, occurredAt: null, sessionKey: null, cancellationToken);
    }

    /// <summary>
    /// Records an explicit unwatch: drops the timeless entries this app created, keeps everything
    /// else, and asks the provider to remove only the entries it owns.
    /// </summary>
    /// <remarks>
    /// Exact plays and provider-imported history survive. Unwatch is a statement about current state,
    /// not a claim that the viewings never happened — which is also why the aggregate play count is
    /// left alone.
    /// </remarks>
    public async Task StageUnwatchedAsync(
        int appUserId, MediaItem item, UserItemData row, CancellationToken cancellationToken)
    {
        var owned = await database.PlaybackHistoryEntries
            .Where(entry => entry.AppUserId == appUserId
                && entry.MediaItemId == item.Id
                && entry.WatchedAt == null
                && (entry.Origin == PlaybackHistoryOrigin.Manual || entry.Origin == PlaybackHistoryOrigin.Legacy))
            .ToListAsync(cancellationToken);

        if (owned.Count > 0)
        {
            database.PlaybackHistoryEntries.RemoveRange(owned);
        }

        var identity = await identities.MapAsync(item, cancellationToken);
        await StageOutboxAsync(
            appUserId, item, row, entry: null, WatchHistoryOutboxOperation.RemoveOwnedTimelessEntries,
            identity, occurredAt: null, sessionKey: null, cancellationToken);
    }

    private async Task StageOutboxAsync(
        int appUserId,
        MediaItem item,
        UserItemData row,
        PlaybackHistoryEntry? entry,
        WatchHistoryOutboxOperation operation,
        WatchHistoryIdentityResult identity,
        DateTimeOffset? occurredAt,
        string? sessionKey,
        CancellationToken cancellationToken)
    {
        var connection = await database.WatchHistoryConnections
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.Status == WatchHistoryConnectionStatus.Connected, cancellationToken);

        if (connection is null)
        {
            // Nothing to deliver to. The local history above still stands, so connecting later has
            // something to export.
            return;
        }

        if (!identity.Resolved)
        {
            // Queueing work that can never be addressed would retry forever. The local change already
            // succeeded; the user sees the gap as a sync issue rather than a failed action.
            logger.LogInformation(
                "Not queueing {Operation} for a local item that cannot be identified ({Issue}).", operation, identity.Issue);
            return;
        }

        // What makes two enqueues "the same change" differs by operation:
        //  - a completion is identified by the playback session that produced it;
        //  - a manual mark or unwatch is identified by the watched-state transition it followed.
        // StateRevision deliberately is not used: it advances on any row touch, including re-marking
        // an already-watched item, which would queue a fresh event — and a second viewing on the
        // user's profile — for a click that changed nothing.
        // Taken from the caller's row rather than re-queried: on a first mark the row is staged and
        // unsaved, so a query would see nothing and produce a different key than the repeat does.
        var discriminator = sessionKey
            ?? row.WatchedStateChangedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            ?? string.Empty;

        var idempotencyKey = string.Join(
            ':',
            connection.Id.ToString("N"),
            item.Id.ToString("N"),
            operation.ToString(),
            discriminator);

        if (await database.WatchHistoryOutboxEvents.AnyAsync(
                existing => existing.IdempotencyKey == idempotencyKey, cancellationToken))
        {
            return;
        }

        database.WatchHistoryOutboxEvents.Add(new WatchHistoryOutboxEvent
        {
            Id = Guid.NewGuid(),
            ConnectionId = connection.Id,
            AppUserId = appUserId,
            MediaItemId = item.Id,
            HistoryEntryId = entry?.Id,
            Operation = operation,
            IdentitySnapshot = Snapshot(identity),
            OccurredAt = occurredAt,
            IdempotencyKey = idempotencyKey,
            Status = WatchHistoryOutboxStatus.Pending,
            CreatedAt = time.GetUtcNow(),
            NextAttemptAt = time.GetUtcNow(),
        });
    }

    // Frozen at the moment of the change: delivery runs later, and by then the library may have been
    // rescanned, re-identified, or the item deleted.
    private static string? Snapshot(WatchHistoryIdentityResult identity) =>
        identity.Resolved ? JsonSerializer.Serialize(identity.Identity) : null;
}
