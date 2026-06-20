using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using MediaServer.Api.Metadata;
using MediaServer.Api.Organizer;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// Corrects a misidentified, already-published leaf item (movie or episode). The operator picks the right
/// metadata identity; we reassign the item's <see cref="MediaSource"/>(s) to the resolved target item and
/// rebuild the clean <c>library/</c> hardlink to match the target's naming — then prune the now-orphaned
/// old item (and any emptied season/series).
///
/// Because a download is ephemeral (its <c>files/</c> seed copy and <see cref="SourceFile"/> rows are
/// reclaimed once published), the surviving <c>library/</c> hardlink is the only link to the inode, so the
/// new link is created <b>from the existing library file</b> rather than from <c>files/</c>. No raw
/// rename — a fresh hardlink plus a delete of the old one. See <c>docs/planning/domain-model.md</c>.
/// </summary>
public sealed class RemapService(
    MediaServerDbContext database,
    IdentifyService identifyService,
    EnrichService enrichService,
    ICatalogPathSandbox sandbox,
    IHardLinker hardLinker,
    LibraryFileEraser fileEraser,
    ILogger<RemapService> logger)
{
    public async Task<RemapResult> RemapAsync(Guid itemId, RemapRequest request, CancellationToken cancellationToken)
    {
        var current = await database.MediaItems.FirstOrDefaultAsync(item => item.Id == itemId, cancellationToken);
        if (current is null)
        {
            return RemapResult.NotFound;
        }

        // Only a playable leaf can be remapped — never a series/season container.
        if (current.Kind is not (MediaKind.Movie or MediaKind.Episode or MediaKind.Video))
        {
            return RemapResult.Unsupported;
        }

        var catalog = await database.Catalogs.FirstOrDefaultAsync(item => item.Id == current.CatalogId, cancellationToken);
        if (catalog is null)
        {
            return RemapResult.NotFound;
        }

        // The library files to relink. Without a source there is nothing on disk to point at the new item.
        var sources = await database.MediaSources.Where(source => source.MediaItemId == current.Id).ToListAsync(cancellationToken);
        if (sources.Count == 0)
        {
            return RemapResult.NoSource;
        }

        // Pre-flight before resolving a target: every source file must resolve inside the catalog and exist
        // on disk, so a broken remap bails without creating a stray target item.
        foreach (var source in sources)
        {
            if (!sandbox.TryResolve(catalog, source.Path, out var existing))
            {
                logger.LogWarning("Refusing to remap unresolved source path {Path}", source.Path);
                return RemapResult.NoSource;
            }

            if (!File.Exists(existing))
            {
                logger.LogWarning("Remap source file missing on disk: {Path}", existing);
                return RemapResult.MissingFile;
            }
        }

        var candidate = new MetadataCandidate(new ProviderRef(request.Provider, request.ProviderId), request.Title, request.Year, 1.0);
        var target = request.Kind == MediaKind.Episode
            ? await identifyService.ResolveEpisodeAsync(catalog, candidate,
                new ParsedName(MediaKind.Episode, request.Title, request.Year, request.Season, request.Episode, null), cancellationToken)
            : await identifyService.ResolveMovieAsync(catalog, candidate, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);

        if (target.Id == current.Id)
        {
            // The operator re-picked the identity the item already has — nothing to move.
            return RemapResult.Ok(current.Id);
        }

        var targetSeries = target.Kind == MediaKind.Episode && target.SeriesId is { } seriesId
            ? await database.MediaItems.FirstOrDefaultAsync(item => item.Id == seriesId, cancellationToken)
            : null;

        // 1. Relink every source file to the target's clean path (new link first so content is never
        //    momentarily unlinked), reassigning the MediaSource rows.
        var oldFiles = new List<string>();
        string? targetLibraryPath = null;
        foreach (var source in sources)
        {
            var extension = Path.GetExtension(source.Path);
            var newRelative = target.Kind == MediaKind.Episode
                ? LibraryNaming.ForEpisode(targetSeries ?? target, target, extension)
                : LibraryNaming.ForMovie(catalog, target, extension);

            if (!sandbox.TryResolve(catalog, source.Path, out var oldAbsolute) ||
                !sandbox.TryResolve(catalog, newRelative, out var newAbsolute))
            {
                logger.LogWarning("Refusing to remap outside catalog root: {Old} → {New}", source.Path, newRelative);
                return RemapResult.NoSource;
            }

            if (!string.Equals(source.Path, newRelative, StringComparison.Ordinal))
            {
                if (!File.Exists(oldAbsolute))
                {
                    logger.LogWarning("Remap source file missing on disk: {Path}", oldAbsolute);
                    return RemapResult.MissingFile;
                }

                if (File.Exists(newAbsolute))
                {
                    File.Delete(newAbsolute); // Idempotent: replace any stale link at the destination.
                }

                hardLinker.Create(oldAbsolute, newAbsolute);
                oldFiles.Add(source.Path);
            }

            source.MediaItemId = target.Id;
            source.Path = newRelative;
            targetLibraryPath = newRelative;
        }

        target.LibraryPath = targetLibraryPath ?? target.LibraryPath;
        target.UpdatedAt = DateTimeOffset.UtcNow;

        // 2. Enrich the target so the corrected title carries real metadata/images (movie → itself;
        //    episode → its series), then mint the stable public id for the target and its containers.
        var enrichTarget = target.Kind == MediaKind.Episode ? targetSeries : target;
        if (enrichTarget is not null)
        {
            await enrichService.EnrichAsync(catalog, enrichTarget, cancellationToken);
        }

        AssignPublicId(target);
        if (targetSeries is not null)
        {
            AssignPublicId(targetSeries);
            if (target.SeasonId is { } targetSeasonId &&
                await database.MediaItems.FirstOrDefaultAsync(item => item.Id == targetSeasonId, cancellationToken) is { } season)
            {
                AssignPublicId(season);
            }
        }

        await database.SaveChangesAsync(cancellationToken);

        // 3. Prune the orphaned old item (and any season/series it emptied). Runs after the reassignment
        //    is persisted so the set-based deletes don't catch the moved sources.
        await CleanupOrphanAsync(current, cancellationToken);

        // 4. Erase the old library files last — the content is already linked under the new path.
        foreach (var relative in oldFiles)
        {
            fileEraser.Erase(catalog, relative);
        }

        logger.LogInformation("Remapped {Old} → {Kind} '{Title}' ({Provider}:{Id}).",
            current.Id, target.Kind, target.Title, target.IdentityProvider, target.IdentityProviderId);
        return RemapResult.Ok(target.Id);
    }

    private static void AssignPublicId(MediaItem item) => item.PublicId ??= PublicIdFactory.ForItem(item);

    /// <summary>Removes the old leaf if it has no sources left, then cascades to an emptied season/series.</summary>
    private async Task CleanupOrphanAsync(MediaItem old, CancellationToken cancellationToken)
    {
        var remaining = await database.MediaSources.CountAsync(source => source.MediaItemId == old.Id, cancellationToken);
        if (remaining > 0)
        {
            return; // Defensive: another source still backs it (e.g. a multi-file item only partly moved).
        }

        var seasonId = old.SeasonId;
        var seriesId = old.SeriesId;

        await PurgeItemsAsync([old.Id], cancellationToken);

        if (seasonId is { } season &&
            await database.MediaItems.CountAsync(item => item.SeasonId == season, cancellationToken) == 0)
        {
            await PurgeItemsAsync([season], cancellationToken);
        }

        if (seriesId is { } series &&
            await database.MediaItems.CountAsync(item => item.Id != series && (item.SeriesId == series || item.ParentId == series), cancellationToken) == 0)
        {
            await PurgeItemsAsync([series], cancellationToken);
        }
    }

    /// <summary>Deletes the given items and their dependents. Caller passes a single-generation id set.</summary>
    private async Task PurgeItemsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        var sourceIds = await database.MediaSources
            .Where(source => ids.Contains(source.MediaItemId))
            .Select(source => source.Id)
            .ToListAsync(cancellationToken);

        await database.MediaStreams.Where(stream => sourceIds.Contains(stream.MediaSourceId)).ExecuteDeleteAsync(cancellationToken);
        await database.MediaSources.Where(source => ids.Contains(source.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.MetadataRecords.Where(record => ids.Contains(record.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.ImageAssets.Where(image => ids.Contains(image.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.UserItemData.Where(data => ids.Contains(data.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
        await database.MediaItems.Where(item => ids.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>Operator remap of a published leaf to a corrected metadata identity (movie or episode).</summary>
public sealed record RemapRequest(
    MediaKind Kind,
    string Provider,
    string ProviderId,
    string Title,
    int? Year,
    int? Season,
    int? Episode);

/// <summary>Outcome of a remap, mapped to HTTP by the endpoint.</summary>
public readonly record struct RemapResult(RemapResult.Kind Status, Guid? TargetId)
{
    public enum Kind { Ok, NotFound, Unsupported, NoSource, MissingFile }

    public static readonly RemapResult NotFound = new(Kind.NotFound, null);
    public static readonly RemapResult Unsupported = new(Kind.Unsupported, null);
    public static readonly RemapResult NoSource = new(Kind.NoSource, null);
    public static readonly RemapResult MissingFile = new(Kind.MissingFile, null);

    public static RemapResult Ok(Guid targetId) => new(Kind.Ok, targetId);
}
