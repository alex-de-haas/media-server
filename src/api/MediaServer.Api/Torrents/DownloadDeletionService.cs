using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Torrents;

/// <summary>
/// Removes an in-flight download — one still in its download/seeding stage, before the download→identify
/// hand-off. It stops and drops the torrent, deletes the staging data under <c>.incoming/</c>, and removes
/// the download's in-flight ingest(s) and source files. A published item has no download (the hand-off
/// dropped it), so its removal goes through the library, not here — there are no library files to
/// reconcile. See <c>docs/features/torrents-and-organizer.md</c>.
/// </summary>
public sealed class DownloadDeletionService(
    MediaServerDbContext database,
    ITorrentEngine engine,
    ILogger<DownloadDeletionService> logger)
{
    public async Task<bool> DeleteAsync(Guid downloadId, bool deleteFiles, CancellationToken cancellationToken)
    {
        var download = await database.Downloads.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == downloadId, cancellationToken);
        if (download is null)
        {
            return false;
        }

        // deleteFiles is moot for an in-flight download: the only bytes are the unusable partial/seed copy
        // under .incoming/, which is always removed. (Published items are governed by library removal.)
        _ = deleteFiles;

        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            // Source files cascade from the ingest, but delete explicitly so nothing is orphaned even if
            // SQLite foreign-key enforcement is off.
            await database.SourceFiles.Where(file => file.DownloadId == downloadId).ExecuteDeleteAsync(cancellationToken);
            await database.IngestItems.Where(item => item.DownloadId == downloadId).ExecuteDeleteAsync(cancellationToken);
            await database.Downloads.Where(item => item.Id == downloadId).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Filesystem + engine teardown after the database is consistent. A failure here must not surface as
        // a request error — log and continue (the engine drops its own state on the next restart pass).
        try
        {
            await engine.RemoveAsync(download.InfoHash, deleteFiles: true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Engine removal for download {DownloadId} failed after its rows were deleted.", downloadId);
        }

        TryDeleteStaging(download.SavePath);

        logger.LogInformation("Removed in-flight download {DownloadId}.", downloadId);
        return true;
    }

    private void TryDeleteStaging(string savePath)
    {
        try
        {
            if (Directory.Exists(savePath))
            {
                Directory.Delete(savePath, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to remove staging folder {Path}", savePath);
        }
    }
}
