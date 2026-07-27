using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Catalogs;

/// <summary>
/// Counts from one <see cref="CatalogAnchorService.ReanchorAllAsync"/> pass.
/// <paramref name="Reanchored"/> catalogs had their <see cref="Catalog.Root"/> rewritten for this
/// runtime, <paramref name="BackFilled"/> gained a mount label they had never recorded, and
/// <paramref name="Unanchored"/> could not be placed in any mount this runtime injects.
/// </summary>
public sealed record CatalogAnchorSummary(int Reanchored, int BackFilled, int Unanchored)
{
    public bool Changed => Reanchored > 0 || BackFilled > 0;
}

/// <summary>
/// Keeps every catalog's absolute <see cref="Catalog.Root"/> valid for the runtime the app is currently
/// running under. Hosty injects host paths for a mount under <c>dev</c> and container paths under
/// <c>docker</c>, so the same catalog has two different absolute paths; the durable identity is the mount
/// label plus the path within it (<see cref="CatalogRootResolver"/>), and the root is re-derived from it.
///
/// Runs once per process start, after migrations and before any worker reads a catalog. Mounts arrive as
/// environment and therefore only change across a restart, so startup is the only point that has to
/// re-resolve. Also serves the operator's explicit re-anchor action, for a mount that was renamed or a
/// catalog that moved to another volume.
/// </summary>
public sealed class CatalogAnchorService(
    MediaServerDbContext database,
    MediaServerSettings settings,
    IFilesystemInspector filesystem,
    IHostyCoreClient core,
    ILogger<CatalogAnchorService> logger)
{
    /// <summary>
    /// Re-resolves every catalog root against the mounts this runtime injects:
    /// a catalog with a known label gets its root rewritten; a catalog with no label whose root still
    /// falls inside a mount records that label (this is what carries catalogs created before anchoring
    /// existed, on the first start in the runtime whose paths still match); a catalog whose label this
    /// runtime does not inject is left untouched and reported, never guessed at.
    /// </summary>
    public async Task<CatalogAnchorSummary> ReanchorAllAsync(CancellationToken cancellationToken)
    {
        var mounts = settings.CatalogMountRoots;
        if (mounts.Count == 0)
        {
            // Standalone run: roots are free-text absolute paths and there is nothing to anchor them to.
            return new CatalogAnchorSummary(0, 0, 0);
        }

        var catalogs = await database.Catalogs.ToListAsync(cancellationToken);
        var reanchored = 0;
        var backFilled = 0;
        var unanchored = 0;

        foreach (var catalog in catalogs)
        {
            if (string.IsNullOrEmpty(catalog.MountLabel))
            {
                if (CatalogRootResolver.ToMountRelative(mounts, catalog.Root) is not { } anchor)
                {
                    // Either a deliberate out-of-mount root, or a root recorded under the other runtime
                    // whose paths this one cannot see. Nothing to derive a label from — report it and let
                    // the operator re-anchor.
                    unanchored++;
                    logger.LogWarning(
                        "Catalog {Catalog} ({Root}) is outside every catalog-root mount of this runtime and has no mount label.",
                        catalog.Name, catalog.Root);
                    continue;
                }

                catalog.MountLabel = anchor.Label;
                catalog.MountRelativePath = anchor.Relative;
                backFilled++;
                logger.LogInformation(
                    "Catalog {Catalog} anchored to mount {Label} at {Relative}.",
                    catalog.Name, anchor.Label, anchor.Relative.Length == 0 ? "<mount root>" : anchor.Relative);
                continue;
            }

            var resolved = CatalogRootResolver.Resolve(mounts, catalog.MountLabel, catalog.MountRelativePath);
            if (resolved is null)
            {
                unanchored++;
                logger.LogWarning(
                    "Catalog {Catalog} is anchored to mount {Label}, which this runtime does not provide; leaving its root at {Root}.",
                    catalog.Name, catalog.MountLabel, catalog.Root);
                continue;
            }

            if (string.Equals(resolved, catalog.Root, StringComparison.Ordinal))
            {
                continue;
            }

            logger.LogInformation(
                "Catalog {Catalog} re-anchored for this runtime: {OldRoot} -> {NewRoot}.",
                catalog.Name, catalog.Root, resolved);
            catalog.Root = resolved;
            catalog.UpdatedAt = DateTimeOffset.UtcNow;
            await RewriteDownloadPathsAsync(catalog, cancellationToken);
            reanchored++;
        }

        if (reanchored > 0 || backFilled > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }

        if (unanchored > 0)
        {
            await core.PublishNotificationAsync(
                CoreNotificationLevel.Warning,
                unanchored == 1 ? "A Media Server catalog needs re-anchoring" : $"{unanchored} Media Server catalogs need re-anchoring",
                "Their catalog-root mount is not available under the current runtime. Open Catalogs and re-anchor them to a configured mount; their library entries are untouched in the meantime.",
                link: null,
                dedupeKey: "media-server:catalogs-unanchored",
                cancellationToken: cancellationToken);
        }

        return new CatalogAnchorSummary(reanchored, backFilled, unanchored);
    }

    /// <summary>
    /// Re-points one catalog at <paramref name="label"/> + <paramref name="relativePath"/> on operator
    /// request, for a mount that was renamed or a catalog that moved. Creates the root the same way
    /// <see cref="CatalogService.CreateAsync"/> does — only when its parent is reachable, so a typo or an
    /// unmounted volume stays a hard error instead of silently producing an empty directory.
    /// </summary>
    public async Task<Catalog?> AnchorAsync(Guid id, string label, string? relativePath, CancellationToken cancellationToken)
    {
        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (catalog is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new CatalogValidationException("A mount is required.");
        }

        var relative = CatalogRootResolver.Normalize(relativePath)
            ?? throw new CatalogValidationException("The path within the mount must stay inside it.");
        // The mount's own label is what gets stored, not the caller's casing of it — see ResolveAnchor.
        var (canonicalLabel, resolved) = CatalogRootResolver.ResolveAnchor(settings.CatalogMountRoots, label.Trim(), relative)
            ?? throw new CatalogValidationException(
                $"No catalog-root mount named \"{label.Trim()}\" is configured for this runtime.");

        var taken = await database.Catalogs.AnyAsync(
            candidate => candidate.Id != id && candidate.MountLabel == canonicalLabel && candidate.MountRelativePath == relative,
            cancellationToken);
        if (taken)
        {
            throw new CatalogValidationException($"Another catalog already uses that location: {resolved}");
        }

        await EnsureNoDownloadIsWritingAsync(catalog, resolved, cancellationToken);
        EnsureRootUsable(resolved);

        catalog.MountLabel = canonicalLabel;
        catalog.MountRelativePath = relative;
        catalog.Root = resolved;
        catalog.UpdatedAt = DateTimeOffset.UtcNow;
        // The root is reachable by now, so clear the offline marker straight away rather than leave the
        // operator looking at a stale "offline" badge until the next health tick.
        catalog.OfflineSince = null;

        await RewriteDownloadPathsAsync(catalog, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Catalog {Catalog} re-anchored by operator to mount {Label} at {Root}.", catalog.Name, catalog.MountLabel, resolved);
        return catalog;
    }

    /// <summary>
    /// Refuses to move a catalog out from under a download the torrent engine is actively writing.
    /// Rewriting <see cref="Download.SavePath"/> alone would not move the existing <c>.incoming/</c> data
    /// or retarget the running engine: it would keep writing to the old directory while completion and
    /// deletion looked at the new one, stranding the files. The startup pass has no such problem — the
    /// engine is re-added from the (already rewritten) save paths after it runs.
    ///
    /// Only the states the engine actually resumes count (<see cref="DownloadState.Queued"/>,
    /// <see cref="DownloadState.Downloading"/>, <see cref="DownloadState.Seeding"/>; see
    /// <see cref="MediaServer.Api.Torrents.TorrentCoordinator"/>), and only while the catalog is where it
    /// says it is: for an unanchored catalog the root is unreachable, nothing can be writing there, and
    /// re-anchoring is precisely the repair — blocking it over a stale row would be a trap.
    /// </summary>
    private async Task EnsureNoDownloadIsWritingAsync(Catalog catalog, string resolved, CancellationToken cancellationToken)
    {
        if (string.Equals(catalog.Root, resolved, StringComparison.Ordinal) || !filesystem.DirectoryExists(catalog.Root))
        {
            return;
        }

        var active = await database.Downloads.CountAsync(
            download => download.CatalogId == catalog.Id &&
                (download.State == DownloadState.Queued ||
                 download.State == DownloadState.Downloading ||
                 download.State == DownloadState.Seeding),
            cancellationToken);

        if (active > 0)
        {
            throw new CatalogInUseException(
                $"This catalog has {active} active download(s) writing to {catalog.Root}. Let them finish (or remove them) before moving the catalog.");
        }
    }

    /// <summary>
    /// Mirrors <see cref="CatalogService"/>'s create rule: a missing root is created when its parent is
    /// reachable, so the operator can re-anchor onto a new sub-folder of a mount; a missing parent means a
    /// typo or an unmounted volume and stays a hard error rather than an empty directory nobody asked for.
    /// </summary>
    private void EnsureRootUsable(string root)
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

        CatalogPaths.For(root).EnsureCreated();
    }

    /// <summary>
    /// A download's save path is <c>&lt;root&gt;/.incoming/&lt;downloadId&gt;</c> — derived, so it follows the
    /// root rather than being remembered independently. Rewritten for every download of the catalog: a
    /// finished one keeps a path that no longer resolves otherwise, and the deletion path still reads it.
    /// </summary>
    private async Task RewriteDownloadPathsAsync(Catalog catalog, CancellationToken cancellationToken)
    {
        var paths = CatalogPaths.For(catalog.Root);
        var downloads = await database.Downloads
            .Where(download => download.CatalogId == catalog.Id)
            .ToListAsync(cancellationToken);

        foreach (var download in downloads)
        {
            download.SavePath = paths.IncomingFor(download.Id);
        }
    }
}
