using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Catalogs;

/// <summary>
/// Operator-managed catalog configuration. Validates that a catalog root is a single filesystem (so
/// the organizer can hardlink <c>files/</c> ↔ <c>library/</c>), is within the Hosty-provided mounts
/// when those are injected, and reports free space / offline status for the UI.
/// </summary>
public sealed class CatalogService(
    MediaServerDbContext database,
    IFilesystemInspector filesystem,
    MediaServerSettings settings)
{
    public async Task<IReadOnlyList<CatalogResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var catalogs = await database.Catalogs
            .AsNoTracking()
            .OrderBy(catalog => catalog.Name)
            .ToListAsync(cancellationToken);

        return catalogs.Select(ToResponse).ToList();
    }

    /// <summary>
    /// The catalog-root mounts the operator may place catalogs under (from <c>HOSTY_MOUNT_CATALOGROOTS</c>).
    /// Empty under standalone local runs where mounts are not enforced. The label is the operator-chosen
    /// Hosty mount label (falling back to the path base name only when none was injected).
    /// </summary>
    public IReadOnlyList<CatalogMountResponse> ListMounts()
    {
        return settings.CatalogMountRoots
            .Select(mount => new CatalogMountResponse(
                string.IsNullOrEmpty(mount.Label) ? mount.Path : mount.Label, mount.Path))
            .ToList();
    }

    /// <summary>
    /// Storage usage grouped by volume. Each catalog's footprint is the sum of its tracked media-source
    /// sizes — approximate (hardlinked files/↔library/ count once, but in-flight partials and non-media
    /// extras are not). Free is a per-volume fact (several catalogs can share a volume); non-catalog
    /// usage is intentionally not reported, so the UI scales the bar to Σ(used) + free.
    /// </summary>
    public async Task<IReadOnlyList<CatalogVolumeUsageResponse>> ListUsageAsync(CancellationToken cancellationToken)
    {
        var catalogs = await database.Catalogs.AsNoTracking()
            .OrderBy(catalog => catalog.Name)
            .ToListAsync(cancellationToken);

        // Tombstones own no sources, so the null-catalog group can only ever be empty — filter it out
        // rather than key the dictionary on a nullable id.
        var usedByCatalog = await (
                from source in database.MediaSources.AsNoTracking()
                join item in database.MediaItems.AsNoTracking() on source.MediaItemId equals item.Id
                where item.CatalogId != null
                group source.SizeBytes by item.CatalogId!.Value into grouped
                select new { CatalogId = grouped.Key, Used = grouped.Sum() })
            .ToDictionaryAsync(entry => entry.CatalogId, entry => entry.Used, cancellationToken);

        return catalogs
            // Group by the resolved volume; when it can't be resolved (offline/unmounted), fall back to
            // the catalog's own path so unrelated "unknown" catalogs aren't merged into one bogus group.
            .GroupBy(catalog =>
            {
                var key = filesystem.GetVolumeKey(catalog.Root);
                return string.IsNullOrEmpty(key) ? Path.GetFullPath(catalog.Root) : key;
            })
            .Select(group =>
            {
                var sampleRoot = group.First().Root;
                var free = filesystem.GetAvailableFreeBytes(sampleRoot);
                var entries = group
                    .Select(catalog => new CatalogUsageEntry(
                        catalog.Id, catalog.Name, catalog.Type, usedByCatalog.GetValueOrDefault(catalog.Id)))
                    .ToList();
                return new CatalogVolumeUsageResponse(group.Key, free, entries);
            })
            .OrderBy(volume => volume.Label, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<CatalogResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var catalog = await database.Catalogs.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        return catalog is null ? null : ToResponse(catalog);
    }

    public async Task<CatalogResponse> CreateAsync(CreateCatalogRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new CatalogValidationException("Name is required.");
        }

        // The mount label is the catalog's durable identity; the absolute root is only where that mount
        // happens to be in this runtime. A request may name the mount directly (the UI's picker) or give a
        // free-text absolute root (standalone runs) — which is still anchored when it lands inside a mount.
        string root;
        string? mountLabel;
        string? mountRelative;

        if (!string.IsNullOrWhiteSpace(request.MountLabel))
        {
            mountLabel = request.MountLabel.Trim();
            mountRelative = CatalogRootResolver.Normalize(request.RelativePath);
            root = CatalogRootResolver.Resolve(settings.CatalogMountRoots, mountLabel, mountRelative)
                ?? throw new CatalogValidationException(
                    $"No catalog-root mount named \"{mountLabel}\" is configured for this runtime.");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(request.Root))
            {
                throw new CatalogValidationException("Root is required.");
            }

            root = Path.GetFullPath(request.Root);
            ValidateWithinMountRoots(root);
            (mountLabel, mountRelative) = CatalogRootResolver.ToMountRelative(settings.CatalogMountRoots, root) is { } anchor
                ? (anchor.Label, anchor.Relative)
                : (null, null);
        }

        EnsureRootReachable(root);

        var duplicate = mountLabel is null
            ? await database.Catalogs.AnyAsync(candidate => candidate.Root == root, cancellationToken)
            : await database.Catalogs.AnyAsync(
                candidate => candidate.MountLabel == mountLabel && candidate.MountRelativePath == mountRelative,
                cancellationToken);
        if (duplicate)
        {
            throw new CatalogValidationException($"A catalog already exists for root: {root}");
        }

        var paths = CatalogPaths.For(root);
        paths.EnsureCreated();

        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Type = request.Type,
            Root = root,
            MountLabel = mountLabel,
            MountRelativePath = mountRelative,
            NamingTemplate = string.IsNullOrWhiteSpace(request.NamingTemplate)
                ? "{Title} ({Year})"
                : request.NamingTemplate.Trim(),
            DefaultKeepSeeding = request.DefaultKeepSeeding,
            MetadataLanguage = string.IsNullOrWhiteSpace(request.MetadataLanguage) ? null : request.MetadataLanguage.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        database.Catalogs.Add(catalog);
        await database.SaveChangesAsync(cancellationToken);

        return ToResponse(catalog);
    }

    public async Task<CatalogResponse?> UpdateAsync(Guid id, UpdateCatalogRequest request, CancellationToken cancellationToken)
    {
        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (catalog is null)
        {
            return null;
        }

        if (request.Name is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                throw new CatalogValidationException("Name cannot be blank.");
            }

            catalog.Name = request.Name.Trim();
        }

        if (request.NamingTemplate is not null)
        {
            catalog.NamingTemplate = string.IsNullOrWhiteSpace(request.NamingTemplate)
                ? "{Title} ({Year})"
                : request.NamingTemplate.Trim();
        }

        if (request.DefaultKeepSeeding is { } keepSeeding)
        {
            catalog.DefaultKeepSeeding = keepSeeding;
        }

        if (request.MetadataLanguage is not null)
        {
            catalog.MetadataLanguage = string.IsNullOrWhiteSpace(request.MetadataLanguage) ? null : request.MetadataLanguage.Trim();
        }

        catalog.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        return ToResponse(catalog);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var exists = await database.Catalogs.AnyAsync(candidate => candidate.Id == id, cancellationToken);
        if (!exists)
        {
            return false;
        }

        // Downloads and ingest items reference the catalog with a Restrict FK, so deleting while any
        // exist would fail at the database. Surface a clear conflict the UI can act on instead.
        var downloads = await database.Downloads.CountAsync(download => download.CatalogId == id, cancellationToken);
        var ingest = await database.IngestItems.CountAsync(item => item.CatalogId == id, cancellationToken);
        if (downloads > 0 || ingest > 0)
        {
            throw new CatalogInUseException(
                $"This catalog still has {downloads} download(s) and {ingest} pipeline item(s). Remove them first, then delete the catalog.");
        }

        // Removing a catalog drops its DB rows only; on-disk media in the root is never deleted here.
        // MediaItem→Catalog is SetNull, but we cannot lean on it: user signal is bound to the work, not
        // the shelf it stood on, so items someone favorited, watched, or played become catalog-less
        // tombstones (see LibraryDeleteService) while untouched items are purged explicitly,
        // child→parent — the self-FK on MediaItem.ParentId is Restrict, so a series deleted ahead of
        // its seasons trips "FOREIGN KEY constraint failed".
        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            // Composable subqueries, never materialized id lists: a catalog is unbounded, and EF expands an
            // in-memory Contains into one host parameter per id (SQLite caps them, and shipping thousands of
            // ids to the server and back is wasted work either way). These stay IQueryable so the ids never
            // leave the database. Each is evaluated where it is used, before the rows it selects are deleted.
            var itemIds = database.MediaItems
                .Where(item => item.CatalogId == id)
                .Select(item => item.Id);
            var sourceIds = database.MediaSources
                .Where(source => itemIds.Contains(source.MediaItemId))
                .Select(source => source.Id);

            // Keep the download's files; just unassign them from the items about to disappear.
            await database.SourceFiles
                .Where(file => file.MediaItemId != null && itemIds.Contains(file.MediaItemId.Value))
                .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.MediaItemId, (Guid?)null), cancellationToken);

            // Dependents first (explicit, so we don't depend on DB cascade being enabled). Transcode jobs
            // hold a Restrict FK straight to the catalog, so they have to go regardless of their media link.
            await database.TranscodeJobs.Where(job => job.CatalogId == id).ExecuteDeleteAsync(cancellationToken);
            // Streams before sources: sourceIds reads MediaSources, so it must run while those rows still exist.
            // Tombstones lose their sources like everything else — a ghost has no playable substance.
            await database.MediaStreams.Where(stream => sourceIds.Contains(stream.MediaSourceId)).ExecuteDeleteAsync(cancellationToken);
            await database.MediaSources.Where(source => itemIds.Contains(source.MediaItemId)).ExecuteDeleteAsync(cancellationToken);

            // Transient sessions go for every item: purged rows would cascade them anyway, and a ghost
            // cannot be played.
            await database.PlaybackSessions
                .Where(session => database.MediaItems.Any(item => item.Id == session.MediaItemId && item.CatalogId == id))
                .ExecuteDeleteAsync(cancellationToken);

            // Tombstone the survivors before anything else touches MediaItems: one update per hierarchy
            // level, top-down, so each statement's descendant subqueries only read rows a later statement
            // will update. Setting CatalogId to null here is what excludes ghosts from every
            // `CatalogId == id` delete below — no Except needed anywhere. Signal stays a composed
            // subquery (UNION of user-data flags and history) for the same no-materialization reason.
            var signalIds = database.UserItemData
                .Where(data => data.IsFavorite || data.Played || data.PlaybackPositionTicks > 0 || data.PlayCount > 0)
                .Select(data => data.MediaItemId)
                .Concat(database.PlaybackHistoryEntries.Select(entry => entry.MediaItemId));
            var now = DateTimeOffset.UtcNow;

            Task TombstoneAsync(IQueryable<MediaItem> items) => items.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.CatalogId, (Guid?)null)
                .SetProperty(item => item.PublicId, (string?)null)
                .SetProperty(item => item.RemovedAt, now)
                .SetProperty(item => item.LibraryPath, (string?)null)
                .SetProperty(item => item.DefaultSourceId, (Guid?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);

            await TombstoneAsync(database.MediaItems
                .Where(item => item.CatalogId == id &&
                    (item.Kind == MediaKind.Series || item.Kind == MediaKind.Movie) &&
                    (signalIds.Contains(item.Id) ||
                     database.MediaItems.Any(child => child.CatalogId == id && child.SeriesId == item.Id &&
                        signalIds.Contains(child.Id)))));
            await TombstoneAsync(database.MediaItems
                .Where(item => item.CatalogId == id && item.Kind == MediaKind.Season &&
                    (signalIds.Contains(item.Id) ||
                     database.MediaItems.Any(child => child.CatalogId == id &&
                        (child.SeasonId == item.Id || child.ParentId == item.Id) &&
                        signalIds.Contains(child.Id)))));
            await TombstoneAsync(database.MediaItems
                .Where(item => item.CatalogId == id &&
                    (item.Kind == MediaKind.Episode || item.Kind == MediaKind.Video) &&
                    signalIds.Contains(item.Id)));

            // A purge unlinks tracked titles through the FK's SetNull; tombstones keep their rows, so the
            // wishlist would keep reading "in library" — unlink every ghost by hand.
            await database.TrackedTitles
                .Where(title => title.MediaItemId != null &&
                    database.MediaItems.Any(item => item.Id == title.MediaItemId && item.RemovedAt != null))
                .ExecuteUpdateAsync(setters => setters.SetProperty(title => title.MediaItemId, (Guid?)null), cancellationToken);

            // From here on, `CatalogId == id` names only the untouched items — purge them as before.
            // itemIds re-evaluates against the narrowed set wherever it is used.
            await database.MetadataRecords.Where(record => itemIds.Contains(record.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
            await database.ImageAssets.Where(image => itemIds.Contains(image.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
            await database.MediaItemPersons.Where(credit => itemIds.Contains(credit.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
            await database.UserItemData.Where(data => itemIds.Contains(data.MediaItemId)).ExecuteDeleteAsync(cancellationToken);

            // Items child→parent: leaves first — episodes and extras (Videos parent to their series,
            // season or movie) — then seasons, then the roots. Filtered on CatalogId directly: itemIds reads
            // the very table being deleted, so each pass would narrow the set out from under the next.
            await database.MediaItems.Where(media => media.CatalogId == id &&
                (media.Kind == MediaKind.Episode || media.Kind == MediaKind.Video)).ExecuteDeleteAsync(cancellationToken);
            await database.MediaItems.Where(media => media.CatalogId == id && media.Kind == MediaKind.Season).ExecuteDeleteAsync(cancellationToken);
            await database.MediaItems.Where(media => media.CatalogId == id &&
                (media.Kind == MediaKind.Series || media.Kind == MediaKind.Movie)).ExecuteDeleteAsync(cancellationToken);

            // ExecuteDelete throughout, including the catalog itself: a tracked Remove would make the change
            // tracker re-issue cascade deletes for items these statements already dropped, which then fails
            // the "expected to affect 1 row" concurrency check.
            await database.Catalogs.Where(candidate => candidate.Id == id).ExecuteDeleteAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// A missing root is created (its <c>files/</c> + <c>library/</c> subtrees follow), so several
    /// catalogs can live as sibling subfolders of one mount. Creation only happens when the parent is
    /// reachable — a missing parent means a typo or an unmounted volume, which stays a hard error.
    /// </summary>
    private void EnsureRootReachable(string root)
    {
        if (filesystem.DirectoryExists(root))
        {
            return;
        }

        var parent = Path.GetDirectoryName(root);
        if (string.IsNullOrEmpty(parent) || !filesystem.DirectoryExists(parent))
        {
            throw new CatalogValidationException($"Catalog root does not exist or is not reachable: {root}");
        }
    }

    /// <summary>
    /// Rejects roots outside the Hosty-injected catalog mounts. Skipped when no mounts are injected
    /// (standalone local runs), matching the dev runtime where mounts are not enforced at start.
    /// </summary>
    private void ValidateWithinMountRoots(string root)
    {
        if (settings.CatalogMountRoots.Count == 0)
        {
            return;
        }

        // Match ToMountRelative: paths are case-insensitive on Windows, so a validly-cased catalog root
        // under a mount isn't rejected over a casing difference.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var withinMount = settings.CatalogMountRoots.Any(mount =>
        {
            var normalized = Path.GetFullPath(mount.Path);
            var withSeparator = normalized.EndsWith(Path.DirectorySeparatorChar) ? normalized : normalized + Path.DirectorySeparatorChar;
            return root.Equals(normalized, comparison) || root.StartsWith(withSeparator, comparison);
        });

        if (!withinMount)
        {
            throw new CatalogValidationException(
                "Catalog root must be within a configured catalog-root mount (HOSTY_MOUNT_CATALOGROOTS).");
        }
    }

    private CatalogResponse ToResponse(Catalog catalog)
    {
        var online = filesystem.DirectoryExists(catalog.Root);
        var freeBytes = online ? filesystem.GetAvailableFreeBytes(catalog.Root) : 0;
        return CatalogResponse.From(catalog, freeBytes, online, IsUnanchored(catalog));
    }

    /// <summary>
    /// A catalog is unanchored when mounts are injected but none of them holds its root. Startup rewrites
    /// the root of every catalog whose label this runtime provides (see <see cref="CatalogAnchorService"/>),
    /// so a root still outside every mount here means the label is unknown to this runtime — or was never
    /// recorded, for a root created under the other runtime profile. Standalone runs (no mounts) never
    /// report it: there is nothing to be anchored to.
    /// </summary>
    private bool IsUnanchored(Catalog catalog) =>
        settings.CatalogMountRoots.Count > 0 &&
        CatalogRootResolver.ToMountRelative(settings.CatalogMountRoots, catalog.Root) is null;
}
