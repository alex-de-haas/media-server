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
public sealed class WatchHistoryEntryService(MediaServerDbContext database, WatchHistoryRecorder recorder)
{
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
