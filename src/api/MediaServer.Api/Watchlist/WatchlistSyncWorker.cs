using MediaServer.Api.Data;
using MediaServer.Api.Jobs;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Watchlist;

/// <summary>
/// Drives the <c>watchlist:refresh-dates</c> loop: a full pass every 24h (and once shortly after
/// startup, filtered by staleness so restarts don't re-hit the provider), plus immediate single-title
/// syncs drained from <see cref="IWatchlistSyncQueue"/> (add / manual refresh / scope change). Full
/// passes are reported as observable jobs on the realtime feed.
/// </summary>
public sealed class WatchlistSyncWorker(
    IServiceScopeFactory scopeFactory,
    IWatchlistSyncQueue queue,
    ILogger<WatchlistSyncWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailOrphanedJobsAsync(stoppingToken);

        try
        {
            await Task.WhenAll(RunPeriodicAsync(stoppingToken), RunOnDemandAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunPeriodicAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            await RunFullPassAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunFullPassAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<WatchlistSyncService>();
            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();

            // Only open an observable job when there is actual work; an empty watchlist stays silent.
            if ((await service.ListStaleTitleIdsAsync(cancellationToken)).Count == 0)
            {
                return;
            }

            var job = await jobs.StartAsync(WatchlistSyncService.JobType, "watchlist", null, cancellationToken);
            try
            {
                await service.SyncAllAsync(job, cancellationToken);
                await jobs.CompleteAsync(job, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down — leave the row Running; the next start reconciles it to Failed.
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Watchlist date-sync pass failed.");
                await jobs.FailAsync(job, exception.Message, CancellationToken.None);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The filter keeps HttpClient-timeout cancellations (token not signalled) from killing the loop.
            logger.LogWarning(exception, "Watchlist date-sync pass failed to start.");
        }
    }

    private async Task RunOnDemandAsync(CancellationToken stoppingToken)
    {
        await foreach (var trackedTitleId in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<WatchlistSyncService>();
                // Forced: an on-demand sync (add / manual refresh) bypasses the settled-title skip.
                await service.SyncOneInScopeAsync(trackedTitleId, force: true, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "On-demand watchlist sync failed for title {Title}.", trackedTitleId);
            }
        }
    }

    /// <summary>
    /// A single app instance owns this loop, so any sync job still marked Running at startup was
    /// stranded by a restart. Mark it Failed so it doesn't show as forever-active.
    /// </summary>
    private async Task FailOrphanedJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();

            var orphaned = await database.Jobs
                .Where(job => job.Type == WatchlistSyncService.JobType && job.Status == JobStatus.Running)
                .ToListAsync(cancellationToken);
            foreach (var job in orphaned)
            {
                await jobs.FailAsync(job, "Interrupted by a restart.", cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to reconcile orphaned watchlist sync jobs on startup.");
        }
    }
}
