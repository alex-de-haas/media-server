using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Torrents;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// Read + operator-action surface over ingest items: listing, detail, retry, and manual match override
/// for items parked in <see cref="IngestStatus.NeedsReview"/>. Operator actions re-enqueue the item so
/// the orchestrator resumes it at the correct stage.
/// </summary>
public sealed class IngestService(
    MediaServerDbContext database,
    IdentifyService identifyService,
    IMetadataProvider metadataProvider,
    IPipelineQueue queue,
    DownloadDeletionService downloadDeletion,
    ICatalogPathSandbox sandbox,
    ILogger<IngestService> logger)
{
    public async Task<IReadOnlyList<IngestItemResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var items = await database.IngestItems
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

        var sourceFilesByDownload = await LoadSourceFilesAsync(items, cancellationToken);
        var downloadNames = await LoadDownloadNamesAsync(items, cancellationToken);
        var mediaTitles = await LoadMediaTitlesAsync(items, cancellationToken);
        return items
            .Select(item => IngestItemResponse.From(
                item, SourceFilesFor(item, sourceFilesByDownload), DownloadNameFor(item, downloadNames), MediaTitleFor(item, mediaTitles)))
            .ToList();
    }

    public async Task<IngestItemResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await database.IngestItems.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var sourceFiles = await database.SourceFiles.AsNoTracking()
            .Where(file => file.IngestItemId == item.Id).ToListAsync(cancellationToken);
        var downloadName = item.DownloadId is { } id2
            ? await database.Downloads.AsNoTracking().Where(download => download.Id == id2).Select(download => download.Name).FirstOrDefaultAsync(cancellationToken)
            : null;
        var mediaTitle = item.MediaItemId is { } mediaId
            ? await database.MediaItems.AsNoTracking().Where(media => media.Id == mediaId).Select(media => media.Title).FirstOrDefaultAsync(cancellationToken)
            : null;
        return IngestItemResponse.From(item, sourceFiles, downloadName, mediaTitle);
    }

    public async Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await database.IngestItems.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null)
        {
            return false;
        }

        item.Status = IngestStatus.Pending;
        item.NextAttemptAt = null;
        item.AttemptCount = 0;
        item.LeaseOwner = null;
        item.LeaseUntil = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        queue.Enqueue(item.Id);
        return true;
    }

    /// <summary>
    /// Re-runs the metadata search with an operator-corrected title for a parked item. Returns null when
    /// the item or its catalog is gone; otherwise the scored candidates to render in the review panel.
    /// The catalog's type picks the default search kind (movie vs. series) when the caller omits it.
    /// </summary>
    public async Task<IReadOnlyList<MetadataCandidate>?> SearchAsync(Guid id, MetadataSearchRequest request, CancellationToken cancellationToken)
    {
        var item = await database.IngestItems.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var catalog = await database.Catalogs.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == item.CatalogId, cancellationToken);
        if (catalog is null)
        {
            return null;
        }

        var kind = request.Kind ?? (catalog.Type == CatalogType.Movie ? MediaKind.Movie : MediaKind.Series);
        var title = request.Title.Trim();

        var results = await metadataProvider.SearchAsync(new MediaQuery(kind, title, request.Year), cancellationToken);

        // The operator-typed year is a hint, not a hard filter. TMDb's year-constrained search returns
        // nothing for a title whose release date doesn't match (or isn't set yet) — common for upcoming
        // films like the one that prompted the manual search. Fall back to a yearless search so the
        // operator still gets candidates to pick from rather than an empty "no matches".
        if (results.Count == 0 && request.Year is not null)
        {
            results = await metadataProvider.SearchAsync(new MediaQuery(kind, title, null), cancellationToken);
        }

        return results;
    }

    public async Task<bool> MatchAsync(Guid id, MatchRequest request, CancellationToken cancellationToken)
    {
        var item = await database.IngestItems.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null)
        {
            return false;
        }

        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == item.CatalogId, cancellationToken)
            ?? throw new InvalidOperationException("Catalog not found for ingest item.");

        var sourceFile = await database.SourceFiles.FirstOrDefaultAsync(file => file.Id == request.SourceFileId, cancellationToken)
            ?? throw new InvalidOperationException("Source file not found.");

        var candidate = new MetadataCandidate(new ProviderRef(request.Provider, request.ProviderId), request.Title, request.Year, 1.0);

        var mediaItem = request.Kind == MediaKind.Episode
            ? await identifyService.ResolveEpisodeAsync(catalog,
                candidate, new ParsedName(MediaKind.Episode, request.Title, request.Year, request.Season, request.Episode, null), cancellationToken)
            : await identifyService.ResolveMovieAsync(catalog, candidate, cancellationToken);

        sourceFile.MediaItemId = mediaItem.Id;
        sourceFile.AssignmentStatus = SourceFileAssignmentStatus.Confirmed;
        sourceFile.UpdatedAt = DateTimeOffset.UtcNow;

        item.Status = IngestStatus.Pending;
        item.NextAttemptAt = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        queue.Enqueue(item.Id);
        return true;
    }

    /// <summary>
    /// Removes an ingest. The operator is never asked about physical files — deletion is automatic by where
    /// the file sits: an in-flight item delegates to download removal (stops the torrent, clears its
    /// <c>.incoming/</c> staging and the engine's resume cache, drops the download + this ingest); a
    /// post-hand-off item erases any <c>.incoming/</c> staging it still owns; a published item just drops the
    /// tracking row, leaving its canonical library file. Returns false if it no longer exists.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await database.IngestItems.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null)
        {
            return false;
        }

        // In-flight: download removal stops the torrent, clears its .incoming/ staging + engine resume
        // cache, and drops the download together with this ingest.
        if (item.DownloadId is { } downloadId)
        {
            await downloadDeletion.DeleteAsync(downloadId, deleteFiles: true, cancellationToken);
            return true;
        }

        // No download (scan import, or after the hand-off). Note any .incoming/ staging folders this ingest
        // owns so they can be erased once the rows are gone; canonical (published) files are left untouched.
        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == item.CatalogId, cancellationToken);
        var stagingDirs = (await database.SourceFiles
                .Where(file => file.IngestItemId == id)
                .Select(file => file.RelativePath)
                .ToListAsync(cancellationToken))
            .Where(CatalogPaths.IsIncoming)
            .Select(StagingRootOf)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        database.IngestItems.Remove(item); // SourceFile rows cascade away with it.
        await database.SaveChangesAsync(cancellationToken);

        if (catalog is not null)
        {
            foreach (var staging in stagingDirs)
            {
                if (sandbox.TryResolve(catalog, staging, out var absolute))
                {
                    TryDeleteDirectory(absolute);
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Clears every published (<see cref="IngestStatus.Done"/>) item — the "Delete all" on the Done tab.
    /// Each row is removed through <see cref="DeleteAsync"/>, so its staging cleanup rules apply and library
    /// files are left untouched. Returns how many rows were removed.
    /// </summary>
    public async Task<int> DeleteCompletedAsync(CancellationToken cancellationToken)
    {
        var ids = await database.IngestItems
            .Where(item => item.Status == IngestStatus.Done)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        var removed = 0;
        foreach (var id in ids)
        {
            if (await DeleteAsync(id, cancellationToken))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>The <c>.incoming/&lt;downloadId&gt;</c> staging root of a path, or null if it is not staged.</summary>
    private static string? StagingRootOf(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : null;
    }

    private void TryDeleteDirectory(string absolute)
    {
        try
        {
            if (Directory.Exists(absolute))
            {
                Directory.Delete(absolute, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Failed to remove staging folder {Path}", absolute);
        }
    }

    private async Task<Dictionary<Guid, List<SourceFile>>> LoadSourceFilesAsync(
        IReadOnlyList<IngestItem> items, CancellationToken cancellationToken)
    {
        var ingestIds = items.Select(item => item.Id).ToList();
        if (ingestIds.Count == 0)
        {
            return new Dictionary<Guid, List<SourceFile>>();
        }

        var files = await database.SourceFiles.AsNoTracking()
            .Where(file => ingestIds.Contains(file.IngestItemId))
            .ToListAsync(cancellationToken);

        return files.GroupBy(file => file.IngestItemId).ToDictionary(group => group.Key, group => group.ToList());
    }

    private static IReadOnlyList<SourceFile> SourceFilesFor(IngestItem item, Dictionary<Guid, List<SourceFile>> byIngest) =>
        byIngest.TryGetValue(item.Id, out var files) ? files : [];

    private async Task<Dictionary<Guid, string?>> LoadDownloadNamesAsync(
        IReadOnlyList<IngestItem> items, CancellationToken cancellationToken)
    {
        var downloadIds = items.Select(item => item.DownloadId).OfType<Guid>().Distinct().ToList();
        if (downloadIds.Count == 0)
        {
            return new Dictionary<Guid, string?>();
        }

        return await database.Downloads.AsNoTracking()
            .Where(download => downloadIds.Contains(download.Id))
            .ToDictionaryAsync(download => download.Id, download => download.Name, cancellationToken);
    }

    private static string? DownloadNameFor(IngestItem item, Dictionary<Guid, string?> byDownload) =>
        item.DownloadId is { } downloadId && byDownload.TryGetValue(downloadId, out var name) ? name : null;

    private async Task<Dictionary<Guid, string>> LoadMediaTitlesAsync(
        IReadOnlyList<IngestItem> items, CancellationToken cancellationToken)
    {
        var mediaItemIds = items.Select(item => item.MediaItemId).OfType<Guid>().Distinct().ToList();
        if (mediaItemIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await database.MediaItems.AsNoTracking()
            .Where(media => mediaItemIds.Contains(media.Id))
            .ToDictionaryAsync(media => media.Id, media => media.Title, cancellationToken);
    }

    private static string? MediaTitleFor(IngestItem item, Dictionary<Guid, string> byMediaItem) =>
        item.MediaItemId is { } mediaItemId && byMediaItem.TryGetValue(mediaItemId, out var title) ? title : null;
}
