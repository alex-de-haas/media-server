using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>
/// Deletes individual plays from one user's history.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="WatchHistoryRecorder"/>'s recording paths, and the only way a dated
/// play is ever removed on the user's say-so. Every read filters on the caller's <c>AppUserId</c>, so
/// an entry belonging to someone else is indistinguishable from one that does not exist — the same
/// boundary the rest of the watch-history surface enforces.
/// </remarks>
public sealed class WatchHistoryEntryService(MediaServerDbContext database, WatchHistoryRecorder recorder, TimeProvider time)
{
    /// <summary>Gives an undated mark the time it should always have had.</summary>
    /// <remarks>
    /// A real viewing reaches this app undated whenever it was not observed crossing the watched
    /// threshold — a client that simply marks an item played, or a server restarted mid-playback, whose
    /// progress reports never land. The play is real and only its time is missing, so this stamps the
    /// existing entry rather than recording a new one: nothing is created, nothing is destroyed, and the
    /// item's play count does not move.
    ///
    /// Narrow on purpose. Re-dating a play that already carries a time is a different claim — that the
    /// recorded time is wrong — and remains out of scope.
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

        if (entry.WatchedAt is not null)
        {
            return SetWatchedAtStatus.AlreadyDated;
        }

        entry.WatchedAt = watchedAt;

        // The aggregate row learns the time too, but only forwards: an item watched last night and
        // backfilled with a viewing from 2019 was still last watched last night. A row whose
        // LastWatchedAt is null is the common case here — a timeless mark never set one.
        var row = await database.UserItemData.FirstOrDefaultAsync(
            data => data.AppUserId == appUserId && data.MediaItemId == entry.MediaItemId, cancellationToken);
        if (row is not null && (row.LastWatchedAt is null || watchedAt > row.LastWatchedAt))
        {
            row.LastWatchedAt = watchedAt;
        }

        // The provider is told, when there is one: it holds this play as timeless, and leaving it that
        // way would have the next explicit sync import the undated mark straight back. Staged before the
        // save so the stamped entry and the outbound intent commit together.
        var item = await database.MediaItems.FirstOrDefaultAsync(
            media => media.Id == entry.MediaItemId, cancellationToken);
        if (item is not null)
        {
            await recorder.StageMarkDatedAsync(appUserId, item, row, entry, watchedAt, cancellationToken);
        }

        await database.SaveChangesAsync(cancellationToken);
        return SetWatchedAtStatus.Updated;
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

/// <summary>Why an undated mark was or was not given a time, so the endpoint can answer 204/400/404.</summary>
public enum SetWatchedAtStatus
{
    Updated,

    /// <summary>Unknown to this user — which is also the answer for someone else's entry.</summary>
    NotFound,

    /// <summary>The entry already carries a time. Correcting one is not what this does.</summary>
    AlreadyDated,

    /// <summary>An instant in the future.</summary>
    FutureInstant,
}
