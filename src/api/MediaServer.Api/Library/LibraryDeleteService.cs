using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// Deletes published library rows: a top-level movie or series, one season, one episode, or a single
/// version of an item. Two modes mirror the downloads UX: a plain remove (DB rows only — the files stay
/// and a rescan can re-publish them) and a remove that also deletes the canonical files from the catalog.
/// It never touches a download's own staging data — downloads and the library are deleted independently.
/// </summary>
public sealed class LibraryDeleteService(
    MediaServerDbContext database,
    LibraryFileEraser fileEraser)
{
    /// <summary>Returns false if no such item exists.</summary>
    public async Task<bool> DeleteAsync(Guid id, bool deleteFiles, CancellationToken cancellationToken)
    {
        // Only published top-level movies/series are deletable — never episodes/seasons or unpublished rows.
        var item = await database.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.PublicId != null &&
                candidate.ParentId == null &&
                (candidate.Kind == MediaKind.Movie || candidate.Kind == MediaKind.Series), cancellationToken);
        if (item is null)
        {
            return false;
        }

        var ids = await CollectItemIdsAsync(item, cancellationToken);

        // Capture file targets before the rows are gone (DB rows are the source of truth for paths).
        var files = deleteFiles ? await GatherLibraryFilesAsync(ids, cancellationToken) : [];

        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            await PurgeItemsAsync(ids, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var (catalog, relativePath) in files)
        {
            fileEraser.Erase(catalog, relativePath);
        }

        return true;
    }

    /// <summary>
    /// Deletes one published episode from its series, with the same two modes as the item-level delete.
    /// Emptied containers are pruned — see <see cref="DeleteWithinSeriesAsync"/>. Returns null when no
    /// such episode exists.
    /// </summary>
    public Task<ChildDeleteResult?> DeleteEpisodeAsync(Guid id, bool deleteFiles, CancellationToken cancellationToken) =>
        DeleteWithinSeriesAsync(id, MediaKind.Episode, deleteFiles, cancellationToken);

    /// <summary>
    /// Deletes one published season — its episodes, the extras parented to it, and the season row itself.
    /// Returns null when no such season exists.
    /// </summary>
    public Task<ChildDeleteResult?> DeleteSeasonAsync(Guid id, bool deleteFiles, CancellationToken cancellationToken) =>
        DeleteWithinSeriesAsync(id, MediaKind.Season, deleteFiles, cancellationToken);

    /// <summary>
    /// Deletes a published episode or season and then prunes whatever it emptied: the owning season once
    /// nothing carries its <see cref="MediaItem.SeasonId"/> any more, then the series once nothing is left
    /// under it. Emptiness counts *every* remaining child, not just episodes — a season-scoped extra keeps
    /// its season alive, and pruning around it would fail the <c>Restrict</c> self-FK on
    /// <see cref="MediaItem.ParentId"/>. This mirrors the cascade <c>RemapService.CleanupOrphanAsync</c>
    /// applies after a remap.
    /// </summary>
    private async Task<ChildDeleteResult?> DeleteWithinSeriesAsync(
        Guid id, MediaKind kind, bool deleteFiles, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.PublicId != null &&
                candidate.Kind == kind && candidate.SeriesId != null, cancellationToken);
        if (item is null)
        {
            return null;
        }

        // A season takes its own children with it (episodes and season-scoped extras alike).
        var ids = kind == MediaKind.Season
            ? await database.MediaItems.AsNoTracking()
                .Where(candidate => candidate.Id == item.Id || candidate.SeasonId == item.Id ||
                    candidate.ParentId == item.Id)
                .Select(candidate => candidate.Id)
                .Distinct()
                .ToListAsync(cancellationToken)
            : [item.Id];

        var files = deleteFiles ? await GatherLibraryFilesAsync(ids, cancellationToken) : [];

        var seasonRemoved = kind == MediaKind.Season;
        var seriesRemoved = false;

        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            await PurgeItemsAsync(ids, cancellationToken);

            if (kind == MediaKind.Episode && item.SeasonId is { } seasonId &&
                !await database.MediaItems.AnyAsync(candidate => candidate.SeasonId == seasonId, cancellationToken))
            {
                await PurgeItemsAsync([seasonId], cancellationToken);
                seasonRemoved = true;
            }

            var seriesId = item.SeriesId!.Value;
            if (!await database.MediaItems.AnyAsync(candidate => candidate.Id != seriesId &&
                (candidate.SeriesId == seriesId || candidate.ParentId == seriesId), cancellationToken))
            {
                await PurgeItemsAsync([seriesId], cancellationToken);
                seriesRemoved = true;
            }

            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var (catalog, relativePath) in files)
        {
            fileEraser.Erase(catalog, relativePath);
        }

        return new ChildDeleteResult(seasonRemoved, seriesRemoved);
    }

    /// <summary>
    /// Deletes a single <see cref="MediaSource"/> (one version of a movie) — used to drop the original after
    /// a verified transcode "replace". Removes the source + its streams (and cascades any transcode-job
    /// history that fed off it); with <paramref name="deleteFile"/> it also erases the file from disk and
    /// unlinks the originating source file. Returns false if no such source exists.
    /// </summary>
    public async Task<bool> DeleteSourceAsync(Guid sourceId, bool deleteFile, CancellationToken cancellationToken)
    {
        var source = await database.MediaSources.AsNoTracking()
            .Where(candidate => candidate.Id == sourceId)
            .Select(candidate => new { candidate.Id, candidate.Path, candidate.SourceFileId, candidate.MediaItemId })
            .FirstOrDefaultAsync(cancellationToken);
        if (source is null)
        {
            return false;
        }

        // Resolve the catalog up front (the rows are the source of truth for the path) when erasing the file.
        Catalog? catalog = deleteFile
            ? await database.MediaItems.AsNoTracking()
                .Where(item => item.Id == source.MediaItemId)
                .Join(database.Catalogs.AsNoTracking(), item => item.CatalogId, candidate => candidate.Id, (_, candidate) => candidate)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            // Unlink the originating ingest file (if any), mirroring the item-delete detach.
            if (source.SourceFileId is { } sourceFileId)
            {
                await database.SourceFiles.Where(file => file.Id == sourceFileId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.MediaItemId, (Guid?)null), cancellationToken);
            }

            await database.MediaStreams.Where(stream => stream.MediaSourceId == sourceId).ExecuteDeleteAsync(cancellationToken);
            await database.MediaSources.Where(candidate => candidate.Id == sourceId).ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        if (deleteFile && catalog is not null)
        {
            fileEraser.Erase(catalog, source.Path);
        }

        return true;
    }

    /// <summary>
    /// Deletes the given items and every dependent row, inside the caller's transaction. Ids are removed
    /// child→parent because the self-FK on <see cref="MediaItem.ParentId"/> is <c>Restrict</c>, so a set
    /// spanning generations still deletes cleanly. Rows that only ever cascade — playback sessions and
    /// history, transcode jobs — are left to the DB.
    /// </summary>
    private async Task PurgeItemsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        // Detach source files from these items (keep the download's files; just unassign them).
        await database.SourceFiles
            .Where(file => file.MediaItemId != null && ids.Contains(file.MediaItemId.Value))
            .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.MediaItemId, (Guid?)null), cancellationToken);

        // Dependents first (explicit, so we don't depend on DB cascade being enabled).
        var sourceIds = await database.MediaSources
            .Where(source => ids.Contains(source.MediaItemId))
            .Select(source => source.Id)
            .ToListAsync(cancellationToken);
        await database.MediaStreams.Where(stream => sourceIds.Contains(stream.MediaSourceId)).ExecuteDeleteAsync(cancellationToken);
        await database.MediaSources.Where(source => ids.Contains(source.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.MetadataRecords.Where(record => ids.Contains(record.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.ImageAssets.Where(image => ids.Contains(image.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.MediaItemPersons.Where(credit => ids.Contains(credit.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.UserItemData.Where(data => ids.Contains(data.MediaItemId)).ExecuteDeleteAsync(cancellationToken);

        // Items child→parent: leaves first — episodes and extras (Videos parent to their series or
        // season) — then seasons, then the root.
        await database.MediaItems.Where(media => ids.Contains(media.Id) &&
            (media.Kind == MediaKind.Episode || media.Kind == MediaKind.Video)).ExecuteDeleteAsync(cancellationToken);
        await database.MediaItems.Where(media => ids.Contains(media.Id) && media.Kind == MediaKind.Season).ExecuteDeleteAsync(cancellationToken);
        await database.MediaItems.Where(media => ids.Contains(media.Id) &&
            (media.Kind == MediaKind.Series || media.Kind == MediaKind.Movie)).ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<List<Guid>> CollectItemIdsAsync(MediaItem item, CancellationToken cancellationToken)
    {
        if (item.Kind != MediaKind.Series)
        {
            return [item.Id];
        }

        // Series → its seasons and episodes (episodes carry SeriesId; seasons are direct children).
        var ids = await database.MediaItems.AsNoTracking()
            .Where(candidate => candidate.Id == item.Id || candidate.SeriesId == item.Id || candidate.ParentId == item.Id)
            .Select(candidate => candidate.Id)
            .ToListAsync(cancellationToken);
        return ids.Distinct().ToList();
    }

    private async Task<List<(Catalog Catalog, string Path)>> GatherLibraryFilesAsync(
        IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
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

/// <summary>
/// What an episode/season delete took beyond its target. <c>SeriesRemoved</c> tells the UI the
/// series page it was called from is gone and it should navigate back to the library.
/// </summary>
public sealed record ChildDeleteResult(bool SeasonRemoved, bool SeriesRemoved);
