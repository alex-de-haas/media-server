using MediaServer.Api.Data;
using MediaServer.Api.Organizer;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Torrents;

/// <summary>
/// Tears down the transient backing of a download once its content is safely published into the
/// library: drops the torrent from the engine, reclaims the <c>files/</c> seed copy (the matching
/// <c>library/</c> hardlink keeps the bytes alive), detaches any referencing <see cref="IngestItem"/>,
/// and deletes the <see cref="Download"/> and its <see cref="SourceFile"/> rows. The published library
/// item is never touched — it is the durable record. Idempotent: a no-op once the download is gone.
///
/// This is the "download is ephemeral" half of the model: a download exists only while it is
/// transferring or seeding; the moment it stops seeding (or never seeds) after publication, its
/// bookkeeping is disposable. See <c>docs/planning/torrents-and-organizer.md</c>.
/// </summary>
public sealed class DownloadCleanupService(
    MediaServerDbContext database,
    ITorrentEngine engine,
    IOrganizer organizer,
    ILogger<DownloadCleanupService> logger)
{
    public async Task TeardownAsync(Guid downloadId, CancellationToken cancellationToken)
    {
        var download = await database.Downloads.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == downloadId, cancellationToken);
        if (download is null)
        {
            return; // Already torn down.
        }

        // Drop the engine manager. KeepAllData (not DownloadedDataOnly) because we reclaim the files/
        // copy ourselves below — a manager-independent path that still works after a restart left the
        // completed download without a manager to remove. A no-op if no manager is registered.
        try
        {
            await engine.RemoveAsync(download.InfoHash, deleteFiles: false, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Engine removal during teardown of download {DownloadId} failed.", downloadId);
        }

        // Reclaim the files/ seed copy. Must run before the SourceFile rows (which name the files) are
        // deleted; the library/ hardlink keeps the published content alive.
        await organizer.UnlinkSeedCopyAsync(download, cancellationToken);

        // Set-based deletes in one transaction (FK-safe order: detach ingest, drop source files, drop the
        // download), bypassing the change tracker so this composes with a caller that tracks the item.
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.IngestItems
            .Where(item => item.DownloadId == downloadId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DownloadId, (Guid?)null), cancellationToken);
        await database.SourceFiles.Where(file => file.DownloadId == downloadId).ExecuteDeleteAsync(cancellationToken);
        await database.Downloads.Where(item => item.Id == downloadId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Tore down download backing {DownloadId}; library content preserved.", downloadId);
    }
}
