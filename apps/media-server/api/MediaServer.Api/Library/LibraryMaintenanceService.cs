using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>Result of a library scan pass: how much was checked and which library files are missing on disk.</summary>
public sealed record LibraryScanReport(int CatalogsScanned, int SourcesChecked, int MissingFiles, IReadOnlyList<string> MissingPaths);

/// <summary>
/// M4 automation polish: on-demand and scheduled library maintenance. The scan verifies every published
/// <see cref="MediaSource"/> still resolves to a file on disk (drift from out-of-band deletes), skipping
/// offline catalogs so an unmounted volume isn't reported as missing media. Metadata refresh re-runs the
/// idempotent enrich step for a single item to pull fresh provider data and images.
/// </summary>
public sealed class LibraryMaintenanceService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    IFilesystemInspector filesystem,
    EnrichService enrichService,
    IHostyCoreClient core,
    ILogger<LibraryMaintenanceService> logger)
{
    private const int MaxMissingReported = 50;

    /// <summary>Re-fetches provider metadata + images for one published item. False when it isn't refreshable.</summary>
    public async Task<bool> RefreshMetadataAsync(Guid mediaItemId, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.FirstOrDefaultAsync(candidate => candidate.Id == mediaItemId, cancellationToken);
        if (item is null || item.IdentityProvider is null || item.IdentityProviderId is null)
        {
            return false; // Unknown, or never identified — nothing authoritative to refresh from.
        }

        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == item.CatalogId, cancellationToken);
        if (catalog is null)
        {
            return false;
        }

        await enrichService.EnrichAsync(catalog, item, cancellationToken);
        logger.LogInformation("Refreshed metadata for media item {MediaItem}.", item.Id);
        return true;
    }

    public async Task<LibraryScanReport> ScanAsync(CancellationToken cancellationToken)
    {
        var catalogsById = await database.Catalogs.AsNoTracking().ToDictionaryAsync(catalog => catalog.Id, cancellationToken);
        var sources = await database.MediaSources.AsNoTracking()
            .Include(source => source.MediaItem)
            .ToListAsync(cancellationToken);

        var scannedCatalogs = new HashSet<Guid>();
        var checkedCount = 0;
        var missing = new List<string>();

        foreach (var source in sources)
        {
            if (source.MediaItem is null || !catalogsById.TryGetValue(source.MediaItem.CatalogId, out var catalog))
            {
                continue;
            }

            // An offline root means the volume is unmounted, not that the media vanished — don't scan it.
            if (!filesystem.DirectoryExists(catalog.Root))
            {
                continue;
            }

            scannedCatalogs.Add(catalog.Id);
            checkedCount++;

            var exists = sandbox.TryResolve(catalog, source.Path, out var absolute) && File.Exists(absolute);
            if (!exists)
            {
                missing.Add(source.Path);
            }
        }

        if (missing.Count > 0)
        {
            logger.LogWarning("Library scan found {Count} missing source file(s).", missing.Count);
            await core.PublishNotificationAsync(
                CoreNotificationLevel.Warning,
                "Media Server: missing library files",
                $"{missing.Count} library file(s) are missing from disk. The affected items may not play until they are re-downloaded.",
                link: null,
                dedupeKey: "media-server:library-missing",
                cancellationToken: cancellationToken);
        }

        return new LibraryScanReport(scannedCatalogs.Count, checkedCount, missing.Count, missing.Take(MaxMissingReported).ToList());
    }
}

/// <summary>Runs <see cref="LibraryMaintenanceService.ScanAsync"/> on a timer to catch out-of-band file drift.</summary>
public sealed class LibraryScanWorker(IServiceScopeFactory scopeFactory, ILogger<LibraryScanWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            await RunOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<LibraryMaintenanceService>();
            var report = await service.ScanAsync(cancellationToken);
            logger.LogInformation(
                "Library scan: {Sources} source(s) across {Catalogs} catalog(s), {Missing} missing.",
                report.SourcesChecked, report.CatalogsScanned, report.MissingFiles);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Scheduled library scan failed.");
        }
    }
}
