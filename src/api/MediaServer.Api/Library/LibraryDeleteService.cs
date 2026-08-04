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
    /// Permanently purges a tombstoned top-level title and its ghost subtree — the retroactive full
    /// purge offered by the removed-titles surface. Returns false when no such tombstone exists.
    /// </summary>
    public async Task<bool> PurgeRemovedAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.RemovedAt != null &&
                (candidate.Kind == MediaKind.Movie || candidate.Kind == MediaKind.Series), cancellationToken);
        if (item is null)
        {
            return false;
        }

        var ids = await CollectItemIdsAsync(item, cancellationToken);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await RemoveItemsAsync(ids, deleteUserData: true, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
    /// Deletes one external stream — a sidecar dub or subtitle sitting beside a library file. Its own
    /// operation rather than a call into <see cref="DeleteSourceAsync"/>: a sidecar is a
    /// <see cref="MediaStream"/> on a source, not a source of its own, so there is no version to drop. The
    /// affordance is presented like deleting an unwanted version, and it makes the same explicit choice —
    /// with <paramref name="deleteFile"/> the file is erased through the same eraser, without it only the
    /// entry goes and the file stays on disk. A merge never calls this: folding a track into a video leaves
    /// its sidecar alone, and removing one is always deliberate.
    /// </summary>
    public async Task<bool> DeleteExternalStreamAsync(Guid streamId, bool deleteFile, CancellationToken cancellationToken)
    {
        var stream = await database.MediaStreams.AsNoTracking()
            .Where(candidate => candidate.Id == streamId && candidate.IsExternal)
            .Select(candidate => new { candidate.Id, candidate.ExternalPath, candidate.MediaSourceId })
            .FirstOrDefaultAsync(cancellationToken);
        if (stream is null)
        {
            return false;
        }

        var owner = await database.MediaSources.AsNoTracking()
            .Where(source => source.Id == stream.MediaSourceId)
            .Join(database.MediaItems.AsNoTracking(), source => source.MediaItemId, item => item.Id, (source, item) => new
            {
                source.MediaItemId,
                item.CatalogId,
            })
            .FirstOrDefaultAsync(cancellationToken);

        var catalog = owner?.CatalogId is { } catalogId
            ? await database.Catalogs.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == catalogId, cancellationToken)
            : null;

        await database.MediaStreams.Where(candidate => candidate.Id == streamId).ExecuteDeleteAsync(cancellationToken);

        // The staged file row, if the ingest that placed it is still around, goes back to being unassigned
        // rather than pointing at something that is no longer part of the library. Scoped to the media item
        // this sidecar belonged to: a relative path is only unique within its catalog, so matching on the
        // path alone would detach an identically-placed sidecar of another catalog whose file is still there.
        if (stream.ExternalPath is { Length: > 0 } path)
        {
            if (owner is not null)
            {
                await database.SourceFiles
                    .Where(file => file.RelativePath == path && file.MediaItemId == owner.MediaItemId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(file => file.MediaItemId, (Guid?)null)
                        .SetProperty(file => file.AssignmentStatus, SourceFileAssignmentStatus.Unassigned), cancellationToken);
            }

            if (deleteFile && catalog is not null)
            {
                fileEraser.Erase(catalog, path);
            }
        }

        return true;
    }

    /// <summary>
    /// Deletes a single <see cref="MediaSource"/> (one version of a movie) — used to drop the original after
    /// a verified transcode "replace". Removes the source + its streams (and cascades any transcode-job
    /// history that fed off it); with <paramref name="deleteFile"/> it also erases the file from disk and
    /// unlinks the originating source file. The sidecars beside that file go with it: they hang off this
    /// source and nothing refers to them once it is gone. Returns false if no such source exists.
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

        // Resolve the catalog and the sidecar paths up front (the rows are the source of truth for both)
        // when erasing the file — by the time the erase runs, the rows holding them are gone.
        Catalog? catalog = deleteFile
            ? await database.MediaItems.AsNoTracking()
                .Where(item => item.Id == source.MediaItemId)
                .Join(database.Catalogs.AsNoTracking(), item => item.CatalogId, candidate => (Guid?)candidate.Id, (_, candidate) => candidate)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        var sidecarPaths = await database.MediaStreams.AsNoTracking()
            .Where(stream => stream.MediaSourceId == sourceId && stream.IsExternal && stream.ExternalPath != null)
            .Select(stream => stream.ExternalPath!)
            .ToListAsync(cancellationToken);

        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            // Unlink the originating ingest file (if any), mirroring the item-delete detach.
            if (source.SourceFileId is { } sourceFileId)
            {
                await database.SourceFiles.Where(file => file.Id == sourceFileId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.MediaItemId, (Guid?)null), cancellationToken);
            }

            // The sidecars' staged rows too — same reasoning as a single sidecar's removal, scoped to this
            // item so an identically-placed sidecar of another catalog keeps its assignment.
            if (sidecarPaths.Count > 0)
            {
                await database.SourceFiles
                    .Where(file => file.MediaItemId == source.MediaItemId && sidecarPaths.Contains(file.RelativePath))
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(file => file.MediaItemId, (Guid?)null)
                        .SetProperty(file => file.AssignmentStatus, SourceFileAssignmentStatus.Unassigned), cancellationToken);
            }

            await database.MediaStreams.Where(stream => stream.MediaSourceId == sourceId).ExecuteDeleteAsync(cancellationToken);
            await database.MediaSources.Where(candidate => candidate.Id == sourceId).ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        if (deleteFile && catalog is not null)
        {
            fileEraser.Erase(catalog, source.Path);
            foreach (var path in sidecarPaths)
            {
                fileEraser.Erase(catalog, path);
            }
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

            // A purge unlinks tracked titles through the FK's SetNull; a tombstone keeps the row, so the
            // wishlist would keep reading "in library" — unlink by hand, mirroring what the FK would do.
            await database.TrackedTitles
                .Where(title => title.MediaItemId != null && tombstoneList.Contains(title.MediaItemId.Value))
                .ExecuteUpdateAsync(setters => setters.SetProperty(title => title.MediaItemId, (Guid?)null), cancellationToken);

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

        // Everything above runs through ExecuteDelete/ExecuteUpdate, which bypass the change tracker,
        // so the DbContext's own change-log hook never sees any of it. A purge is precisely the case a
        // native client cannot discover any other way — the row is gone and, unlike a tombstone, leaves
        // nothing behind to poll — so the notifications are appended here by hand, inside the caller's
        // transaction. See docs/features/native-client-api/plan.md.
        await AppendChangeLogAsync(tombstoneIds, ChangeKind.Upsert, cancellationToken);
        await AppendChangeLogAsync(purgeIds, ChangeKind.Delete, cancellationToken);
    }

    private async Task AppendChangeLogAsync(
        IReadOnlyCollection<Guid> mediaItemIds, ChangeKind kind, CancellationToken cancellationToken)
    {
        if (mediaItemIds.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        database.ChangeLog.AddRange(mediaItemIds.Select(id => new ChangeLogEntry
        {
            EntityType = ChangeEntityType.MediaItem,
            EntityId = id.ToString("N"),
            Kind = kind,
            OccurredAt = now,
        }));
        await database.SaveChangesAsync(cancellationToken);
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

    /// <summary>
    /// Every file these items own: each source's video, plus the sidecars sitting beside it. A sidecar is a
    /// file of its own but not a file of its own item — it exists only as an external stream of a source, so
    /// once that source is gone nothing in the library refers to it any more. Leaving it behind would strand
    /// a dub next to a deleted movie, in a folder that then cannot be pruned either.
    /// </summary>
    private async Task<List<(Catalog Catalog, string Path)>> GatherLibraryFilesAsync(
        IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
    {
        var sources = await database.MediaSources.AsNoTracking()
            .Where(source => itemIds.Contains(source.MediaItemId))
            .Join(database.MediaItems.AsNoTracking(), source => source.MediaItemId, media => media.Id,
                (source, media) => new { source.Id, source.Path, media.CatalogId })
            .ToListAsync(cancellationToken);
        if (sources.Count == 0)
        {
            return [];
        }

        var sourceIds = sources.Select(source => source.Id).ToList();
        var sidecars = await database.MediaStreams.AsNoTracking()
            .Where(stream => sourceIds.Contains(stream.MediaSourceId) && stream.IsExternal && stream.ExternalPath != null)
            .Select(stream => new { stream.MediaSourceId, stream.ExternalPath })
            .ToListAsync(cancellationToken);
        var byCatalog = sources.ToDictionary(source => source.Id, source => source.CatalogId);

        var catalogIds = sources.Where(source => source.CatalogId != null)
            .Select(source => source.CatalogId!.Value).Distinct().ToList();
        var catalogs = await database.Catalogs.AsNoTracking()
            .Where(catalog => catalogIds.Contains(catalog.Id))
            .ToDictionaryAsync(catalog => catalog.Id, cancellationToken);

        // Videos first, then the sidecars beside them, so the eraser's empty-parent prune sees the folder
        // once everything in it is gone rather than after the first file.
        var files = sources
            .Where(source => source.CatalogId is { } catalogId && catalogs.ContainsKey(catalogId))
            .Select(source => (catalogs[source.CatalogId!.Value], source.Path))
            .ToList();
        files.AddRange(sidecars
            .Where(sidecar => byCatalog.GetValueOrDefault(sidecar.MediaSourceId) is { } catalogId &&
                catalogs.ContainsKey(catalogId))
            .Select(sidecar => (catalogs[byCatalog[sidecar.MediaSourceId]!.Value], sidecar.ExternalPath!)));
        return files;
    }
}

/// <summary>
/// What an episode/season delete took beyond its target (removed = left the library, whether purged
/// or tombstoned). <c>SeriesRemoved</c> tells the UI the series page it was called from is gone and
/// it should navigate back to the library; <c>SeasonRemoved</c> only reports that the owning season
/// was pruned along the way.
/// </summary>
public sealed record ChildDeleteResult(bool SeasonRemoved, bool SeriesRemoved);
