using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Torrents;

/// <summary>
/// Removes a download and reconciles everything it produced, so deleting a torrent never leaves orphaned
/// rows or files (see <c>docs/planning/torrents-and-organizer.md</c> removal semantics):
/// <list type="bullet">
/// <item><b>Unfinished download</b> (still downloading / errored): the partial file is unusable, so it is
/// always deleted regardless of the requested mode, along with the in-flight ingest.</item>
/// <item><b>Completed, keep files:</b> the seed copy and any published library items stay; the download row
/// goes, and any still-in-flight ingest (which can no longer proceed without its source files) is dropped.</item>
/// <item><b>Completed, delete files:</b> a full purge — the produced library items (and now-empty
/// season/series containers), their <c>library/</c> hardlinks, the <c>files/</c> copy, the ingest, and the
/// download row.</item>
/// </list>
/// </summary>
public sealed class DownloadDeletionService(
    MediaServerDbContext database,
    ITorrentEngine engine,
    LibraryFileEraser fileEraser,
    ILogger<DownloadDeletionService> logger)
{
    public async Task<bool> DeleteAsync(Guid downloadId, bool deleteFiles, CancellationToken cancellationToken)
    {
        var download = await database.Downloads.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == downloadId, cancellationToken);
        if (download is null)
        {
            return false;
        }

        var completed = download.State is DownloadState.Completed or DownloadState.Seeding or DownloadState.StoppedSeeding;
        // An unfinished download only has a partial, unplayable file — never keep it, whatever the caller asked.
        var purgeFiles = deleteFiles || !completed;
        var infoHash = download.InfoHash;

        // Resolve on-disk targets before the rows that name them are gone.
        var leafIds = purgeFiles ? await LeafItemIdsAsync(downloadId, cancellationToken) : [];
        var libraryFiles = leafIds.Count > 0 ? await GatherLibraryFilesAsync(leafIds, cancellationToken) : [];
        var containerIds = leafIds.Count > 0 ? await CollectEmptyContainersAsync(leafIds, cancellationToken) : [];

        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            if (purgeFiles && (leafIds.Count > 0 || containerIds.Count > 0))
            {
                await PurgeMediaRowsAsync(leafIds, containerIds, cancellationToken);
            }

            // Set-based deletes (bypassing the change tracker) so the IngestItem optimistic-concurrency
            // token doesn't fight a tracked delete, and so nothing is left orphaned regardless of whether
            // SQLite foreign-key enforcement is on.
            if (purgeFiles)
            {
                // Full purge removes every ingest record for the download.
                await database.IngestItems.Where(item => item.DownloadId == downloadId).ExecuteDeleteAsync(cancellationToken);
            }
            else
            {
                // Keep-files: drop the still-in-flight items (they can't proceed without their source
                // files) but keep published (Done) history rows — just detach them from the dying download.
                await database.IngestItems
                    .Where(item => item.DownloadId == downloadId && item.Status != IngestStatus.Done)
                    .ExecuteDeleteAsync(cancellationToken);
                await database.IngestItems
                    .Where(item => item.DownloadId == downloadId && item.Status == IngestStatus.Done)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DownloadId, (Guid?)null), cancellationToken);
            }

            await database.SourceFiles.Where(file => file.DownloadId == downloadId).ExecuteDeleteAsync(cancellationToken);
            await database.Downloads.Where(item => item.Id == downloadId).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        // Filesystem side effects only after the database is consistent. The DB rows are already gone and
        // can't be retried, so a torrent-engine failure here must not surface as a request error — log it
        // and continue erasing files (the engine drops its own state on the next restart resume pass).
        try
        {
            await engine.RemoveAsync(infoHash, purgeFiles, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Engine removal for download {DownloadId} failed after its rows were deleted.", downloadId);
        }

        foreach (var (catalog, relativePath) in libraryFiles)
        {
            fileEraser.Erase(catalog, relativePath);
        }

        logger.LogInformation(
            "Removed download {DownloadId} (purgeFiles={Purge}): {Files} library file(s), {Items} media item(s) erased.",
            downloadId, purgeFiles, libraryFiles.Count, leafIds.Count + containerIds.Count);
        return true;
    }

    /// <summary>Leaf media items (movies/episodes) this download's source files were assigned to.</summary>
    private async Task<List<Guid>> LeafItemIdsAsync(Guid downloadId, CancellationToken cancellationToken) =>
        await database.SourceFiles.AsNoTracking()
            .Where(file => file.DownloadId == downloadId && file.MediaItemId != null)
            .Select(file => file.MediaItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

    /// <summary>Season/series containers that would be left childless once the leaves are deleted.</summary>
    private async Task<List<Guid>> CollectEmptyContainersAsync(List<Guid> leafIds, CancellationToken cancellationToken)
    {
        var leaves = await database.MediaItems.AsNoTracking()
            .Where(item => leafIds.Contains(item.Id))
            .ToListAsync(cancellationToken);

        var empty = new List<Guid>();

        var seasonIds = leaves.Where(leaf => leaf.SeasonId is not null).Select(leaf => leaf.SeasonId!.Value).Distinct();
        foreach (var seasonId in seasonIds)
        {
            var survivesEpisode = await database.MediaItems
                .AnyAsync(item => item.SeasonId == seasonId && !leafIds.Contains(item.Id), cancellationToken);
            if (!survivesEpisode)
            {
                empty.Add(seasonId);
            }
        }

        var seriesIds = leaves.Where(leaf => leaf.SeriesId is not null).Select(leaf => leaf.SeriesId!.Value).Distinct();
        foreach (var seriesId in seriesIds)
        {
            var survivesEpisode = await database.MediaItems
                .AnyAsync(item => item.SeriesId == seriesId && !leafIds.Contains(item.Id), cancellationToken);
            if (survivesEpisode)
            {
                continue;
            }

            // No episodes remain under this series, so the whole series goes — including any season that
            // never had episodes (and so isn't a leaf), which would otherwise be left orphaned.
            empty.Add(seriesId);
            var seasonsUnderSeries = await database.MediaItems
                .Where(item => item.ParentId == seriesId && item.Kind == MediaKind.Season)
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);
            empty.AddRange(seasonsUnderSeries);
        }

        return empty.Distinct().ToList();
    }

    private async Task PurgeMediaRowsAsync(List<Guid> leafIds, List<Guid> containerIds, CancellationToken cancellationToken)
    {
        var ids = leafIds.Concat(containerIds).Distinct().ToList();

        var sourceIds = await database.MediaSources
            .Where(source => ids.Contains(source.MediaItemId))
            .Select(source => source.Id)
            .ToListAsync(cancellationToken);
        await database.MediaStreams.Where(stream => sourceIds.Contains(stream.MediaSourceId)).ExecuteDeleteAsync(cancellationToken);
        await database.MediaSources.Where(source => ids.Contains(source.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.MetadataRecords.Where(record => ids.Contains(record.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.ImageAssets.Where(image => ids.Contains(image.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.UserItemData.Where(data => ids.Contains(data.MediaItemId)).ExecuteDeleteAsync(cancellationToken);

        // Unassign source files first so deleting the media items can't trip a foreign key.
        await database.SourceFiles
            .Where(file => file.MediaItemId != null && ids.Contains(file.MediaItemId.Value))
            .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.MediaItemId, (Guid?)null), cancellationToken);

        // Child→parent (ParentId self-FK is Restrict): episodes, then seasons, then series/movies.
        await database.MediaItems.Where(media => ids.Contains(media.Id) && media.Kind == MediaKind.Episode).ExecuteDeleteAsync(cancellationToken);
        await database.MediaItems.Where(media => ids.Contains(media.Id) && media.Kind == MediaKind.Season).ExecuteDeleteAsync(cancellationToken);
        await database.MediaItems.Where(media => ids.Contains(media.Id) &&
            (media.Kind == MediaKind.Series || media.Kind == MediaKind.Movie || media.Kind == MediaKind.Video)).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<List<(Catalog Catalog, string Path)>> GatherLibraryFilesAsync(
        List<Guid> itemIds, CancellationToken cancellationToken)
    {
        var sources = await database.MediaSources.AsNoTracking()
            .Where(source => itemIds.Contains(source.MediaItemId))
            .Join(database.MediaItems.AsNoTracking(), source => source.MediaItemId, media => media.Id,
                (source, media) => new { source.Path, media.CatalogId })
            .ToListAsync(cancellationToken);
        if (sources.Count == 0)
        {
            return [];
        }

        var catalogIds = sources.Select(source => source.CatalogId).Distinct().ToList();
        var catalogs = await database.Catalogs.AsNoTracking()
            .Where(catalog => catalogIds.Contains(catalog.Id))
            .ToDictionaryAsync(catalog => catalog.Id, cancellationToken);

        return sources
            .Where(source => catalogs.ContainsKey(source.CatalogId))
            .Select(source => (catalogs[source.CatalogId], source.Path))
            .ToList();
    }
}
