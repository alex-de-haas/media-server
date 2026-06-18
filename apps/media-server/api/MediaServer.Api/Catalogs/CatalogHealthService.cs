using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Catalogs;

/// <summary>
/// Periodically checks every catalog root's reachability and free space, persisting transition markers
/// (<see cref="Catalog.OfflineSince"/>/<see cref="Catalog.LowDiskSince"/>) so the operator is notified
/// once when a root goes offline or runs low on disk — and once more when it recovers — rather than on
/// every tick. Notifications are deduped on Core too (see <see cref="IHostyCoreClient"/>). The M4 plan
/// keeps polling here because Core has no restore-time mount remap or directory webhooks.
/// </summary>
public sealed class CatalogHealthService(
    MediaServerDbContext database,
    IFilesystemInspector filesystem,
    IHostyCoreClient core,
    ILogger<CatalogHealthService> logger)
{
    /// <summary>Free-space floor below which the operator is warned (5 GiB).</summary>
    public const long LowDiskThresholdBytes = 5L * 1024 * 1024 * 1024;

    public async Task<int> CheckAsync(CancellationToken cancellationToken)
    {
        var catalogs = await database.Catalogs.ToListAsync(cancellationToken);
        var changed = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var catalog in catalogs)
        {
            var reachable = filesystem.DirectoryExists(catalog.Root);

            if (!reachable)
            {
                if (catalog.OfflineSince is null)
                {
                    catalog.OfflineSince = now;
                    changed++;
                    logger.LogWarning("Catalog {Catalog} ({Root}) is offline.", catalog.Name, catalog.Root);
                    await core.PublishNotificationAsync(
                        CoreNotificationLevel.Warning,
                        $"Catalog \"{catalog.Name}\" is offline",
                        $"The catalog root {catalog.Root} is unreachable. Downloads and streaming for this catalog are paused until it returns.",
                        link: null,
                        dedupeKey: $"media-server:catalog-offline:{catalog.Id}",
                        cancellationToken: cancellationToken);
                }

                continue; // Can't inspect free space on an unreachable volume.
            }

            if (catalog.OfflineSince is not null)
            {
                catalog.OfflineSince = null;
                changed++;
                logger.LogInformation("Catalog {Catalog} ({Root}) is back online.", catalog.Name, catalog.Root);
                await core.PublishNotificationAsync(
                    CoreNotificationLevel.Success,
                    $"Catalog \"{catalog.Name}\" is back online",
                    $"The catalog root {catalog.Root} is reachable again.",
                    link: null,
                    dedupeKey: $"media-server:catalog-online:{catalog.Id}",
                    cancellationToken: cancellationToken);
            }

            var freeBytes = filesystem.GetAvailableFreeBytes(catalog.Root);
            var lowDisk = freeBytes < LowDiskThresholdBytes;

            if (lowDisk && catalog.LowDiskSince is null)
            {
                catalog.LowDiskSince = now;
                changed++;
                logger.LogWarning("Catalog {Catalog} ({Root}) is low on disk: {FreeBytes} bytes free.", catalog.Name, catalog.Root, freeBytes);
                await core.PublishNotificationAsync(
                    CoreNotificationLevel.Warning,
                    $"Catalog \"{catalog.Name}\" is low on disk",
                    $"Only {FormatBytes(freeBytes)} free on {catalog.Root}. New downloads may fail.",
                    link: null,
                    dedupeKey: $"media-server:catalog-low-disk:{catalog.Id}",
                    cancellationToken: cancellationToken);
            }
            else if (!lowDisk && catalog.LowDiskSince is not null)
            {
                catalog.LowDiskSince = null;
                changed++;
                logger.LogInformation("Catalog {Catalog} ({Root}) disk recovered: {FreeBytes} bytes free.", catalog.Name, catalog.Root, freeBytes);
            }
        }

        if (changed > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}

/// <summary>Runs <see cref="CatalogHealthService.CheckAsync"/> on a timer (and once shortly after startup).</summary>
public sealed class CatalogHealthWorker(IServiceScopeFactory scopeFactory, ILogger<CatalogHealthWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
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
            var service = scope.ServiceProvider.GetRequiredService<CatalogHealthService>();
            await service.CheckAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Catalog health check failed.");
        }
    }
}
