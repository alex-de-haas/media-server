using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
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
    INameParser nameParser,
    AppSettingsService appSettings,
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
        var catalogTypes = await LoadCatalogTypesAsync(items, cancellationToken);
        var releaseGroups = await appSettings.GetCustomReleaseGroupsAsync(cancellationToken);
        return items
            .Select(item => IngestItemResponse.From(
                item, SourceFilesFor(item, sourceFilesByDownload), DownloadNameFor(item, downloadNames), MediaTitleFor(item, mediaTitles),
                nameParser, CatalogTypeFor(item, catalogTypes), releaseGroups))
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
        var mediaTitle = await ResolveMediaTitleAsync(item.MediaItemId, cancellationToken);
        var catalogType = await database.Catalogs.AsNoTracking()
            .Where(catalog => catalog.Id == item.CatalogId).Select(catalog => catalog.Type).FirstOrDefaultAsync(cancellationToken);
        var releaseGroups = await appSettings.GetCustomReleaseGroupsAsync(cancellationToken);
        return IngestItemResponse.From(item, sourceFiles, downloadName, mediaTitle, nameParser, catalogType, releaseGroups);
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
    /// Published rows have already handed off their download (DownloadId is null), so they are dropped in a
    /// single batch after noting any <c>.incoming/</c> staging to erase; the rare row that still holds a
    /// download falls back to the per-item <see cref="DeleteAsync"/>. Library files are left untouched.
    /// Returns how many rows were removed.
    /// </summary>
    public async Task<int> DeleteCompletedAsync(CancellationToken cancellationToken)
    {
        var items = await database.IngestItems
            .Where(item => item.Status == IngestStatus.Done)
            .ToListAsync(cancellationToken);
        if (items.Count == 0)
        {
            return 0;
        }

        var removed = 0;

        // A published item handed its download off long before reaching Done, so DownloadId is null. Should one
        // ever defy that, fall back to the single-item path so its torrent + files are torn down correctly.
        foreach (var item in items.Where(item => item.DownloadId is not null))
        {
            if (await DeleteAsync(item.Id, cancellationToken))
            {
                removed++;
            }
        }

        var batch = items.Where(item => item.DownloadId is null).ToList();
        if (batch.Count == 0)
        {
            return removed;
        }

        // Note any .incoming/ staging the batch still owns (paired with its catalog) before the rows — and
        // their cascading source files — are gone, then drop every row in one round-trip. Published library
        // files are left untouched.
        var ingestIds = batch.Select(item => item.Id).ToList();
        var catalogIds = batch.Select(item => item.CatalogId).Distinct().ToList();
        var catalogById = await database.Catalogs
            .Where(catalog => catalogIds.Contains(catalog.Id))
            .ToDictionaryAsync(catalog => catalog.Id, cancellationToken);
        var catalogByIngest = batch.ToDictionary(item => item.Id, item => item.CatalogId);
        var stagingDirs = (await database.SourceFiles
                .Where(file => ingestIds.Contains(file.IngestItemId))
                .Select(file => new { file.IngestItemId, file.RelativePath })
                .ToListAsync(cancellationToken))
            .Where(file => CatalogPaths.IsIncoming(file.RelativePath))
            .Select(file => (CatalogId: catalogByIngest[file.IngestItemId], Staging: StagingRootOf(file.RelativePath)))
            .Where(pair => pair.Staging is not null)
            .Distinct()
            .ToList();

        database.IngestItems.RemoveRange(batch); // SourceFile rows cascade away with them.
        await database.SaveChangesAsync(cancellationToken);
        removed += batch.Count;

        foreach (var (catalogId, staging) in stagingDirs)
        {
            if (catalogById.TryGetValue(catalogId, out var catalog) && sandbox.TryResolve(catalog, staging!, out var absolute))
            {
                TryDeleteDirectory(absolute);
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

    private async Task<Dictionary<Guid, CatalogType>> LoadCatalogTypesAsync(
        IReadOnlyList<IngestItem> items, CancellationToken cancellationToken)
    {
        var catalogIds = items.Select(item => item.CatalogId).Distinct().ToList();
        if (catalogIds.Count == 0)
        {
            return new Dictionary<Guid, CatalogType>();
        }

        return await database.Catalogs.AsNoTracking()
            .Where(catalog => catalogIds.Contains(catalog.Id))
            .ToDictionaryAsync(catalog => catalog.Id, catalog => catalog.Type, cancellationToken);
    }

    private static CatalogType CatalogTypeFor(IngestItem item, Dictionary<Guid, CatalogType> byCatalog) =>
        byCatalog.TryGetValue(item.CatalogId, out var type) ? type : CatalogType.Movie;

    private async Task<Dictionary<Guid, string>> LoadMediaTitlesAsync(
        IReadOnlyList<IngestItem> items, CancellationToken cancellationToken)
    {
        var mediaItemIds = items.Select(item => item.MediaItemId).OfType<Guid>().Distinct().ToList();
        if (mediaItemIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        // Published episodes carry a generic "Episode N" title; pull the parent series name in the same
        // query (a correlated subquery) so the ingest list shows e.g. "Breaking Bad · S01E05" instead of a
        // context-free "Episode 5", without a second round-trip.
        var media = await database.MediaItems.AsNoTracking()
            .Where(item => mediaItemIds.Contains(item.Id))
            .Select(item => new MediaTitleParts(
                item.Id, item.Kind, item.Title, item.ParentIndexNumber, item.IndexNumber,
                item.SeriesId == null
                    ? null
                    : database.MediaItems.Where(series => series.Id == item.SeriesId).Select(series => series.Title).FirstOrDefault()))
            .ToListAsync(cancellationToken);

        return media.ToDictionary(item => item.Id, ComposeTitle);
    }

    private async Task<string?> ResolveMediaTitleAsync(Guid? mediaItemId, CancellationToken cancellationToken)
    {
        if (mediaItemId is not { } id)
        {
            return null;
        }

        var media = await database.MediaItems.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new MediaTitleParts(
                item.Id, item.Kind, item.Title, item.ParentIndexNumber, item.IndexNumber,
                item.SeriesId == null
                    ? null
                    : database.MediaItems.Where(series => series.Id == item.SeriesId).Select(series => series.Title).FirstOrDefault()))
            .FirstOrDefaultAsync(cancellationToken);
        return media is null ? null : ComposeTitle(media);
    }

    private static string ComposeTitle(MediaTitleParts media) => media.Kind == MediaKind.Episode
        ? FormatEpisodeTitle(media.SeriesTitle, media.ParentIndexNumber, media.IndexNumber, media.Title)
        : media.Title;

    /// <summary>
    /// "Breaking Bad · S01E05". When the season/episode numbers are missing the episode's own title is used
    /// in their place ("Breaking Bad · Episode 5"); when the series can't be resolved either, just the
    /// episode title remains. Published episodes always carry both numbers, so the latter paths are fallbacks.
    /// </summary>
    private static string FormatEpisodeTitle(string? seriesTitle, int? season, int? episode, string fallback)
    {
        var episodeLabel = season is { } s && episode is { } e ? $"S{s:00}E{e:00}" : fallback;
        return string.IsNullOrWhiteSpace(seriesTitle) ? episodeLabel : $"{seriesTitle} · {episodeLabel}";
    }

    private static string? MediaTitleFor(IngestItem item, Dictionary<Guid, string> byMediaItem) =>
        item.MediaItemId is { } mediaItemId && byMediaItem.TryGetValue(mediaItemId, out var title) ? title : null;

    private sealed record MediaTitleParts(
        Guid Id, MediaKind Kind, string Title, int? ParentIndexNumber, int? IndexNumber, string? SeriesTitle);
}
