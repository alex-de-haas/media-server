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

        if (!filesystem.DirectoryExists(root))
        {
            throw new CatalogValidationException($"Catalog root does not exist or is not reachable: {root}");
        }

        if (await database.Catalogs.AnyAsync(candidate => candidate.Root == root, cancellationToken))
        {
            throw new CatalogValidationException($"A catalog already exists for root: {root}");
        }

        var paths = CatalogPaths.For(root);
        paths.EnsureCreated();

        if (!filesystem.AreSameFilesystem(paths.FilesDir, paths.LibraryDir))
        {
            throw new CatalogValidationException(
                "files/ and library/ must be on the same filesystem so the organizer can hardlink between them.");
        }

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

        // Removing a catalog drops its DB rows only; on-disk media in the root is never deleted here.
        database.Catalogs.Remove(catalog);
        await database.SaveChangesAsync(cancellationToken);
        return true;
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
