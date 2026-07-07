using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using MediaServer.Api.Jobs;
using MediaServer.Api.Organizer;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// Moves a published top-level item (a movie or a whole series) from one catalog into another
/// type-compatible catalog: its files are moved into the target catalog's canonical layout and every
/// durable pointer (<see cref="MediaItem"/>/<see cref="MediaSource"/>/<see cref="SourceFile"/>/
/// <see cref="IngestItem"/>) is repointed. Because <see cref="MediaItem.PublicId"/> embeds the catalog, the
/// public (Jellyfin) id is re-minted — clients re-sync — while the internal <see cref="MediaItem.Id"/> is
/// preserved, so <c>UserData</c>/metadata/credits survive a re-point.
///
/// Two cases per leaf, keyed on whether the target catalog already holds the same provider identity:
/// <list type="bullet">
/// <item><b>Re-point</b> (no collision): the existing rows are moved as-is (change <c>CatalogId</c>,
/// re-mint <c>PublicId</c>) — nothing is created, all dependent data is preserved.</item>
/// <item><b>Merge</b> (collision): the source's sources are reassigned onto the existing target leaf as
/// alternate versions (edition-labelled to keep paths distinct) and the orphaned source rows are pruned —
/// mirroring <see cref="RemapService"/>.</item>
/// </list>
/// A move within one volume is an atomic rename; across volumes it copies then deletes the source (with a
/// free-space pre-check), reporting progress on its <see cref="Job"/>. See
/// <c>docs/features/file-directory-management.md</c>.
/// </summary>
public sealed class LibraryMoveService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    IFilesystemInspector filesystem,
    JobService jobs,
    ILogger<LibraryMoveService> logger)
{
    public const string JobType = "library:move";

    // Path equality follows the filesystem: case-insensitive on Windows and default macOS, ordinal elsewhere.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// Runs a validated move. The coordinator has already confirmed the item, the target catalog, type
    /// compatibility, and (for a cross-volume move) free space; this method plans the file moves, performs
    /// the disk IO with progress, then repoints/merges the rows in a single short transaction.
    /// </summary>
    public async Task<MoveResult> MoveAsync(Guid itemId, Guid targetCatalogId, Job job, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
        {
            return MoveResult.NotFound;
        }

        if (item.PublicId is null || item.ParentId is not null || item.Kind is not (MediaKind.Movie or MediaKind.Series))
        {
            return MoveResult.Unsupported;
        }

        var source = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == item.CatalogId, cancellationToken);
        var target = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == targetCatalogId, cancellationToken);
        if (source is null || target is null)
        {
            return MoveResult.NotFound;
        }

        if (source.Id == target.Id)
        {
            return MoveResult.SameCatalog;
        }

        if (!IsTypeCompatible(item.Kind, target.Type))
        {
            return MoveResult.IncompatibleType;
        }

        var sameVolume = SameVolume(filesystem, source.Root, target.Root);

        // Plan the moves (read-only: no row mutations yet, so the progress SaveChanges below only ever
        // persists the Job — never a half-applied move).
        var plan = item.Kind == MediaKind.Movie
            ? await BuildMoviePlanAsync(item, source, target, cancellationToken)
            : await BuildSeriesPlanAsync(item, source, target, cancellationToken);
        if (plan is null)
        {
            return MoveResult.MissingFile;
        }

        // Move the bytes first, outside any transaction, so a long cross-volume copy never holds the SQLite
        // write lock. Same-volume renames are instant; cross-volume copies keep the source until commit.
        // Move the bytes, then repoint/merge the rows in one short transaction. If either the file IO or the
        // DB work fails before commit, undo the file moves so disk and database never drift apart (a
        // same-volume rename or a cross-volume copy would otherwise be left behind after a rolled-back txn).
        var crossVolumeSources = new List<string>();
        try
        {
            await ExecuteFilesAsync(plan.Moves, target, sameVolume, crossVolumeSources, job, cancellationToken);

            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await ApplyAsync(plan, source, target, cancellationToken);
            job.RelatedId = plan.ResultTopId;
            await database.SaveChangesAsync(cancellationToken);
            await PrunePlanAsync(plan, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            RollbackFiles(plan.Moves, sameVolume, cancellationToken);
            throw;
        }

        // Free the source copies (cross-volume) and any directories the moves emptied.
        foreach (var absolute in crossVolumeSources)
        {
            TryDeleteFile(absolute);
        }

        foreach (var directory in plan.Moves.Select(move => Path.GetDirectoryName(move.OldAbsolute)!).Distinct(PathComparer))
        {
            TryRemoveEmptyDirectories(directory, source.Root);
        }

        logger.LogInformation("Moved {Kind} '{Title}' ({Item}) from catalog {Source} to {Target}.",
            item.Kind, item.Title, item.Id, source.Id, target.Id);
        return MoveResult.Ok(plan.ResultTopId);
    }

    // ---- Planning (read-only) --------------------------------------------------------------------------

    private async Task<MovePlan?> BuildMoviePlanAsync(MediaItem movie, Catalog source, Catalog target, CancellationToken cancellationToken)
    {
        var mergeTarget = await database.MediaItems.FirstOrDefaultAsync(candidate =>
            candidate.CatalogId == target.Id && candidate.Kind == MediaKind.Movie && candidate.Id != movie.Id &&
            candidate.IdentityProvider == movie.IdentityProvider && candidate.IdentityProviderId == movie.IdentityProviderId,
            cancellationToken);

        var used = new HashSet<string>(PathComparer);
        if (mergeTarget is not null)
        {
            used.UnionWith(await database.MediaSources.Where(s => s.MediaItemId == mergeTarget.Id).Select(s => s.Path).ToListAsync(cancellationToken));
        }

        var leaf = mergeTarget ?? movie;
        var moves = await PlanLeafMovesAsync(movie.Id, source, target, leaf, series: leaf, isMerge: mergeTarget is not null, used, cancellationToken);
        if (moves is null)
        {
            return null;
        }

        return new MovePlan(mergeTarget is null ? movie.Id : mergeTarget.Id)
        {
            Moves = moves,
            Movie = movie,
            MovieMergeTarget = mergeTarget,
        };
    }

    private async Task<MovePlan?> BuildSeriesPlanAsync(MediaItem series, Catalog source, Catalog target, CancellationToken cancellationToken)
    {
        var targetSeries = await database.MediaItems.FirstOrDefaultAsync(candidate =>
            candidate.CatalogId == target.Id && candidate.Kind == MediaKind.Series && candidate.Id != series.Id &&
            candidate.IdentityProvider == series.IdentityProvider && candidate.IdentityProviderId == series.IdentityProviderId,
            cancellationToken);

        var episodes = await database.MediaItems
            .Where(candidate => candidate.Kind == MediaKind.Episode && candidate.SeriesId == series.Id)
            .ToListAsync(cancellationToken);
        var seasons = await database.MediaItems
            .Where(candidate => candidate.Kind == MediaKind.Season && candidate.SeriesId == series.Id)
            .ToListAsync(cancellationToken);

        var moves = new List<SourceMove>();

        if (targetSeries is null)
        {
            // Re-point the whole subtree: the series keeps its structure, only its catalog (and public ids) change.
            foreach (var episode in episodes)
            {
                var used = new HashSet<string>(PathComparer);
                var leafMoves = await PlanLeafMovesAsync(episode.Id, source, target, episode, series, isMerge: false, used, cancellationToken);
                if (leafMoves is null)
                {
                    return null;
                }

                moves.AddRange(leafMoves);
            }

            return new MovePlan(series.Id)
            {
                Moves = moves,
                SourceSeries = series,
                RepointContainers = new List<MediaItem>(seasons) { series }.Concat(episodes).ToList(),
            };
        }

        // Merge into the existing target series, per episode.
        var targetEpisodes = await database.MediaItems
            .Where(candidate => candidate.CatalogId == target.Id && candidate.Kind == MediaKind.Episode && candidate.SeriesId == targetSeries.Id)
            .ToListAsync(cancellationToken);

        var placements = new List<EpisodePlacement>();
        var mergedEpisodeIds = new List<Guid>();

        foreach (var episode in episodes)
        {
            var seasonNumber = episode.IdentitySeasonNumber ?? episode.ParentIndexNumber ?? 1;
            var episodeNumber = episode.IdentityEpisodeNumber ?? episode.IndexNumber ?? 0;
            var targetEpisode = targetEpisodes.FirstOrDefault(candidate =>
                (candidate.IdentitySeasonNumber ?? candidate.ParentIndexNumber ?? 1) == seasonNumber &&
                (candidate.IdentityEpisodeNumber ?? candidate.IndexNumber ?? 0) == episodeNumber);

            if (targetEpisode is not null)
            {
                var used = new HashSet<string>(PathComparer);
                used.UnionWith(await database.MediaSources.Where(s => s.MediaItemId == targetEpisode.Id).Select(s => s.Path).ToListAsync(cancellationToken));
                var leafMoves = await PlanLeafMovesAsync(episode.Id, source, target, targetEpisode, targetSeries, isMerge: true, used, cancellationToken);
                if (leafMoves is null)
                {
                    return null;
                }

                moves.AddRange(leafMoves);
                mergedEpisodeIds.Add(episode.Id);
            }
            else
            {
                var used = new HashSet<string>(PathComparer);
                var leafMoves = await PlanLeafMovesAsync(episode.Id, source, target, episode, targetSeries, isMerge: false, used, cancellationToken);
                if (leafMoves is null)
                {
                    return null;
                }

                moves.AddRange(leafMoves);
                placements.Add(new EpisodePlacement(episode, seasonNumber));
            }
        }

        return new MovePlan(targetSeries.Id)
        {
            Moves = moves,
            SourceSeries = series,
            TargetSeries = targetSeries,
            EpisodePlacements = placements,
            MergedEpisodeIds = mergedEpisodeIds,
            SourceSeasonIds = seasons.Select(season => season.Id).ToList(),
            SourceSeriesId = series.Id,
        };
    }

    /// <summary>
    /// Plans the file moves for one source leaf's media sources onto <paramref name="targetLeaf"/> in the
    /// target catalog, deriving a distinct canonical path per source. Returns null if a source file is
    /// missing on disk (the whole move bails before touching anything).
    /// </summary>
    private async Task<List<SourceMove>?> PlanLeafMovesAsync(
        Guid sourceLeafId, Catalog source, Catalog target, MediaItem targetLeaf, MediaItem series,
        bool isMerge, HashSet<string> used, CancellationToken cancellationToken)
    {
        var sources = await database.MediaSources
            .Where(candidate => candidate.MediaItemId == sourceLeafId)
            .OrderBy(candidate => candidate.CreatedAt).ThenBy(candidate => candidate.Id)
            .ToListAsync(cancellationToken);

        var moves = new List<SourceMove>();
        foreach (var mediaSource in sources)
        {
            if (!sandbox.TryResolve(source, mediaSource.Path, out var oldAbsolute))
            {
                logger.LogWarning("Refusing to move unresolved source path {Path}", mediaSource.Path);
                return null;
            }

            if (!File.Exists(oldAbsolute))
            {
                logger.LogWarning("Move source file missing on disk: {Path}", oldAbsolute);
                return null;
            }

            var extension = Path.GetExtension(mediaSource.Path);
            var (versionName, newRelative) = ResolveDistinctPath(
                target, targetLeaf, targetLeaf.Kind == MediaKind.Episode ? series : null,
                extension, mediaSource.VersionName, source.Name, used);

            if (!sandbox.TryResolve(target, newRelative, out var newAbsolute))
            {
                logger.LogWarning("Refusing to move outside target catalog root: {Path}", newRelative);
                return null;
            }

            used.Add(newRelative);
            moves.Add(new SourceMove(mediaSource, targetLeaf, oldAbsolute, newRelative, newAbsolute, versionName, isMerge));
        }

        return moves;
    }

    /// <summary>
    /// Picks a canonical path for a source that does not collide with <paramref name="used"/>. Keeps the
    /// source's own version label when free; on a collision (a merge onto a leaf that already has that base
    /// name) it labels the incoming file — by its own edition, else the source catalog name — appending an
    /// ordinal until the path is unique, so alternate versions never overwrite each other.
    /// </summary>
    private static (string? VersionName, string RelativePath) ResolveDistinctPath(
        Catalog target, MediaItem leaf, MediaItem? series, string extension, string? desiredVersion, string sourceCatalogName, HashSet<string> used)
    {
        var candidate = BuildPath(target, leaf, series, extension, desiredVersion);
        if (!used.Contains(candidate))
        {
            return (desiredVersion, candidate);
        }

        var seed = string.IsNullOrWhiteSpace(desiredVersion) ? sourceCatalogName : desiredVersion;
        var version = seed;
        candidate = BuildPath(target, leaf, series, extension, version);
        for (var ordinal = 2; used.Contains(candidate); ordinal++)
        {
            version = $"{seed} {ordinal}";
            candidate = BuildPath(target, leaf, series, extension, version);
        }

        return (version, candidate);
    }

    private static string BuildPath(Catalog target, MediaItem leaf, MediaItem? series, string extension, string? version) =>
        series is null
            ? LibraryNaming.ForMovie(target, leaf, extension, version)
            : LibraryNaming.ForEpisode(series, leaf, extension, version);

    // ---- File IO ---------------------------------------------------------------------------------------

    private async Task ExecuteFilesAsync(
        IReadOnlyList<SourceMove> moves, Catalog target, bool sameVolume, List<string> crossVolumeSources, Job job, CancellationToken cancellationToken)
    {
        var lastPercent = 0;
        for (var index = 0; index < moves.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var move = moves[index];

            // Case-only path change on a case-insensitive filesystem maps to the same file — never true across
            // catalog roots, but guard anyway so we never delete the file we're about to move.
            if (string.Equals(move.OldAbsolute, move.NewAbsolute, PathComparison))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(move.NewAbsolute)!);
            if (File.Exists(move.NewAbsolute))
            {
                File.Delete(move.NewAbsolute); // Idempotent re-run: replace a stale leftover at the destination.
            }

            if (sameVolume)
            {
                File.Move(move.OldAbsolute, move.NewAbsolute);
            }
            else
            {
                // Copy asynchronously so a large 4K file doesn't block a thread-pool thread and honours
                // cancellation (a stopped/cancelled job aborts mid-copy). The source is deleted only after commit.
                await using (var input = new FileStream(move.OldAbsolute, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: true))
                await using (var output = new FileStream(move.NewAbsolute, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 1 << 20, useAsync: true))
                {
                    await input.CopyToAsync(output, cancellationToken);
                }

                crossVolumeSources.Add(move.OldAbsolute);
            }

            var percent = (int)((index + 1) * 100L / moves.Count);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                await jobs.ProgressAsync(job, percent, cancellationToken);
            }
        }
    }

    /// <summary>Best-effort undo of the file moves after a mid-move failure, before any DB change is committed.</summary>
    private void RollbackFiles(IReadOnlyList<SourceMove> moves, bool sameVolume, CancellationToken cancellationToken)
    {
        foreach (var move in moves)
        {
            try
            {
                if (string.Equals(move.OldAbsolute, move.NewAbsolute, PathComparison) || !File.Exists(move.NewAbsolute))
                {
                    continue;
                }

                if (sameVolume)
                {
                    if (!File.Exists(move.OldAbsolute))
                    {
                        File.Move(move.NewAbsolute, move.OldAbsolute); // Put the renamed file back.
                    }
                }
                else
                {
                    File.Delete(move.NewAbsolute); // Drop the copy; the source is untouched.
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Failed to roll back moved file {Path}", move.NewAbsolute);
            }
        }
    }

    // ---- Applying the plan (transactional) -------------------------------------------------------------

    private async Task ApplyAsync(MovePlan plan, Catalog source, Catalog target, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // The source files behind every moving media source, plus the ingest items that own them (their
        // CatalogId must follow the move so it stays consistent with the item's new catalog).
        var sourceFileIds = plan.Moves.Where(move => move.Source.SourceFileId is not null).Select(move => move.Source.SourceFileId!.Value).ToList();
        var sourceFiles = await database.SourceFiles.Where(file => sourceFileIds.Contains(file.Id)).ToListAsync(cancellationToken);
        var sourceFilesById = sourceFiles.ToDictionary(file => file.Id);

        foreach (var move in plan.Moves)
        {
            move.Source.Path = move.NewRelative;
            move.Source.MediaItemId = move.TargetLeaf.Id;
            if (move.IsMerge)
            {
                move.Source.VersionName = move.VersionName;
            }

            if (move.Source.SourceFileId is { } fileId && sourceFilesById.TryGetValue(fileId, out var sourceFile))
            {
                sourceFile.RelativePath = move.NewRelative;
                sourceFile.MediaItemId = move.TargetLeaf.Id;
                if (move.IsMerge)
                {
                    sourceFile.Edition = move.VersionName;
                }

                sourceFile.UpdatedAt = now;
            }
        }

        // Move the owning ingest items into the target catalog and point their durable MediaItemId at the
        // target leaf: a merge deletes the source leaf, so the link would otherwise be SET NULL (orphaned).
        var ingestIds = sourceFiles.Select(file => file.IngestItemId).Distinct().ToList();
        if (ingestIds.Count > 0)
        {
            var targetLeafByIngest = sourceFiles
                .Where(file => file.MediaItemId is not null)
                .GroupBy(file => file.IngestItemId)
                .ToDictionary(group => group.Key, group => group.First().MediaItemId);
            var ingestItems = await database.IngestItems.Where(ingest => ingestIds.Contains(ingest.Id)).ToListAsync(cancellationToken);
            foreach (var ingest in ingestItems)
            {
                ingest.CatalogId = target.Id;
                if (targetLeafByIngest.TryGetValue(ingest.Id, out var leafId))
                {
                    ingest.MediaItemId = leafId;
                }
            }
        }

        if (plan.Movie is not null)
        {
            ApplyMovie(plan, target, now);
        }
        else
        {
            await ApplySeriesAsync(plan, source, target, now, cancellationToken);
        }
    }

    private static void ApplyMovie(MovePlan plan, Catalog target, DateTimeOffset now)
    {
        var movie = plan.Movie!;
        if (plan.MovieMergeTarget is null)
        {
            // Re-point: the same row moves to the target catalog with a fresh public id.
            movie.CatalogId = target.Id;
            movie.LibraryPath = plan.Moves.FirstOrDefault()?.NewRelative ?? movie.LibraryPath;
            movie.PublicId = PublicIdFactory.ForItem(movie);
            movie.UpdatedAt = now;
        }
        else
        {
            // Merge: the sources were reassigned above; the source movie row is pruned in PrunePlanAsync.
            plan.MovieMergeTarget.UpdatedAt = now;
        }
    }

    private async Task ApplySeriesAsync(MovePlan plan, Catalog source, Catalog target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (plan.TargetSeries is null)
        {
            // Re-point the whole subtree: series, seasons, and episodes all follow to the target catalog.
            foreach (var container in plan.RepointContainers)
            {
                container.CatalogId = target.Id;
                container.UpdatedAt = now;
                if (container.Kind == MediaKind.Episode)
                {
                    var primary = plan.Moves.FirstOrDefault(move => move.TargetLeaf.Id == container.Id);
                    if (primary is not null)
                    {
                        container.LibraryPath = primary.NewRelative;
                    }
                }

                container.PublicId = PublicIdFactory.ForItem(container);
            }

            return;
        }

        // Merge into an existing series: re-point the episodes it lacks under (new-or-existing) target seasons.
        var seasonCache = new Dictionary<int, MediaItem>();
        foreach (var placement in plan.EpisodePlacements)
        {
            var season = await GetOrCreateTargetSeasonAsync(plan.TargetSeries, target, placement.SeasonNumber, seasonCache, now, cancellationToken);
            var episode = placement.Episode;
            episode.CatalogId = target.Id;
            episode.ParentId = season.Id;
            episode.SeriesId = plan.TargetSeries.Id;
            episode.SeasonId = season.Id;
            episode.ParentIndexNumber = placement.SeasonNumber;
            episode.LibraryPath = plan.Moves.FirstOrDefault(move => move.TargetLeaf.Id == episode.Id)?.NewRelative ?? episode.LibraryPath;
            episode.PublicId = PublicIdFactory.ForItem(episode);
            episode.UpdatedAt = now;
        }

        plan.TargetSeries.UpdatedAt = now;
    }

    private async Task<MediaItem> GetOrCreateTargetSeasonAsync(
        MediaItem targetSeries, Catalog target, int seasonNumber, Dictionary<int, MediaItem> cache, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(seasonNumber, out var cached))
        {
            return cached;
        }

        var existing = await database.MediaItems.FirstOrDefaultAsync(candidate =>
            candidate.CatalogId == target.Id && candidate.Kind == MediaKind.Season &&
            candidate.SeriesId == targetSeries.Id && candidate.IdentitySeasonNumber == seasonNumber, cancellationToken);
        if (existing is not null)
        {
            cache[seasonNumber] = existing;
            return existing;
        }

        var season = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = target.Id,
            Kind = MediaKind.Season,
            Title = $"Season {seasonNumber}",
            ParentId = targetSeries.Id,
            SeriesId = targetSeries.Id,
            IndexNumber = seasonNumber,
            ParentIndexNumber = seasonNumber,
            IdentityProvider = targetSeries.IdentityProvider,
            IdentityProviderId = targetSeries.IdentityProviderId,
            IdentitySeasonNumber = seasonNumber,
            Providers = targetSeries.Providers is { } providers ? new Dictionary<string, string>(providers) : new(),
            AddedAt = now,
            UpdatedAt = now,
        };
        season.PublicId = PublicIdFactory.ForItem(season);
        database.MediaItems.Add(season);
        cache[seasonNumber] = season;
        return season;
    }

    // ---- Pruning (set-based, after the reassignment is persisted) --------------------------------------

    private async Task PrunePlanAsync(MovePlan plan, CancellationToken cancellationToken)
    {
        if (plan.MovieMergeTarget is not null)
        {
            await PurgeItemsAsync([plan.Movie!.Id], cancellationToken);
            return;
        }

        if (plan.TargetSeries is null)
        {
            return; // Whole-subtree re-point creates no orphans.
        }

        // Merge: episodes whose sources were reassigned, then the now-empty source seasons, then the series.
        if (plan.MergedEpisodeIds.Count > 0)
        {
            await PurgeItemsAsync(plan.MergedEpisodeIds, cancellationToken);
        }

        if (plan.SourceSeasonIds.Count > 0)
        {
            await PurgeItemsAsync(plan.SourceSeasonIds, cancellationToken);
        }

        if (plan.SourceSeriesId is { } seriesId)
        {
            await PurgeItemsAsync([seriesId], cancellationToken);
        }
    }

    /// <summary>Deletes the given items and their dependents. Caller passes a single-generation id set.</summary>
    private async Task PurgeItemsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var sourceIds = await database.MediaSources.Where(source => ids.Contains(source.MediaItemId)).Select(source => source.Id).ToListAsync(cancellationToken);
        await database.MediaStreams.Where(stream => sourceIds.Contains(stream.MediaSourceId)).ExecuteDeleteAsync(cancellationToken);
        await database.MediaSources.Where(source => ids.Contains(source.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.MetadataRecords.Where(record => ids.Contains(record.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.ImageAssets.Where(image => ids.Contains(image.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.MediaItemPersons.Where(credit => ids.Contains(credit.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.UserItemData.Where(data => ids.Contains(data.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.MediaItems.Where(item => ids.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------

    /// <summary>Movies belong in movie catalogs; series/anime share the tvshows collection type, so they interchange.</summary>
    internal static bool IsTypeCompatible(MediaKind kind, CatalogType type) => kind switch
    {
        MediaKind.Movie => type == CatalogType.Movie,
        MediaKind.Series => type is CatalogType.Series or CatalogType.Anime,
        _ => false,
    };

    /// <summary>True when both roots resolve to the same volume (a move between them is an atomic rename).</summary>
    internal static bool SameVolume(IFilesystemInspector filesystem, string sourceRoot, string targetRoot)
    {
        var a = filesystem.GetVolumeKey(sourceRoot);
        var b = filesystem.GetVolumeKey(targetRoot);
        return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, PathComparison);
    }

    private void TryDeleteFile(string absolute)
    {
        try
        {
            if (File.Exists(absolute))
            {
                File.Delete(absolute);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to delete moved-out source file {Path}", absolute);
        }
    }

    // Walk up from an emptied directory, deleting each now-empty directory until a non-empty one (or the
    // catalog root) is reached — identical to the remap cleanup so moving out leaves no empty parents behind.
    private void TryRemoveEmptyDirectories(string directory, string root)
    {
        var stop = EnsureTrailingSeparator(Path.GetFullPath(root));
        var current = Path.GetFullPath(directory);
        while (EnsureTrailingSeparator(current).StartsWith(stop, PathComparison) &&
               !EnsureTrailingSeparator(current).Equals(stop, PathComparison))
        {
            try
            {
                if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                {
                    break;
                }

                Directory.Delete(current);
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent))
                {
                    break;
                }

                current = parent;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Failed to remove emptied directory {Path}", current);
                break;
            }
        }
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    /// <summary>One planned file relocation plus the target leaf its media source is (re)assigned to.</summary>
    private sealed record SourceMove(
        MediaSource Source, MediaItem TargetLeaf, string OldAbsolute, string NewRelative, string NewAbsolute, string? VersionName, bool IsMerge);

    /// <summary>A source episode re-pointed under the merge target's season <paramref name="SeasonNumber"/>.</summary>
    private sealed record EpisodePlacement(MediaItem Episode, int SeasonNumber);

    /// <summary>The resolved plan for one top-level move: the file moves plus the structural row changes.</summary>
    private sealed class MovePlan(Guid resultTopId)
    {
        public Guid ResultTopId { get; } = resultTopId;
        public required IReadOnlyList<SourceMove> Moves { get; init; }

        // Movie move.
        public MediaItem? Movie { get; init; }
        public MediaItem? MovieMergeTarget { get; init; }

        // Series move.
        public MediaItem? SourceSeries { get; init; }
        public MediaItem? TargetSeries { get; init; }
        public IReadOnlyList<MediaItem> RepointContainers { get; init; } = [];
        public IReadOnlyList<EpisodePlacement> EpisodePlacements { get; init; } = [];
        public IReadOnlyList<Guid> MergedEpisodeIds { get; init; } = [];
        public IReadOnlyList<Guid> SourceSeasonIds { get; init; } = [];
        public Guid? SourceSeriesId { get; init; }
    }
}

/// <summary>Operator request to move a published top-level item into another catalog.</summary>
public sealed record LibraryMoveRequest(Guid TargetCatalogId);

/// <summary>Outcome of a move, mapped to HTTP by the endpoint/coordinator.</summary>
public readonly record struct MoveResult(MoveResult.Kind Status, Guid? ResultId)
{
    public enum Kind { Ok, NotFound, Unsupported, SameCatalog, IncompatibleType, CatalogOffline, InsufficientSpace, MissingFile }

    public static readonly MoveResult NotFound = new(Kind.NotFound, null);
    public static readonly MoveResult Unsupported = new(Kind.Unsupported, null);
    public static readonly MoveResult SameCatalog = new(Kind.SameCatalog, null);
    public static readonly MoveResult IncompatibleType = new(Kind.IncompatibleType, null);
    public static readonly MoveResult CatalogOffline = new(Kind.CatalogOffline, null);
    public static readonly MoveResult InsufficientSpace = new(Kind.InsufficientSpace, null);
    public static readonly MoveResult MissingFile = new(Kind.MissingFile, null);

    public static MoveResult Ok(Guid resultId) => new(Kind.Ok, resultId);
}
