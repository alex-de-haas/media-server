using MediaServer.Api.Data;
using MediaServer.Api.Media;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Torrents;

/// <summary>
/// Persists the playable <see cref="SourceFile"/> rows for a download from a torrent file list.
/// Idempotent: re-running for the same download updates sizes/indexes in place rather than creating
/// duplicates, so it is safe to call again after metadata refreshes or a restart.
/// </summary>
public sealed class DownloadFileService(MediaServerDbContext database)
{
    public async Task<IReadOnlyList<SourceFile>> UpsertSourceFilesAsync(
        Guid downloadId, IReadOnlyList<TorrentFileInfo> files, CancellationToken cancellationToken)
    {
        var existing = await database.SourceFiles
            .Where(file => file.DownloadId == downloadId)
            .ToListAsync(cancellationToken);

        var byPath = existing.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var result = new List<SourceFile>();

        foreach (var file in files.Where(file => MediaFormats.IsPlayableMedia(file.RelativePath, file.Length)))
        {
            if (byPath.TryGetValue(file.RelativePath, out var current))
            {
                current.SizeBytes = file.Length;
                current.TorrentFileIndex = file.Index;
                current.UpdatedAt = now;
                result.Add(current);
            }
            else
            {
                var created = new SourceFile
                {
                    Id = Guid.NewGuid(),
                    DownloadId = downloadId,
                    RelativePath = file.RelativePath,
                    TorrentFileIndex = file.Index,
                    SizeBytes = file.Length,
                    AssignmentStatus = SourceFileAssignmentStatus.Unassigned,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                database.SourceFiles.Add(created);
                result.Add(created);
            }
        }

        await database.SaveChangesAsync(cancellationToken);
        return result;
    }
}
