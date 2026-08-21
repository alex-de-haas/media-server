using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>
/// Edits individual plays in one user's history: when each happened, and whether it happened at all.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="WatchHistoryRecorder"/>'s recording paths, and the only way a dated
/// play is ever removed on the user's say-so. Every read filters on the caller's <c>AppUserId</c>, so
/// an entry belonging to someone else is indistinguishable from one that does not exist — the same
/// boundary the rest of the watch-history surface enforces.
/// </remarks>
public sealed class WatchHistoryEntryService(MediaServerDbContext database, WatchHistoryRecorder recorder, TimeProvider time)
{
    /// <summary>Sets when a play happened: a mark that was never timed, or one timed wrongly.</summary>
    /// <remarks>
    /// Two claims with the same fix. A real viewing reaches this app undated whenever it was not
    /// observed crossing the watched threshold — a client that simply marks an item played, or a server
    /// restarted mid-playback, whose progress reports never land. A dated one carries the instant the
    /// report arrived, which is not always the instant the viewer remembers: a play left running, or a
    /// hand-logged viewing given the wrong day. Either way the play is real and only its time is at
    /// issue, so this stamps the existing entry rather than recording a new one: nothing is created,
    /// nothing is destroyed, and the item's play count does not move.
    /// </remarks>
    public async Task<SetWatchedAtStatus> SetWatchedAtAsync(
        int appUserId, Guid entryId, DateTimeOffset watchedAt, CancellationToken cancellationToken)
    {
        if (watchedAt > time.GetUtcNow() + FutureAllowance)
        {
            return SetWatchedAtStatus.FutureInstant;
        }

        var entry = await database.PlaybackHistoryEntries.FirstOrDefaultAsync(
            row => row.Id == entryId && row.AppUserId == appUserId, cancellationToken);

        if (entry is null)
        {
            return SetWatchedAtStatus.NotFound;
        }

        var previous = entry.WatchedAt;
        if (previous == watchedAt)
        {
            // Already the instant it carries. Writing it again would be harmless locally but would ask
            // the provider to retire and re-state the play for a correction nobody made.
            return SetWatchedAtStatus.Updated;
        }

        entry.WatchedAt = watchedAt;

        var row = await database.UserItemData.FirstOrDefaultAsync(
            data => data.AppUserId == appUserId && data.MediaItemId == entry.MediaItemId, cancellationToken);
        if (row is not null)
        {
            row.LastWatchedAt = await LatestWatchAsync(appUserId, entry, previous, row.LastWatchedAt, watchedAt, cancellationToken);
        }

        // The provider is told, when there is one: it holds this play at the time it had — timeless, or
        // the instant now being corrected — and leaving it that way would have the next explicit sync
        // import the stale claim straight back. Staged before the save so the stamped entry and the
        // outbound intent commit together.
        var item = await database.MediaItems.FirstOrDefaultAsync(
            media => media.Id == entry.MediaItemId, cancellationToken);
        if (item is not null)
        {
            await recorder.StageWatchedAtChangedAsync(appUserId, item, row, entry, watchedAt, cancellationToken);
        }

        await database.SaveChangesAsync(cancellationToken);
        return SetWatchedAtStatus.Updated;
    }

    /// <summary>What the item's <c>LastWatchedAt</c> becomes once one of its plays has moved in time.</summary>
    /// <remarks>
    /// Forwards-only in general: the row can hold a later viewing this table never received —
    /// pre-migration history, or a remap that merged aggregates without merging entries — so
    /// backfilling an old play must not claim the item has gone unwatched since.
    ///
    /// The exception is the row pointing at the play being moved. Then it has to follow it, backwards
    /// included, or the item would keep advertising an instant nothing was watched at. Recomputed from
    /// the plays that remain rather than simply taking the new instant, because pulling this one back
    /// can hand the title to another play.
    /// </remarks>
    private async Task<DateTimeOffset?> LatestWatchAsync(
        int appUserId,
        PlaybackHistoryEntry entry,
        DateTimeOffset? previous,
        DateTimeOffset? lastWatchedAt,
        DateTimeOffset watchedAt,
        CancellationToken cancellationToken)
    {
        if (previous is null || lastWatchedAt != previous)
        {
            return lastWatchedAt is null || watchedAt > lastWatchedAt ? watchedAt : lastWatchedAt;
        }

        // Read from the siblings, not from the whole table: this entry is tracked and unsaved, so the
        // database still answers with the instant it is being moved away from.
        var others = await database.PlaybackHistoryEntries
            .Where(other => other.AppUserId == appUserId
                && other.MediaItemId == entry.MediaItemId
                && other.Id != entry.Id
                && other.WatchedAt != null)
            .Select(other => other.WatchedAt!.Value)
            .ToListAsync(cancellationToken);

        return others.Append(watchedAt).Max();
    }

    /// <summary>How far ahead of the server's clock a supplied instant may be before it is refused.</summary>
    private static readonly TimeSpan FutureAllowance = TimeSpan.FromMinutes(5);

    /// <summary>Deletes one entry and everything projected from it.</summary>
    /// <returns>False when this user has no such entry.</returns>
    public async Task<bool> DeleteAsync(int appUserId, Guid entryId, CancellationToken cancellationToken)
    {
        var entry = await database.PlaybackHistoryEntries.FirstOrDefaultAsync(
            row => row.Id == entryId && row.AppUserId == appUserId, cancellationToken);

        if (entry is null)
        {
            return false;
        }

        var item = await database.MediaItems.FirstOrDefaultAsync(
            row => row.Id == entry.MediaItemId, cancellationToken);

        if (item is null)
        {
            // The cascade normally makes this impossible. If it happens anyway there is no identity to
            // describe to a provider and no aggregate row worth reprojecting, so drop the orphan and
            // report success — the user asked for the entry to be gone, and it is.
            database.PlaybackHistoryEntries.Remove(entry);
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }

        // Tracked, not a bulk update: SaveChanges is what bumps StateRevision, which the Jellyfin delta
        // sync and the watch-history sync's staleness check both read.
        var data = await database.UserItemData.FirstOrDefaultAsync(
            row => row.AppUserId == appUserId && row.MediaItemId == entry.MediaItemId, cancellationToken);

        // Staged, then committed here in one transaction: the deletion, the reprojected aggregates and
        // the outbound removal have to land together, or a crash between them leaves the app believing
        // it removed something remotely that it never queued.
        await recorder.StageEntryDeletionAsync(appUserId, item, data, entry, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>Why a play was or was not given its time, so the endpoint can answer 204/400/404.</summary>
public enum SetWatchedAtStatus
{
    Updated,

    /// <summary>Unknown to this user — which is also the answer for someone else's entry.</summary>
    NotFound,

    /// <summary>An instant in the future.</summary>
    FutureInstant,
}
