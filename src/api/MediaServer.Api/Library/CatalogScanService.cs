using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>What one catalog's scan found and what it did about it.</summary>
/// <param name="Offline">
/// The catalog's storage could not be read at all. Nothing was imported or removed — the numbers below
/// are zero because the scan declined to act, not because there was nothing to do.
/// </param>
/// <param name="VersionsRemoved">Gone files whose item kept at least one other version and survived.</param>
/// <param name="TitlesGhosted">Titles that left the library but kept their history as tombstones.</param>
/// <param name="TitlesPurged">Titles nobody had watched, rated or favorited, deleted outright.</param>
public sealed record CatalogScanReport(
    Guid CatalogId,
    string CatalogName,
    bool Offline,
    int FilesScanned,
    int Imported,
    int Skipped,
    int SourcesChecked,
    int MissingFiles,
    int VersionsRemoved,
    int SidecarsRemoved,
    int TitlesGhosted,
    int TitlesPurged,
    IReadOnlyList<string> MissingPaths);

/// <summary>Every catalog scanned in one pass, with the totals a caller reports.</summary>
public sealed record LibraryScanReport(IReadOnlyList<CatalogScanReport> Catalogs)
{
    public int CatalogsScanned => Catalogs.Count(report => !report.Offline);

    public int CatalogsOffline => Catalogs.Count(report => report.Offline);

    public int Imported => Catalogs.Sum(report => report.Imported);

    public int SourcesChecked => Catalogs.Sum(report => report.SourcesChecked);

    public int MissingFiles => Catalogs.Sum(report => report.MissingFiles);

    public int VersionsRemoved => Catalogs.Sum(report => report.VersionsRemoved);

    public int SidecarsRemoved => Catalogs.Sum(report => report.SidecarsRemoved);

    public int TitlesGhosted => Catalogs.Sum(report => report.TitlesGhosted);

    public int TitlesPurged => Catalogs.Sum(report => report.TitlesPurged);
}

/// <summary>
/// Syncs a catalog with its disk, in both directions: media the library does not know about is imported
/// (see <see cref="LibraryImportService"/>), and library rows whose files are gone are removed — as
/// tombstones where some user watched, rated or favorited the title, and outright where nobody did.
/// </summary>
/// <remarks>
/// The removal half is why this runs behind a mount check rather than a file-by-file one. A catalog sits
/// on one mount, so its files are all present or all absent together: when <b>none</b> of them can be
/// read the volume is gone and the scan declines to act, and when <b>any</b> of them can the volume is
/// there and the rest really were deleted. Acting on the second case is safe in a way it could not be
/// for files: a scan never erases anything from disk, so a wrong call costs a metadata refetch — the
/// file's return is picked up by the next scan, and identification adopts the tombstone back under the
/// same public id, with its history intact.
/// </remarks>
/// <summary>What a scan request did, distinguishing the two ways it can decline.</summary>
/// <remarks>
/// "No such catalog" and "one is already running" are different answers, and a caller that cannot
/// tell them apart reports a busy catalog as a missing one.
/// </remarks>
public sealed record CatalogScanOutcome(CatalogScanReport? Report, bool NotFound = false, bool AlreadyRunning = false);

public sealed class CatalogScanService(
    MediaServerDbContext database,
    CatalogFileProbe probe,
    IFilesystemInspector filesystem,
    CatalogHealthService health,
    LibraryImportService importService,
    LibraryDeleteService deleteService,
    IHostyCoreClient core,
    ICatalogScanQueue scanQueue,
    ILogger<CatalogScanService> logger)
{
    private const int MaxMissingReported = 50;

    /// <summary>Scans one catalog, unless one is already running for it.</summary>
    public async Task<CatalogScanOutcome> ScanAsync(Guid catalogId, CancellationToken cancellationToken)
    {
        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == catalogId, cancellationToken);
        if (catalog is null)
        {
            return new CatalogScanOutcome(null, NotFound: true);
        }

        return await ReserveAndScanAsync(catalog, cancellationToken);
    }

    /// <summary>
    /// Takes the catalog's scan reservation, runs the scan, and gives it back.
    /// </summary>
    /// <remarks>
    /// Here rather than in the coordinator that queues MCP requests, because this is the one point every
    /// entry point funnels through — the queued worker, the synchronous route the web UI calls, and the
    /// nightly maintenance job. A guard held one level up protected only the path that went through it,
    /// so two disk walks over the same catalog remained possible, which is precisely what the guard was
    /// advertised to prevent.
    /// </remarks>
    private async Task<CatalogScanOutcome> ReserveAndScanAsync(Catalog catalog, CancellationToken cancellationToken)
    {
        if (!scanQueue.TryReserve(catalog.Id))
        {
            return new CatalogScanOutcome(null, AlreadyRunning: true);
        }

        try
        {
            return new CatalogScanOutcome(await ScanCatalogAsync(catalog, cancellationToken));
        }
        finally
        {
            scanQueue.Release(catalog.Id);
        }
    }

    /// <summary>Scans every catalog, one after another — the global button and the nightly job.</summary>
    public async Task<LibraryScanReport> ScanAllAsync(CancellationToken cancellationToken)
    {
        var catalogs = await database.Catalogs.OrderBy(catalog => catalog.Name).ToListAsync(cancellationToken);
        var reports = new List<CatalogScanReport>(catalogs.Count);
        foreach (var catalog in catalogs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A catalog already being scanned is skipped rather than queued behind: the caller asked for
            // the library, and a run already under way is the outcome they wanted.
            var outcome = await ReserveAndScanAsync(catalog, cancellationToken);
            if (outcome.Report is { } report)
            {
                reports.Add(report);
            }
        }

        return new LibraryScanReport(reports);
    }

    private async Task<CatalogScanReport> ScanCatalogAsync(Catalog catalog, CancellationToken cancellationToken)
    {
        // The cheapest evidence first: with no root directory there is nothing a file check could add.
        if (!filesystem.DirectoryExists(catalog.Root))
        {
            await GoOfflineAsync(catalog, cancellationToken);
            // Deliberately not stamped as scanned: a volume that could not be read was not scanned, and
            // recording otherwise turns "the disk is missing" into "the library is empty".
            return OfflineReport(catalog);
        }

        var sources = await database.MediaSources.AsNoTracking()
            .Where(source => source.MediaItem!.CatalogId == catalog.Id && source.MediaItem.PublicId != null)
            .Select(source => new { source.Id, source.Path, source.MediaItemId })
            .ToListAsync(cancellationToken);

        var missing = sources.Where(source => !probe.Resolves(catalog, source.Path)).ToList();

        // The mount rule. A catalog that has files and cannot read one of them is a volume that went
        // away — an empty bind mount reads exactly like this, and its root exists all the while.
        if (sources.Count > 0 && missing.Count == sources.Count)
        {
            logger.LogWarning(
                "Catalog {Catalog}: none of its {Count} library file(s) can be read — treating it as offline rather than as {Count} deletions.",
                catalog.Name, sources.Count);
            await GoOfflineAsync(catalog, cancellationToken);
            return OfflineReport(catalog);
        }

        // The volume answered, so anything still marked offline has recovered — say so before importing,
        // or the operator watches a catalog they were told is offline quietly grow.
        if (catalog.OfflineSince is not null)
        {
            await health.MarkOnlineAsync(catalog, cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
        }

        var imported = await importService.ImportAsync(catalog.Id, cancellationToken)
            ?? new LibraryImportReport(0, 0, 0);

        var removal = await RemoveVanishedAsync(catalog, sources.Select(source => source.MediaItemId).Distinct().ToList(),
            missing.Select(source => (source.Id, source.Path, source.MediaItemId)).ToList(), cancellationToken);

        var report = new CatalogScanReport(
            catalog.Id,
            catalog.Name,
            Offline: false,
            imported.FilesScanned,
            imported.Imported,
            imported.Skipped,
            sources.Count,
            missing.Count,
            removal.VersionsRemoved,
            removal.SidecarsRemoved,
            removal.TitlesGhosted,
            removal.TitlesPurged,
            missing.Select(source => source.Path).Take(MaxMissingReported).ToList());

        // Stamped here, at the one point every entry point funnels through — the queued worker, the
        // synchronous route, and the nightly job all reach this line. Recording it anywhere upstream
        // would leave the paths that skip that upstream reporting a catalog as never scanned.
        catalog.LastScannedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);

        await AnnounceAsync(report, cancellationToken);
        return report;
    }

    /// <summary>
    /// Removes what the disk no longer backs: a gone file drops its version, an item whose every version
    /// is gone goes through the delete pipeline (tombstone or purge, by user signal), and a gone sidecar
    /// beside a surviving file drops its track.
    /// </summary>
    private async Task<RemovalCounts> RemoveVanishedAsync(
        Catalog catalog,
        IReadOnlyList<Guid> itemIds,
        IReadOnlyList<(Guid Id, string Path, Guid MediaItemId)> missing,
        CancellationToken cancellationToken)
    {
        var missingIds = missing.Select(source => source.Id).ToHashSet();
        var survivingSources = await database.MediaSources.AsNoTracking()
            .Where(source => itemIds.Contains(source.MediaItemId) && !missingIds.Contains(source.Id))
            .Select(source => new { source.Id, source.MediaItemId })
            .ToListAsync(cancellationToken);
        var keepers = survivingSources.Select(source => source.MediaItemId).ToHashSet();
        var survivingSourceIds = survivingSources.Select(source => source.Id).ToList();

        // Sidecars are only this pass's business where the file they sit beside survived: one hanging off
        // a version that is about to go leaves with it, and one under a vanished item leaves with the item.
        var sidecars = await database.MediaStreams.AsNoTracking()
            .Where(stream => stream.IsExternal && stream.ExternalPath != null &&
                survivingSourceIds.Contains(stream.MediaSourceId))
            .Select(stream => new { stream.Id, stream.ExternalPath })
            .ToListAsync(cancellationToken);

        var vanishedItemIds = missing.Select(source => source.MediaItemId).Distinct()
            .Where(id => !keepers.Contains(id))
            .ToList();

        // The top-level works those leaves belong to — what the operator calls "a title" — captured
        // before the removal, because afterwards some of the rows naming them are gone.
        var works = await database.MediaItems.AsNoTracking()
            .Where(item => vanishedItemIds.Contains(item.Id))
            .Select(item => new { item.Id, item.Kind, item.SeriesId, item.ParentId })
            .ToListAsync(cancellationToken);
        var workIds = works
            .Select(item => item.Kind == MediaKind.Movie ? item.Id : item.SeriesId ?? item.ParentId ?? item.Id)
            .Distinct()
            .ToList();

        foreach (var id in vanishedItemIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await deleteService.RemoveVanishedAsync(id, cancellationToken);
        }

        var versionsRemoved = 0;
        foreach (var source in missing.Where(source => keepers.Contains(source.MediaItemId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await deleteService.DeleteSourceAsync(source.Id, deleteFile: false, cancellationToken))
            {
                versionsRemoved++;
                logger.LogInformation(
                    "Catalog {Catalog}: version '{Path}' is gone from disk; the item keeps its other versions.",
                    catalog.Name, source.Path);
            }
        }

        var sidecarsRemoved = 0;
        foreach (var sidecar in sidecars.Where(sidecar => !probe.Resolves(catalog, sidecar.ExternalPath!)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await deleteService.DeleteExternalStreamAsync(sidecar.Id, deleteFile: false, cancellationToken))
            {
                sidecarsRemoved++;
            }
        }

        var survivors = await database.MediaItems.AsNoTracking()
            .Where(item => workIds.Contains(item.Id))
            .Select(item => new { item.Id, item.RemovedAt })
            .ToListAsync(cancellationToken);

        return new RemovalCounts(
            versionsRemoved,
            sidecarsRemoved,
            survivors.Count(work => work.RemovedAt != null),
            workIds.Count - survivors.Count);
    }

    private async Task GoOfflineAsync(Catalog catalog, CancellationToken cancellationToken)
    {
        if (await health.MarkOfflineAsync(catalog, DateTimeOffset.UtcNow, cancellationToken))
        {
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    private CatalogScanReport OfflineReport(Catalog catalog) =>
        new(catalog.Id, catalog.Name, Offline: true, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);

    /// <summary>
    /// Tells the operator what the scan did to their library. A nightly job that unpublishes a film
    /// without a word reads as data loss the next morning, so the notification names the outcome rather
    /// than the symptom — how many titles kept their history, and how many were deleted.
    /// </summary>
    private async Task AnnounceAsync(CatalogScanReport report, CancellationToken cancellationToken)
    {
        if (report.MissingFiles == 0)
        {
            return;
        }

        var outcome = report switch
        {
            { TitlesGhosted: 0, TitlesPurged: 0 } =>
                $"{report.VersionsRemoved + report.SidecarsRemoved} version(s) were removed; every title kept another file.",
            { TitlesPurged: 0 } =>
                $"{report.TitlesGhosted} title(s) left the library but kept their watch history.",
            { TitlesGhosted: 0 } =>
                $"{report.TitlesPurged} title(s) nobody had watched were deleted.",
            _ =>
                $"{report.TitlesGhosted} title(s) left the library but kept their watch history; " +
                $"{report.TitlesPurged} nobody had watched were deleted.",
        };

        logger.LogWarning(
            "Catalog {Catalog}: {Missing} library file(s) are gone from disk. {Outcome}",
            report.CatalogName, report.MissingFiles, outcome);

        await core.PublishNotificationAsync(
            CoreNotificationLevel.Warning,
            $"Media Server: files missing in \"{report.CatalogName}\"",
            $"{report.MissingFiles} library file(s) are gone from disk. {outcome}",
            link: null,
            dedupeKey: $"media-server:catalog-scan:{report.CatalogId}",
            cancellationToken: cancellationToken);
    }

    private sealed record RemovalCounts(int VersionsRemoved, int SidecarsRemoved, int TitlesGhosted, int TitlesPurged);
}
