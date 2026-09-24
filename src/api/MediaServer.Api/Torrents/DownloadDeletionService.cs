using MediaServer.Api.Data;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Torrents;

/// <summary>Cancels unpublished imports only after acknowledged engine release and owned cleanup.</summary>
public sealed class DownloadDeletionService(MediaServerDbContext database, DownloadRetentionService retention)
{
    public async Task<bool> DeleteAsync(Guid downloadId, bool deleteFiles, CancellationToken cancellationToken)
    {
        using var gate = await IngestMutationGate.EnterAsync(cancellationToken);
        return await DeleteUnderLockAsync(downloadId, cancellationToken);
    }

    internal async Task<bool> DeleteUnderLockAsync(Guid downloadId, CancellationToken ct)
    {
        var download = await database.Downloads.FirstOrDefaultAsync(x => x.Id == downloadId, ct);
        if (download is null) return false;
        var ingests = await database.IngestItems.Where(x => x.DownloadId == downloadId).ToListAsync(ct);
        if (ingests.Any(x => x.Status == IngestStatus.Done))
            throw new TorrentRequestException("This item is in the library. Stop seeding and remove originals before clearing its history.");
        if (!await retention.CleanupAsync(download, true, ct))
            throw new TorrentRequestException(download.RetentionError ?? "Cleanup is pending; original files are retained.");

        return true;
    }
}
