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
    /// Empty under standalone local runs where mounts are not enforced. The label is the base folder name.
    /// </summary>
    public IReadOnlyList<CatalogMountResponse> ListMounts()
    {
        return settings.CatalogMountRoots
            .Select(path =>
            {
                var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var label = Path.GetFileName(trimmed);
                return new CatalogMountResponse(string.IsNullOrEmpty(label) ? path : label, path);
            })
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
        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (catalog is null)
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
        database.Catalogs.Remove(catalog);
        await database.SaveChangesAsync(cancellationToken);
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

        var withinMount = settings.CatalogMountRoots.Any(mount =>
        {
            var normalized = Path.GetFullPath(mount);
            var withSeparator = normalized.EndsWith(Path.DirectorySeparatorChar) ? normalized : normalized + Path.DirectorySeparatorChar;
            return root.Equals(normalized, StringComparison.Ordinal) || root.StartsWith(withSeparator, StringComparison.Ordinal);
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
