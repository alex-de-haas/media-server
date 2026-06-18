using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// Read + operator-action surface over ingest items: listing, detail, retry, and manual match override
/// for items parked in <see cref="IngestStatus.NeedsReview"/>. Operator actions re-enqueue the item so
/// the orchestrator resumes it at the correct stage.
/// </summary>
public sealed class IngestService(MediaServerDbContext database, IdentifyService identifyService, IPipelineQueue queue)
{
    public async Task<IReadOnlyList<IngestItemResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var items = await database.IngestItems
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(cancellationToken);

        var sourceFilesByDownload = await LoadSourceFilesAsync(items, cancellationToken);
        return items.Select(item => IngestItemResponse.From(item, SourceFilesFor(item, sourceFilesByDownload))).ToList();
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
        return IngestItemResponse.From(item, sourceFiles);
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
}
