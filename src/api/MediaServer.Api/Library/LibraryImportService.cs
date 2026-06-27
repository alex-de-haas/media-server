using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Media;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>Result of a catalog import scan.</summary>
public sealed record LibraryImportReport(int FilesScanned, int Imported, int Skipped);

/// <summary>
/// Imports media files already present in a catalog root (a hand-copied collection, or content placed out
/// of band) by creating an ingest for each orphan file at the identify stage. The normal pipeline tail
/// then runs: identify → organize (rename into canonical form) → probe → enrich → publish. Files that
/// already back a published <see cref="MediaSource"/>, or already have an in-flight ingest, are skipped
/// (idempotent). The transient <c>.incoming/</c> staging area is never scanned.
/// See <c>docs/features/torrents-and-organizer.md</c>.
/// </summary>
public sealed class LibraryImportService(
    MediaServerDbContext database, IPipelineQueue queue, ILogger<LibraryImportService> logger)
{
    // Path matching follows the filesystem: case-insensitive on Windows and default macOS, ordinal elsewhere.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Scans the catalog root and queues an ingest for each orphan media file. Null if no catalog.</summary>
    public async Task<LibraryImportReport?> ImportAsync(Guid catalogId, CancellationToken cancellationToken)
    {
        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == catalogId, cancellationToken);
        if (catalog is null)
        {
            return null;
        }

        var paths = CatalogPaths.For(catalog);
        paths.EnsureCreated();

        // Files already backing a published media source, or already owned by any ingest in this catalog,
        // are off-limits — re-scanning must not duplicate them.
        var publishedPaths = await database.MediaSources.AsNoTracking()
            .Where(source => source.MediaItem!.CatalogId == catalogId)
            .Select(source => source.Path)
            .ToListAsync(cancellationToken);
        var queuedPaths = await database.SourceFiles.AsNoTracking()
            .Where(file => file.IngestItem!.CatalogId == catalogId)
            .Select(file => file.RelativePath)
            .ToListAsync(cancellationToken);
        // Transcode-engine outputs ("Movie - HEVC.mkv", etc.) are derived alternate versions managed by the
        // transcode importer, not primary library files. They may sit on disk without a current MediaSource —
        // e.g. after a version was removed but its file kept. Re-ingesting one identifies it as the same movie
        // and the organizer renames it onto the original's canonical path, destroying the original. Keep every
        // known transcode output off-limits so a scan never re-ingests it.
        var transcodeOutputs = await database.TranscodeJobs.AsNoTracking()
            .Where(job => job.CatalogId == catalogId)
            .Select(job => job.OutputPath)
            .ToListAsync(cancellationToken);
        var known = new HashSet<string>(publishedPaths.Concat(queuedPaths).Concat(transcodeOutputs), PathComparer);

        var scanned = 0;
        var skipped = 0;
        var now = DateTimeOffset.UtcNow;
        var newIngestIds = new List<Guid>();

        foreach (var absolute in EnumerateMediaFiles(paths))
        {
            long size;
            try
            {
                size = new FileInfo(absolute).Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A file that moved/was deleted/became unreadable mid-scan shouldn't fail the whole import.
                logger.LogWarning(exception, "Skipping unreadable file during import scan: {Path}", absolute);
                continue;
            }

            var relative = ToRelative(paths.Root, absolute);

            // Skip non-playable junk (samples) the same way the torrent path does.
            if (!MediaFormats.IsPlayableMedia(relative, size))
            {
                continue;
            }

            scanned++;

            if (!known.Add(relative))
            {
                skipped++;
                continue;
            }

            var ingestId = Guid.NewGuid();
            database.IngestItems.Add(new IngestItem
            {
                Id = ingestId,
                CatalogId = catalogId,
                DownloadId = null,
                Stage = IngestStage.Identify,
                Status = IngestStatus.Pending,
                StagesCompleted = new List<string> { "intake", "download" },
                CreatedAt = now,
                UpdatedAt = now,
            });
            database.SourceFiles.Add(new SourceFile
            {
                Id = Guid.NewGuid(),
                IngestItemId = ingestId,
                DownloadId = null,
                RelativePath = relative,
                SizeBytes = size,
                AssignmentStatus = SourceFileAssignmentStatus.Unassigned,
                CreatedAt = now,
                UpdatedAt = now,
            });
            newIngestIds.Add(ingestId);
        }

        if (newIngestIds.Count > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
            foreach (var id in newIngestIds)
            {
                queue.Enqueue(id);
            }
        }

        logger.LogInformation("Catalog {Catalog} import scan: {Scanned} media file(s), {Imported} queued, {Skipped} already known.",
            catalogId, scanned, newIngestIds.Count, skipped);
        return new LibraryImportReport(scanned, newIngestIds.Count, skipped);
    }

    private static IEnumerable<string> EnumerateMediaFiles(CatalogPaths paths)
    {
        if (!Directory.Exists(paths.Root))
        {
            return [];
        }

        var incoming = Path.GetFullPath(paths.IncomingDir) + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(paths.Root, "*", SearchOption.AllDirectories)
            .Where(MediaFormats.IsVideo)
            .Where(file => !Path.GetFullPath(file).StartsWith(incoming, PathComparison));
    }

    private static string ToRelative(string root, string absolute) =>
        Path.GetRelativePath(root, absolute).Replace(Path.DirectorySeparatorChar, '/');
}
