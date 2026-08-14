using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Organizer;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// Corrects a misidentified, already-published leaf item (movie or episode). The operator picks the right
/// metadata identity; we reassign the item's <see cref="MediaSource"/>(s) to the resolved target item and
/// <b>move</b> the canonical file to match the target's naming — then prune the now-orphaned old item
/// (and any emptied season/series). A move within the catalog root is atomic and zero-copy; there are no
/// hardlinks. See <c>docs/features/torrents-and-organizer/feature.md</c>.
/// </summary>
public sealed class RemapService(
    MediaServerDbContext database,
    IdentifyService identifyService,
    EnrichService enrichService,
    ICatalogPathSandbox sandbox,
    LibraryDeleteService deleteService,
    ILogger<RemapService> logger)
{
    // Path equality follows the filesystem: case-insensitive on Windows and default macOS, ordinal elsewhere.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

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

        // Wrap the DB writes so a mid-remap failure (a file op or a later delete) rolls back rather than
        // leaving a half-created target. Compose with an ambient transaction if one already exists.
        await using var transaction = database.Database.CurrentTransaction is null
            ? await database.Database.BeginTransactionAsync(cancellationToken)
            : null;

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

        // 1. Move every source file to the target's clean path, reassigning the MediaSource rows. A
        //    multi-version item keeps each source's version label so the rebuilt paths stay distinct.
        var emptiedDirs = new List<string>();
        string? targetLibraryPath = null;
        foreach (var source in sources)
        {
            var extension = Path.GetExtension(source.Path);
            var newRelative = target.Kind == MediaKind.Episode
                ? LibraryNaming.ForEpisode(targetSeries ?? target, target, extension, source.VersionName)
                : LibraryNaming.ForMovie(catalog, target, extension, source.VersionName);

            if (!sandbox.TryResolve(catalog, source.Path, out var oldAbsolute) ||
                !sandbox.TryResolve(catalog, newRelative, out var newAbsolute))
            {
                logger.LogWarning("Refusing to remap outside catalog root: {Old} → {New}", source.Path, newRelative);
                return RemapResult.NoSource;
            }

            // Compare the *resolved* absolute paths case-insensitively on case-insensitive filesystems
            // (Windows, default macOS): a case-only path change maps to the same file, so deleting the
            // "destination" would delete the source itself — a no-op move avoids that data loss.
            var sameFile = string.Equals(oldAbsolute, newAbsolute, PathComparison);
            if (!sameFile)
            {
                if (!File.Exists(oldAbsolute))
                {
                    logger.LogWarning("Remap source file missing on disk: {Path}", oldAbsolute);
                    return RemapResult.MissingFile;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(newAbsolute)!);
                if (File.Exists(newAbsolute))
                {
                    File.Delete(newAbsolute); // Idempotent: replace any stale file at the destination.
                }

                File.Move(oldAbsolute, newAbsolute);
                emptiedDirs.Add(Path.GetDirectoryName(oldAbsolute)!);
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

        // 3. Migrate the user's relationship with the file to its corrected identity, then prune the
        //    orphaned old item (and any season/series it emptied). Runs after the reassignment is
        //    persisted so the set-based deletes don't catch the moved sources.
        await CleanupOrphanAsync(current, target, cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        // 4. Remove any directories the moves emptied (best effort; the file already lives at its new path).
        foreach (var directory in emptiedDirs)
        {
            TryRemoveEmptyDirectories(directory, catalog.Root);
        }

        logger.LogInformation("Remapped {Old} → {Kind} '{Title}' ({Provider}:{Id}).",
            current.Id, target.Kind, target.Title, target.IdentityProvider, target.IdentityProviderId);
        return RemapResult.Ok(target.Id);
    }

    // Walk up from the emptied directory, deleting each now-empty directory until a non-empty one (or the
    // catalog root) is reached, so remapping out of a season/series folder leaves no empty parents behind.
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

    private static void AssignPublicId(MediaItem item) => item.PublicId ??= PublicIdFactory.ForItem(item);

    /// <summary>
    /// Migrates user signal from the misidentified leaf to its corrected identity, then removes the old
    /// leaf and whatever season/series it emptied. The plays and flags describe the <b>file</b> the user
    /// watched — the file now lives under <paramref name="target"/>, so the signal follows it instead of
    /// dying with the wrong-identity husk (which is why the husk is purged, never tombstoned).
    /// </summary>
    private async Task CleanupOrphanAsync(MediaItem old, MediaItem target, CancellationToken cancellationToken)
    {
        var remaining = await database.MediaSources.CountAsync(source => source.MediaItemId == old.Id, cancellationToken);
        if (remaining > 0)
        {
            return; // Defensive: another source still backs it (e.g. a multi-file item only partly moved).
        }

        await MigrateUserSignalAsync(old.Id, target.Id, cancellationToken);

        var seasonId = old.SeasonId;
        var seriesId = old.SeriesId;

        // The husk carries nothing of the user's any more — force the purge.
        await deleteService.RemoveItemsAsync([old.Id], deleteUserData: true, cancellationToken);

        // Emptied containers follow the shared delete rules: gone from the library either way, kept as
        // tombstones when ghost children or their own user signal still need them, purged otherwise.
        if (seasonId is { } season &&
            await database.MediaItems.CountAsync(item => item.SeasonId == season && item.PublicId != null, cancellationToken) == 0)
        {
            await deleteService.RemoveItemsAsync([season], deleteUserData: false, cancellationToken);
        }

        if (seriesId is { } series &&
            await database.MediaItems.CountAsync(item => item.Id != series && (item.SeriesId == series || item.ParentId == series) &&
                item.PublicId != null, cancellationToken) == 0)
        {
            await deleteService.RemoveItemsAsync([series], deleteUserData: false, cancellationToken);
        }
    }

    /// <summary>
    /// Repoints playback history and merges per-user state from <paramref name="oldId"/> onto
    /// <paramref name="targetId"/>. History rows repoint wholesale (a colliding play — same user and
    /// session already on the target — is the same viewing recorded twice, so the duplicate dies).
    /// User state repoints where the target has no row for the user, and merges where it does:
    /// favorite and watched combine with OR, the target keeps its own position and counts. Everything
    /// left on the old item afterwards is duplicate residue for the purge that follows.
    /// </summary>
    private async Task MigrateUserSignalAsync(Guid oldId, Guid targetId, CancellationToken cancellationToken)
    {
        await database.PlaybackHistoryEntries
            .Where(entry => entry.MediaItemId == oldId && entry.PlaySessionId != null &&
                database.PlaybackHistoryEntries.Any(existing => existing.MediaItemId == targetId &&
                    existing.AppUserId == entry.AppUserId && existing.PlaySessionId == entry.PlaySessionId))
            .ExecuteDeleteAsync(cancellationToken);
        await database.PlaybackHistoryEntries
            .Where(entry => entry.MediaItemId == oldId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(entry => entry.MediaItemId, targetId), cancellationToken);

        // Bulk updates bypass SaveChanges, so the StateRevision bump the Jellyfin delta sync relies on
        // is applied by hand in both statements.
        await database.UserItemData
            .Where(data => data.MediaItemId == oldId &&
                !database.UserItemData.Any(existing => existing.MediaItemId == targetId && existing.AppUserId == data.AppUserId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(data => data.MediaItemId, targetId)
                .SetProperty(data => data.StateRevision, data => data.StateRevision + 1), cancellationToken);
        await database.UserItemData
            .Where(data => data.MediaItemId == targetId &&
                database.UserItemData.Any(other => other.MediaItemId == oldId && other.AppUserId == data.AppUserId &&
                    (other.IsFavorite || other.Played || other.Rating != null)))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(data => data.IsFavorite, data => data.IsFavorite ||
                    database.UserItemData.Any(other => other.MediaItemId == oldId && other.AppUserId == data.AppUserId && other.IsFavorite))
                .SetProperty(data => data.Played, data => data.Played ||
                    database.UserItemData.Any(other => other.MediaItemId == oldId && other.AppUserId == data.AppUserId && other.Played))
                // The higher of the two ratings survives — the numeric reading of the OR above. MAX
                // ignores NULLs and yields NULL when neither row carries one, so "unrated" merges with
                // a rating by taking the rating, and with nothing by staying unrated.
                .SetProperty(data => data.Rating, data => database.UserItemData
                    .Where(other => (other.MediaItemId == oldId || other.MediaItemId == targetId) &&
                        other.AppUserId == data.AppUserId)
                    .Max(other => other.Rating))
                .SetProperty(data => data.StateRevision, data => data.StateRevision + 1), cancellationToken);
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
