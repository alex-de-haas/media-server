using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Torrents;

/// <summary>
/// Persists the playable <see cref="SourceFile"/> rows for a download from a torrent file list. Each file
/// is owned by the download's ingest item and its path is recorded relative to the catalog root under the
/// download's <c>.incoming/&lt;downloadId&gt;/</c> staging folder. Idempotent: re-running updates sizes/
/// indexes in place rather than creating duplicates, so it is safe to call again after metadata refreshes
/// or a restart.
/// </summary>
public sealed class DownloadFileService(MediaServerDbContext database)
{
    public async Task<IReadOnlyList<SourceFile>> UpsertSourceFilesAsync(
        Guid downloadId, IReadOnlyList<TorrentFileInfo> files, CancellationToken cancellationToken)
    {
        var ingestItemId = await database.IngestItems
            .Where(item => item.DownloadId == downloadId)
            .Select(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (ingestItemId == Guid.Empty)
        {
            return []; // No ingest yet for this download (e.g. a rolled-back add).
        }

        var incomingPrefix = CatalogPaths.IncomingRelative(downloadId);

        // De-duplicate the incoming list by relative path defensively (an engine file list shouldn't
        // repeat a path, but the upsert must never create two rows for one file). External audio tracks
        // ride along with the videos: Identify matches them to their episode and the mux stage merges
        // them into the video file before Organize.
        var playable = files
            .Where(file => MediaFormats.IsPlayableMedia(file.RelativePath, file.Length) || MediaFormats.IsCompanionAudio(file.RelativePath))
            .GroupBy(file => file.RelativePath, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var existing = await database.SourceFiles
            .Where(file => file.IngestItemId == ingestItemId)
            .ToListAsync(cancellationToken);

        var byPath = existing.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var result = new List<SourceFile>();

        foreach (var file in playable)
        {
            // The file's current location relative to the catalog root: under .incoming/<downloadId>/.
            var relativePath = $"{incomingPrefix}/{file.RelativePath}";
            if (byPath.TryGetValue(relativePath, out var current))
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
                    IngestItemId = ingestItemId,
                    DownloadId = downloadId,
                    RelativePath = relativePath,
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

        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateException)
        {
            // A concurrent coordinator handler (metadata vs. completion) inserted the same
            // (IngestItemId, RelativePath) first and the unique index rejected our insert. Drop *all* of our
            // tracked SourceFile changes — pending inserts and in-place updates alike — so a later
            // SaveChanges by the caller can't replay them, then return the rows that won the race.
            foreach (var entry in database.ChangeTracker.Entries<SourceFile>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            return await database.SourceFiles
                .AsNoTracking()
                .Where(file => file.IngestItemId == ingestItemId)
                .ToListAsync(cancellationToken);
        }
    }
}
