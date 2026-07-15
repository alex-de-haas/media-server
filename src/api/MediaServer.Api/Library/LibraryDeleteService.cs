using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// Deletes a published top-level item (movie or series) from the library. Two modes mirror the
/// downloads UX: a plain remove (DB rows only — the files stay and a rescan can re-publish them) and a
/// remove that also deletes the hardlinked files under the catalog's <c>library/</c> subtree. It never
/// touches <c>files/</c> (the seed copy) — downloads and the library are deleted independently.
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
            await database.UserItemData.Where(data => ids.Contains(data.MediaItemId)).ExecuteDeleteAsync(cancellationToken);

            // Items child→parent: the self-FK on ParentId is Restrict, so leaves first — episodes and
            // extras (Videos parent to their series or season) — then seasons, then the root.
            await database.MediaItems.Where(media => ids.Contains(media.Id) &&
                (media.Kind == MediaKind.Episode || media.Kind == MediaKind.Video)).ExecuteDeleteAsync(cancellationToken);
            await database.MediaItems.Where(media => ids.Contains(media.Id) && media.Kind == MediaKind.Season).ExecuteDeleteAsync(cancellationToken);
            await database.MediaItems.Where(media => ids.Contains(media.Id) &&
                (media.Kind == MediaKind.Series || media.Kind == MediaKind.Movie)).ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var (catalog, relativePath) in files)
        {
            fileEraser.Erase(catalog, relativePath);
        }

        return true;
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
