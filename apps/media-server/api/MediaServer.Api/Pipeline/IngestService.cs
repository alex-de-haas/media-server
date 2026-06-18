using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// Read + operator-action surface over ingest items: listing, detail, retry, and manual match override
/// for items parked in <see cref="IngestStatus.NeedsReview"/>. Operator actions re-enqueue the item so
/// the orchestrator resumes it at the correct stage.
/// </summary>
public sealed class IngestService(
    MediaServerDbContext database, IdentifyService identifyService, IMetadataProvider metadataProvider, IPipelineQueue queue)
{
    public async Task<IReadOnlyList<IngestItemResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var items = await database.IngestItems
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

        var sourceFilesByDownload = await LoadSourceFilesAsync(items, cancellationToken);
        var downloadNames = await LoadDownloadNamesAsync(items, cancellationToken);
        return items
            .Select(item => IngestItemResponse.From(item, SourceFilesFor(item, sourceFilesByDownload), DownloadNameFor(item, downloadNames)))
            .ToList();
    }

    public async Task<IngestItemResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await database.IngestItems.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var sourceFiles = item.DownloadId is { } downloadId
            ? await database.SourceFiles.AsNoTracking().Where(file => file.DownloadId == downloadId).ToListAsync(cancellationToken)
            : [];
        var downloadName = item.DownloadId is { } id2
            ? await database.Downloads.AsNoTracking().Where(download => download.Id == id2).Select(download => download.Name).FirstOrDefaultAsync(cancellationToken)
            : null;
        return IngestItemResponse.From(item, sourceFiles, downloadName);
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
    /// Removes a single ingest tracking row (operator safety valve, e.g. for an orphaned/stuck entry).
    /// Only the <see cref="IngestItem"/> is deleted — nothing references it, so downloads, source files,
    /// and any published library item are untouched. Returns false if it no longer exists.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await database.IngestItems.FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);
        if (item is null)
        {
            return false;
        }

        database.IngestItems.Remove(item);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<Dictionary<Guid, List<SourceFile>>> LoadSourceFilesAsync(
        IReadOnlyList<IngestItem> items, CancellationToken cancellationToken)
    {
        var downloadIds = items.Select(item => item.DownloadId).OfType<Guid>().Distinct().ToList();
        if (downloadIds.Count == 0)
        {
            return new Dictionary<Guid, List<SourceFile>>();
        }

        var files = await database.SourceFiles.AsNoTracking()
            .Where(file => downloadIds.Contains(file.DownloadId))
            .ToListAsync(cancellationToken);

        return files.GroupBy(file => file.DownloadId).ToDictionary(group => group.Key, group => group.ToList());
    }

    private static IReadOnlyList<SourceFile> SourceFilesFor(IngestItem item, Dictionary<Guid, List<SourceFile>> byDownload) =>
        item.DownloadId is { } downloadId && byDownload.TryGetValue(downloadId, out var files) ? files : [];

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
}
