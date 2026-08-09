using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
            appUserId, item, row, entry, WatchHistoryOutboxOperation.AddExactWatch, identity, watchedAt,
            discriminator: playSessionId, cancellationToken);

        return entry;
    }

    /// <summary>
    /// Records a play the user states by hand, at an instant they choose: one dated entry, every time.
    /// </summary>
    /// <remarks>
    /// The counterpart to <see cref="StageManualWatchedAsync"/>, and deliberately not the same thing.
    /// The toggle is a statement about current state, so it is idempotent and timeless; this is a
    /// statement about a specific viewing, so every call is another play — that is what a rewatch is,
    /// and it is what makes a viewing the server never observed appear on the calendar at all.
    /// </remarks>
    /// <returns>The staged entry, so the caller can name it.</returns>
    public async Task<PlaybackHistoryEntry> StageLoggedWatchAsync(
        int appUserId, MediaItem item, UserItemData row, DateTimeOffset watchedAt, CancellationToken cancellationToken)
    {
        var identity = await identities.MapAsync(item, cancellationToken);
        var entry = new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(),
            AppUserId = appUserId,
            MediaItemId = item.Id,
            CreatedAt = time.GetUtcNow(),
            WatchedAt = watchedAt,
            Origin = PlaybackHistoryOrigin.Manual,
            // No session: nothing was observed. The uniqueness rule that guards observed playback does
            // not apply here — two logs at the same instant are two viewings the user claims, and only
            // they can say otherwise.
            PlaySessionId = null,
            IdentitySnapshot = Snapshot(identity),
            LinkStatus = PlaybackHistoryLinkStatus.None,
        };
        database.PlaybackHistoryEntries.Add(entry);

        await StageOutboxAsync(
            appUserId, item, row, entry, WatchHistoryOutboxOperation.AddExactWatch, identity, watchedAt,
            // Keyed on the entry, like a deletion is: logging a second play changes no state on the row,
            // so the row-derived fallback would hash to the first log's key and the second event would
            // be swallowed as a duplicate.
            discriminator: entry.Id.ToString("N"),
            cancellationToken);

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
        PlaybackHistoryEntry? timeless = null;
        if (!hasHistory)
        {
            timeless = new PlaybackHistoryEntry
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
            };
            database.PlaybackHistoryEntries.Add(timeless);
        }
        else
        {
            // No new entry, but the event still needs somewhere to record the remote id it may
            // create. An existing timeless entry is the one this app can own; exact plays are not
            // what a timeless remote mark corresponds to.
            timeless = await database.PlaybackHistoryEntries.FirstOrDefaultAsync(
                entry => entry.AppUserId == appUserId
                    && entry.MediaItemId == item.Id
                    && entry.WatchedAt == null
                    && (entry.Origin == PlaybackHistoryOrigin.Manual || entry.Origin == PlaybackHistoryOrigin.Legacy),
                cancellationToken);
        }

        // The provider still has to be told, even when local history already existed: it may hold
        // nothing for this item. The worker decides that with a read-before-write.
        //
        // The entry travels with the event so the worker has a stable place to persist the remote id
        // it resolves. Without it, a mark undone before delivery would leave a remote timeless mark
        // with no local owner — and ownership is the only thing that permits removing it later.
        await StageOutboxAsync(
            appUserId, item, row, timeless, WatchHistoryOutboxOperation.EnsureTimelessWatched,
            identity, occurredAt: null, discriminator: null, cancellationToken);
    }

    /// <summary>
    /// Records that an undated mark now has a time: the remote timeless mark this app owns is retired
    /// and the play is re-stated as an exact one.
    /// </summary>
    /// <remarks>
    /// Two events rather than one, because "watched, time unknown" and "watched at T" are different
    /// claims and the provider holds the first. Adding without removing would leave the account with
    /// the same viewing twice — and the next explicit sync would import that timeless mark straight
    /// back into the undated list the user just emptied. They are independent (a removal is addressed
    /// by remote id, an add by identity and instant), so their delivery order does not matter.
    ///
    /// The caller has already stamped <paramref name="entry"/>; this clears the link it carried,
    /// because after the removal there is no remote entry left for it to name.
    /// </remarks>
    public async Task StageMarkDatedAsync(
        int appUserId, MediaItem item, UserItemData? row, PlaybackHistoryEntry entry, DateTimeOffset watchedAt,
        CancellationToken cancellationToken)
    {
        // An Unresolved link is excluded for the reason it always is: the add committed but its id was
        // never pinned down, and removing on a guess destroys history this app did not create. The
        // remote timeless mark then survives, and a later sync can re-import it.
        var remoteId = entry is { ProviderEntryOwned: true, ProviderHistoryId: { } id }
            && entry.LinkStatus != PlaybackHistoryLinkStatus.Unresolved
                ? id
                : null;

        var identity = await identities.MapAsync(item, cancellationToken);

        if (remoteId is not null)
        {
            entry.ProviderKey = null;
            entry.ProviderHistoryId = null;
            entry.ProviderEntryOwned = false;
            entry.LinkStatus = PlaybackHistoryLinkStatus.None;

            await StageOutboxAsync(
                appUserId, item, row, entry: null, WatchHistoryOutboxOperation.RemoveOwnedEntries,
                identity, occurredAt: null,
                // Distinct from the add below by operation, and from a later deletion of this same entry
                // by nothing — but that deletion now finds no owned id to remove and queues nothing at
                // all, so the two cannot collide.
                discriminator: entry.Id.ToString("N"),
                cancellationToken,
                remoteIdSnapshot: JsonSerializer.Serialize(new[] { remoteId }));
        }

        await StageOutboxAsync(
            appUserId, item, row, entry, WatchHistoryOutboxOperation.AddExactWatch, identity, watchedAt,
            discriminator: entry.Id.ToString("N"), cancellationToken);
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

        // Captured before the rows go: after this transaction there is nowhere left to read them
        // from, and without them the worker would have nothing to remove — the remote mark would
        // survive an unwatch forever.
        var remoteIds = owned
            .Where(entry => entry.ProviderEntryOwned && entry.ProviderHistoryId is not null)
            .Select(entry => entry.ProviderHistoryId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (owned.Count > 0)
        {
            database.PlaybackHistoryEntries.RemoveRange(owned);
        }

        var identity = await identities.MapAsync(item, cancellationToken);
        await StageOutboxAsync(
            appUserId, item, row, entry: null, WatchHistoryOutboxOperation.RemoveOwnedTimelessEntries,
            identity, occurredAt: null, discriminator: null, cancellationToken,
            remoteIdSnapshot: remoteIds.Count > 0 ? JsonSerializer.Serialize(remoteIds) : null);
    }

    /// <summary>
    /// Deletes one recorded play, reprojects the item's aggregates from what survives, and asks the
    /// provider to remove the remote entry when — and only when — this app owns it.
    /// </summary>
    /// <remarks>
    /// This is the one path that treats a play as a mistake rather than as history. Unwatching says
    /// "not watched now" and deliberately keeps the count; deleting says "this play did not happen",
    /// so the count has to follow. The caller is responsible for having loaded <paramref name="entry"/>
    /// scoped to <paramref name="appUserId"/> — nothing below re-checks ownership of the row.
    /// </remarks>
    public async Task StageEntryDeletionAsync(
        int appUserId, MediaItem item, UserItemData? row, PlaybackHistoryEntry entry, CancellationToken cancellationToken)
    {
        // Captured before the row goes, for the same reason unwatch captures its ids: afterwards there
        // is nowhere left to read it from, and without it the remote entry would outlive the local one
        // forever. An Unresolved link is excluded — the add committed but its id was never pinned
        // down, and deleting on a guess destroys history this app did not create.
        var remoteId = entry is { ProviderEntryOwned: true, ProviderHistoryId: { } id }
            && entry.LinkStatus != PlaybackHistoryLinkStatus.Unresolved
                ? id
                : null;

        database.PlaybackHistoryEntries.Remove(entry);

        // The session gate outlives the entry — sessions are kept for 24 hours — and it decides
        // whether a crossing counts by asking whether this session already completed. Left pointing at
        // a play that no longer exists, it would answer "already counted" for the rest of the day: the
        // same client session crossing the threshold again would mark the item played, count nothing,
        // and record no entry at all. Deleting the play has to reopen the session that produced it.
        var completions = await database.PlaybackSessions
            .Where(session => session.AppUserId == appUserId && session.HistoryEntryId == entry.Id)
            .ToListAsync(cancellationToken);

        foreach (var session in completions)
        {
            session.CompletedAt = null;
            session.HistoryEntryId = null;
            // ObservedBelowThreshold is left alone: that the session once played below the threshold
            // is an observation about the session, and deleting a play does not unmake it.
        }

        var remaining = await database.PlaybackHistoryEntries
            .Where(other => other.AppUserId == appUserId
                && other.MediaItemId == item.Id
                && other.Id != entry.Id)
            .Select(other => other.WatchedAt)
            .ToListAsync(cancellationToken);

        if (row is not null)
        {
            Reproject(row, remaining);
        }

        if (remoteId is null)
        {
            // Nothing the provider can be asked to remove, so nothing is queued. Staging an empty
            // event would complete as a no-op — but until the worker got to it, it would count as
            // undelivered work and block an explicit sync for the user. Note this is the common case
            // today: only timeless marks ever have their remote id resolved, so a deleted exact play
            // survives at the provider and an explicit sync can re-import it.
            return;
        }

        var identity = await identities.MapAsync(item, cancellationToken);
        await StageOutboxAsync(
            appUserId, item, row, entry: null, WatchHistoryOutboxOperation.RemoveOwnedEntries,
            identity, occurredAt: null,
            // Keyed on the entry, not on the row's watched-state transition: deleting two plays of the
            // same item changes no state at all, so any row-derived discriminator would collide and
            // the second removal would be swallowed as a duplicate.
            discriminator: entry.Id.ToString("N"),
            cancellationToken,
            remoteIdSnapshot: JsonSerializer.Serialize(new[] { remoteId }));
    }

    /// <summary>Recomputes one item's aggregates after a play was deleted from it.</summary>
    /// <remarks>
    /// Two invariants, because the count is not a strict projection of the entry table — a mark,
    /// unwatch and re-mark legitimately leaves one entry and a count of two, and a remap merges
    /// history onto a row without recomputing it:
    /// <list type="number">
    /// <item>a deletion never <b>increases</b> the count, however far the two have drifted;</item>
    /// <item>deleting the last entry leaves a clean slate rather than a count with nothing behind it.</item>
    /// </list>
    /// Between those, one deleted play is one fewer play, floored at what is actually left.
    /// </remarks>
    private void Reproject(UserItemData row, IReadOnlyList<DateTimeOffset?> remaining)
    {
        row.PlayCount = remaining.Count == 0
            ? 0
            : Math.Min(row.PlayCount, Math.Max(remaining.Count, row.PlayCount - 1));
        row.LastWatchedAt = remaining.Count == 0 ? null : remaining.Max();

        // Cleared only when nothing is left to say it was watched. Never set: deleting a play cannot
        // make something watched, and an item deliberately unwatched must not flip back because one of
        // its surviving plays was tidied away.
        //
        // The timestamp bump is load-bearing rather than cosmetic: it is the idempotency discriminator
        // for manual marks, so without it a mark-watched after this deletion would hash to the key of
        // the one before it and be swallowed as a duplicate.
        if (remaining.Count == 0 && row.Played)
        {
            row.Played = false;
            row.WatchedStateChangedAt = time.GetUtcNow();
        }

        // PlaybackPositionTicks, LastPlayedDate and IsFavorite are untouched: a resume point is still
        // genuinely useful, and neither ordering nor favorites is a claim about this play.
    }

    private async Task StageOutboxAsync(
        int appUserId,
        MediaItem item,
        UserItemData? row,
        PlaybackHistoryEntry? entry,
        WatchHistoryOutboxOperation operation,
        WatchHistoryIdentityResult identity,
        DateTimeOffset? occurredAt,
        string? discriminator,
        CancellationToken cancellationToken,
        string? remoteIdSnapshot = null)
    {
        var connection = await database.WatchHistoryConnections
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.Status == WatchHistoryConnectionStatus.Connected, cancellationToken);

        if (connection is null)
        {
            // Nothing to deliver to. The local history above still stands, so connecting later has
            // something to export.
            return;
        }

        // A removal is addressed by the remote ids captured on the event and never reads the identity,
        // so gating it on one would drop the removal for an item that has since been re-identified or
        // lost its metadata — leaving the remote entry behind for the next sync to re-import, which is
        // exactly the play the user just deleted.
        if (!identity.Resolved && operation is not WatchHistoryOutboxOperation.RemoveOwnedEntries)
        {
            // Queueing work that can never be addressed would retry forever. The local change already
            // succeeded; the user sees the gap as a sync issue rather than a failed action.
            logger.LogInformation(
                "Not queueing {Operation} for a local item that cannot be identified ({Issue}).", operation, identity.Issue);
            return;
        }

        // What makes two enqueues "the same change" differs by operation:
        //  - a completion is identified by the playback session that produced it;
        //  - a deletion is identified by the entry it removed;
        //  - a manual mark or unwatch is identified by the watched-state transition it followed.
        // StateRevision deliberately is not used: it advances on any row touch, including re-marking
        // an already-watched item, which would queue a fresh event — and a second viewing on the
        // user's profile — for a click that changed nothing.
        // The fallback is taken from the caller's row rather than re-queried: on a first mark the row
        // is staged and unsaved, so a query would see nothing and produce a different key than the
        // repeat does.
        var key = discriminator
            ?? row?.WatchedStateChangedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)
            ?? string.Empty;

        // The discriminator is hashed rather than embedded: a session key may be up to 200 characters,
        // which together with the ids and the operation name overruns the column's 256. Silent
        // truncation would be the worst outcome here — two different changes could collide on a
        // truncated key and the second would be swallowed as a duplicate.
        var idempotencyKey = string.Join(
            ':',
            connection.Id.ToString("N"),
            item.Id.ToString("N"),
            operation.ToString(),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))));

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
            RemoteIdSnapshot = remoteIdSnapshot,
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
