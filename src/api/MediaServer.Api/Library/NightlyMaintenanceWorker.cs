using MediaServer.Api.Catalogs;

namespace MediaServer.Api.Library;

/// <summary>
/// Runs the library's upkeep once a night, at a fixed off-peak hour: every catalog is scanned against
/// its disk, so files added or deleted out of band are picked up without anyone pressing a button, and
/// the titles the metadata provider says it edited are re-enriched.
/// </summary>
/// <remarks>
/// The hour is fixed rather than configurable — nobody has needed to move it, and a setting nobody
/// changes is a setting to maintain. It is local time on purpose: "three in the morning" means the
/// operator's night, not UTC's.
/// </remarks>
public sealed class NightlyMaintenanceWorker(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<NightlyMaintenanceWorker> logger)
    : BackgroundService
{
    /// <summary>Local time of day the nightly pass starts.</summary>
    public static readonly TimeOnly RunAt = new(3, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Recomputed every iteration from the current local time, so a clock change (daylight
                // saving, or the host being corrected) moves the next run rather than drifting forever.
                await Task.Delay(DelayUntilNextRun(time.GetLocalNow()), time, stoppingToken);
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    /// <summary>How long from <paramref name="now"/> until the next <see cref="RunAt"/>.</summary>
    internal static TimeSpan DelayUntilNextRun(DateTimeOffset now)
    {
        var todaysRun = new DateTimeOffset(now.Date, now.Offset) + RunAt.ToTimeSpan();
        var next = todaysRun > now ? todaysRun : todaysRun.AddDays(1);
        return next - now;
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // The scan goes first: it is what decides which titles the library still holds, and refreshing
        // metadata for one whose files went away last night would be provider calls spent on a ghost.
        await ScanAsync(cancellationToken);
        await RefreshChangedMetadataAsync(cancellationToken);
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var scan = scope.ServiceProvider.GetRequiredService<CatalogScanService>();
            var report = await scan.ScanAllAsync(cancellationToken);
            logger.LogInformation(
                "Nightly scan: {Catalogs} catalog(s) scanned ({Offline} offline), {Imported} file(s) imported, " +
                "{Missing} missing, {Ghosted} title(s) kept as removed, {Purged} purged.",
                report.CatalogsScanned, report.CatalogsOffline, report.Imported, report.MissingFiles,
                report.TitlesGhosted, report.TitlesPurged);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Nightly library scan failed.");
        }
    }

    private async Task RefreshChangedMetadataAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var refresh = scope.ServiceProvider.GetRequiredService<IncrementalMetadataRefreshService>();
            await refresh.RunAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Its own try: a provider that is down must not cost the scan, which touched nothing remote.
            logger.LogWarning(exception, "Nightly incremental metadata refresh failed.");
        }
    }
}
