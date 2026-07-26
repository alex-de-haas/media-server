using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// Deletes published library rows: a top-level movie or series, one season, one episode, or a single
/// version of an item. Two modes mirror the downloads UX: a plain remove (DB rows only — the files stay
/// and a rescan can re-publish them) and a remove that also deletes the canonical files from the catalog.
/// It never touches a download's own staging data — downloads and the library are deleted independently.
///
/// Removal is not always erasure. An item some user has a relationship with — a favorite, watched
/// state, a resume position, or any play in the history — is kept as a <b>tombstone</b>: the row
/// survives unpublished (<see cref="MediaItem.PublicId"/> null, <see cref="MediaItem.RemovedAt"/> set)
/// with its metadata, artwork, credits, and every piece of user data, while its sources, streams, and
/// (optionally) files are removed exactly as before. Ingest later adopts the tombstone back by
/// identity, so a re-downloaded title finds its history waiting. <paramref name="deleteUserData"/>
/// forces the old full purge for users who really do want the history gone; an item nobody has touched
/// is purged either way — tombstones preserve history, they don't hoard husks.
/// </summary>
public sealed class LibraryDeleteService(
    MediaServerDbContext database,
    LibraryFileEraser fileEraser)
{
    /// <summary>Returns false if no such item exists.</summary>
    public async Task<bool> DeleteAsync(Guid id, bool deleteFiles, bool deleteUserData, CancellationToken cancellationToken)
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
            await RemoveItemsAsync(ids, deleteUserData, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var (catalog, relativePath) in files)
        {
            fileEraser.Erase(catalog, relativePath);
        }

        return true;
    }

    /// <summary>
    /// Deletes one published episode from its series, with the same modes as the item-level delete.
    /// Emptied containers are pruned — see <see cref="DeleteWithinSeriesAsync"/>. Returns null when no
    /// such episode exists.
    /// </summary>
    public Task<ChildDeleteResult?> DeleteEpisodeAsync(Guid id, bool deleteFiles, bool deleteUserData, CancellationToken cancellationToken) =>
        DeleteWithinSeriesAsync(id, MediaKind.Episode, deleteFiles, deleteUserData, cancellationToken);

    /// <summary>
    /// Deletes one published season — its episodes, the extras parented to it, and the season row itself.
    /// Returns null when no such season exists.
    /// </summary>
    public Task<ChildDeleteResult?> DeleteSeasonAsync(Guid id, bool deleteFiles, bool deleteUserData, CancellationToken cancellationToken) =>
        DeleteWithinSeriesAsync(id, MediaKind.Season, deleteFiles, deleteUserData, cancellationToken);

    /// <summary>
    /// Deletes a published episode or season and then prunes whatever it emptied: the owning season once
    /// no <b>published</b> item carries its <see cref="MediaItem.SeasonId"/> any more, then the series
    /// once nothing published is left under it. Ghost children keep a container out of the library but
    /// not out of the database — a pruned container holding tombstones becomes a tombstone itself, so
    /// the <c>Restrict</c> self-FK on <see cref="MediaItem.ParentId"/> always holds. Emptiness counts
    /// *every* remaining published child, not just episodes — a season-scoped extra keeps its season
    /// alive. This mirrors the cascade <c>RemapService.CleanupOrphanAsync</c> applies after a remap.
    /// </summary>
    private async Task<ChildDeleteResult?> DeleteWithinSeriesAsync(
        Guid id, MediaKind kind, bool deleteFiles, bool deleteUserData, CancellationToken cancellationToken)
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
            await RemoveItemsAsync(ids, deleteUserData, cancellationToken);

            if (kind == MediaKind.Episode && item.SeasonId is { } seasonId &&
                !await database.MediaItems.AnyAsync(candidate => candidate.SeasonId == seasonId &&
                    candidate.PublicId != null, cancellationToken))
            {
                await RemoveItemsAsync([seasonId], deleteUserData, cancellationToken);
                seasonRemoved = true;
            }

            var seriesId = item.SeriesId!.Value;
            if (!await database.MediaItems.AnyAsync(candidate => candidate.Id != seriesId &&
                (candidate.SeriesId == seriesId || candidate.ParentId == seriesId) &&
                candidate.PublicId != null, cancellationToken))
            {
                await RemoveItemsAsync([seriesId], deleteUserData, cancellationToken);
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
                .Join(database.Catalogs.AsNoTracking(), item => item.CatalogId, candidate => (Guid?)candidate.Id, (_, candidate) => candidate)
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
    /// Removes the given items from the library, inside the caller's transaction: every item loses its
    /// sources and streams and detaches its ingest files, then each item is either <b>tombstoned</b>
    /// (user signal exists and <paramref name="deleteUserData"/> is off) or <b>purged</b> with all its
    /// dependents. Ancestors of a tombstone inside the set are tombstoned too, and a container that
    /// still holds children outside the set (earlier ghosts) is never purged — both because the
    /// self-FK on <see cref="MediaItem.ParentId"/> is <c>Restrict</c>, and because purging a parent
    /// out from under a surviving ghost would orphan the history it exists to preserve.
    /// </summary>
    internal async Task RemoveItemsAsync(IReadOnlyList<Guid> ids, bool deleteUserData, CancellationToken cancellationToken)
    {
        var idSet = ids.ToHashSet();
        var tombstoneIds = deleteUserData
            ? new HashSet<Guid>()
            : await CollectSignalIdsAsync(ids, cancellationToken);

        var relations = await database.MediaItems.AsNoTracking()
            .Where(item => ids.Contains(item.Id))
            .Select(item => new { item.Id, item.ParentId, item.SeasonId, item.SeriesId })
            .ToListAsync(cancellationToken);

        // A ghost keeps its ancestors: walk parent links to a fixed point so a signal-bearing episode
        // tombstones its season and series even when those carry no signal of their own.
        bool grew;
        do
        {
            grew = false;
            foreach (var relation in relations)
            {
                if (!tombstoneIds.Contains(relation.Id))
                {
                    continue;
                }

                foreach (var parent in (Guid?[])[relation.ParentId, relation.SeasonId, relation.SeriesId])
                {
                    if (parent is { } parentId && idSet.Contains(parentId) && tombstoneIds.Add(parentId))
                    {
                        grew = true;
                    }
                }
            }
        } while (grew);

        var purgeIds = ids.Where(id => !tombstoneIds.Contains(id)).ToList();

        // Ghosts from earlier deletions may still hang under a container this call would purge; such a
        // container is tombstoned instead, whatever the flags — its children's history outlives it.
        if (purgeIds.Count > 0)
        {
            var blockedParents = await database.MediaItems.AsNoTracking()
                .Where(child => !ids.Contains(child.Id) &&
                    ((child.ParentId != null && purgeIds.Contains(child.ParentId.Value)) ||
                     (child.SeasonId != null && purgeIds.Contains(child.SeasonId.Value)) ||
                     (child.SeriesId != null && purgeIds.Contains(child.SeriesId.Value))))
                .Select(child => new { child.ParentId, child.SeasonId, child.SeriesId })
                .ToListAsync(cancellationToken);
            foreach (var blocked in blockedParents)
            {
                foreach (var parent in (Guid?[])[blocked.ParentId, blocked.SeasonId, blocked.SeriesId])
                {
                    if (parent is { } parentId && idSet.Contains(parentId))
                    {
                        tombstoneIds.Add(parentId);
                    }
                }
            }

            purgeIds = ids.Where(id => !tombstoneIds.Contains(id)).ToList();
        }

        // Shared teardown — every removed item loses its playable substance. Detach source files from
        // these items (keep the download's files; just unassign them), then drop sources and streams
        // (transcode jobs cascade from their source at the DB level).
        await database.SourceFiles
            .Where(file => file.MediaItemId != null && ids.Contains(file.MediaItemId.Value))
            .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.MediaItemId, (Guid?)null), cancellationToken);

        var sourceIds = await database.MediaSources
            .Where(source => ids.Contains(source.MediaItemId))
            .Select(source => source.Id)
            .ToListAsync(cancellationToken);
        await database.MediaStreams.Where(stream => sourceIds.Contains(stream.MediaSourceId)).ExecuteDeleteAsync(cancellationToken);
        await database.MediaSources.Where(source => ids.Contains(source.MediaItemId)).ExecuteDeleteAsync(cancellationToken);

        if (tombstoneIds.Count > 0)
        {
            var tombstoneList = tombstoneIds.ToList();

            // A ghost cannot be played; transient sessions would only go stale.
            await database.PlaybackSessions.Where(session => tombstoneList.Contains(session.MediaItemId))
                .ExecuteDeleteAsync(cancellationToken);

            var now = DateTimeOffset.UtcNow;
            await database.MediaItems.Where(media => tombstoneList.Contains(media.Id))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(media => media.PublicId, (string?)null)
                    .SetProperty(media => media.RemovedAt, now)
                    .SetProperty(media => media.LibraryPath, (string?)null)
                    .SetProperty(media => media.DefaultSourceId, (Guid?)null)
                    .SetProperty(media => media.UpdatedAt, now), cancellationToken);
        }

        if (purgeIds.Count > 0)
        {
            // Dependents first (explicit, so we don't depend on DB cascade being enabled), then items
            // child→parent because the self-FK on ParentId is Restrict: leaves first — episodes and
            // extras (Videos parent to their series or season) — then seasons, then the root. Playback
            // sessions and history cascade from the item rows at the DB level.
            await database.MetadataRecords.Where(record => purgeIds.Contains(record.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
            await database.ImageAssets.Where(image => purgeIds.Contains(image.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
            await database.MediaItemPersons.Where(credit => purgeIds.Contains(credit.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
            await database.UserItemData.Where(data => purgeIds.Contains(data.MediaItemId)).ExecuteDeleteAsync(cancellationToken);

            await database.MediaItems.Where(media => purgeIds.Contains(media.Id) &&
                (media.Kind == MediaKind.Episode || media.Kind == MediaKind.Video)).ExecuteDeleteAsync(cancellationToken);
            await database.MediaItems.Where(media => purgeIds.Contains(media.Id) && media.Kind == MediaKind.Season).ExecuteDeleteAsync(cancellationToken);
            await database.MediaItems.Where(media => purgeIds.Contains(media.Id) &&
                (media.Kind == MediaKind.Series || media.Kind == MediaKind.Movie)).ExecuteDeleteAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The ids among <paramref name="ids"/> some user has a relationship with: a favorite, watched
    /// state, a resume position, a play count, or at least one history entry — for <b>any</b> user.
    /// These are the items a delete tombstones rather than purges.
    /// </summary>
    internal async Task<HashSet<Guid>> CollectSignalIdsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var withUserData = await database.UserItemData.AsNoTracking()
            .Where(data => ids.Contains(data.MediaItemId) &&
                (data.IsFavorite || data.Played || data.PlaybackPositionTicks > 0 || data.PlayCount > 0))
            .Select(data => data.MediaItemId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var withHistory = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => ids.Contains(entry.MediaItemId))
            .Select(entry => entry.MediaItemId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return [.. withUserData, .. withHistory];
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

        var catalogIds = sources.Where(source => source.CatalogId != null)
            .Select(source => source.CatalogId!.Value).Distinct().ToList();
        var catalogs = await database.Catalogs.AsNoTracking()
            .Where(catalog => catalogIds.Contains(catalog.Id))
            .ToDictionaryAsync(catalog => catalog.Id, cancellationToken);

        return sources
            .Where(source => source.CatalogId is { } catalogId && catalogs.ContainsKey(catalogId))
            .Select(source => (catalogs[source.CatalogId!.Value], source.Path))
            .ToList();
    }
}

/// <summary>
/// What an episode/season delete took beyond its target. <c>SeasonRemoved</c> tells the UI the
/// season page it was called from is gone and it should navigate back to the library.
/// </summary>
public sealed record ChildDeleteResult(bool SeasonRemoved, bool SeriesRemoved);
