using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline;

/// <summary>Drains the pipeline queue and drives one ingest item at a time (serialized single-reader).</summary>
public sealed class PipelineWorker(IPipelineQueue queue, IngestOrchestrator orchestrator, ILogger<PipelineWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var ingestItemId in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                await orchestrator.DriveAsync(ingestItemId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unhandled error driving ingest item {IngestItem}.", ingestItemId);
            }
        }
    }
}

/// <summary>
/// Re-drives non-terminal ingest items: enqueues everything outstanding on startup (resume) and, on a
/// timer, re-enqueues items whose backoff has elapsed or whose lease has expired after a crash. See
/// <c>docs/planning/background-tasks.md</c>.
/// </summary>
public sealed class ReconcilerWorker(IServiceScopeFactory scopeFactory, IPipelineQueue queue, ILogger<ReconcilerWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial resume pass after a (re)start.
        await ReconcileAsync(stoppingToken);

        try
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ReconcileAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var now = DateTimeOffset.UtcNow;

            // Status filter and due-time comparison both run in SQL: the DateTimeOffset value converter
            // stores a sortable UTC string, so SQLite can compare NextAttemptAt/LeaseUntil directly.
            var dueIds = await database.IngestItems
                .Where(item => (item.Status == IngestStatus.Pending || item.Status == IngestStatus.Running)
                               && (item.NextAttemptAt == null || item.NextAttemptAt <= now)
                               && (item.LeaseUntil == null || item.LeaseUntil <= now))
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);

            foreach (var id in dueIds)
            {
                queue.Enqueue(id);
            }

            if (dueIds.Count > 0)
            {
                logger.LogDebug("Reconciler re-queued {Count} ingest item(s).", dueIds.Count);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Reconciler pass failed.");
        }
    }
}
