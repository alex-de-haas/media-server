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

        var usedByCatalog = await (
                from source in database.MediaSources.AsNoTracking()
                join item in database.MediaItems.AsNoTracking() on source.MediaItemId equals item.Id
                group source.SizeBytes by item.CatalogId into grouped
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

        if (string.IsNullOrWhiteSpace(request.Root))
        {
            throw new CatalogValidationException("Root is required.");
        }

        var root = Path.GetFullPath(request.Root);
        ValidateWithinMountRoots(root);
        EnsureRootReachable(root);

        if (await database.Catalogs.AnyAsync(candidate => candidate.Root == root, cancellationToken))
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
        // MediaItem→Catalog is Cascade, but we cannot lean on it: the DB would delete the catalog's items
        // in arbitrary order and the self-FK on MediaItem.ParentId is Restrict, so a series deleted ahead
        // of its seasons trips "FOREIGN KEY constraint failed". Clear the items explicitly, child→parent.
        await using (var transaction = await database.Database.BeginTransactionAsync(cancellationToken))
        {
            var ids = await database.MediaItems
                .Where(item => item.CatalogId == id)
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);

            // Keep the download's files; just unassign them from the items about to disappear.
            await database.SourceFiles
                .Where(file => file.MediaItemId != null && ids.Contains(file.MediaItemId.Value))
                .ExecuteUpdateAsync(setters => setters.SetProperty(file => file.MediaItemId, (Guid?)null), cancellationToken);

            // Dependents first (explicit, so we don't depend on DB cascade being enabled). Transcode jobs
            // hold a Restrict FK straight to the catalog, so they have to go regardless of their media link.
            var sourceIds = await database.MediaSources
                .Where(source => ids.Contains(source.MediaItemId))
                .Select(source => source.Id)
                .ToListAsync(cancellationToken);
            await database.TranscodeJobs.Where(job => job.CatalogId == id).ExecuteDeleteAsync(cancellationToken);
            await database.MediaStreams.Where(stream => sourceIds.Contains(stream.MediaSourceId)).ExecuteDeleteAsync(cancellationToken);
            await database.MediaSources.Where(source => ids.Contains(source.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
            await database.MetadataRecords.Where(record => ids.Contains(record.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
            await database.ImageAssets.Where(image => ids.Contains(image.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
            await database.MediaItemPersons.Where(credit => ids.Contains(credit.MediaItemId)).ExecuteDeleteAsync(cancellationToken);
            await database.UserItemData.Where(data => ids.Contains(data.MediaItemId)).ExecuteDeleteAsync(cancellationToken);

            // Items child→parent: leaves first — episodes and extras (Videos parent to their series,
            // season or movie) — then seasons, then the roots.
            await database.MediaItems.Where(media => ids.Contains(media.Id) &&
                (media.Kind == MediaKind.Episode || media.Kind == MediaKind.Video)).ExecuteDeleteAsync(cancellationToken);
            await database.MediaItems.Where(media => ids.Contains(media.Id) && media.Kind == MediaKind.Season).ExecuteDeleteAsync(cancellationToken);
            await database.MediaItems.Where(media => ids.Contains(media.Id) &&
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
        return CatalogResponse.From(catalog, freeBytes, online);
    }
}
