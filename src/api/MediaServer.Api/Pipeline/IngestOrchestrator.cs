using System.Text.Json;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Jobs;
using MediaServer.Api.Realtime;
using MediaServer.Api.Torrents;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// Drives a single ingest item through the ordered stages. Claims the item with a lease, advances
/// <see cref="IngestItem.Stage"/>/<see cref="IngestItem.Status"/>, records <see cref="IngestItem.StagesCompleted"/>
/// for resume, applies backoff on <see cref="StageResult.Deferred"/>/retryable failures, and parks
/// <see cref="StageResult.NeedsReview"/> items. All driving is serialized through the pipeline worker.
/// </summary>
public sealed class IngestOrchestrator(IServiceScopeFactory scopeFactory, ILogger<IngestOrchestrator> logger)
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private readonly string _instanceId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    public async Task DriveAsync(Guid ingestItemId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var services = scope.ServiceProvider;
        var database = services.GetRequiredService<MediaServerDbContext>();
        var notifier = services.GetRequiredService<IRealtimeNotifier>();
        var jobService = services.GetRequiredService<JobService>();
        var stages = services.GetServices<IPipelineStage>().OrderBy(stage => stage.Order).ToList();

        var item = await database.IngestItems.FirstOrDefaultAsync(candidate => candidate.Id == ingestItemId, cancellationToken);
        if (item is null || item.Status is IngestStatus.Done or IngestStatus.NeedsReview)
        {
            return; // Nothing to do, or parked awaiting operator review.
        }

        if (!await TryClaimAsync(database, item, cancellationToken))
        {
            return;
        }

        var catalog = await database.Catalogs.FirstOrDefaultAsync(candidate => candidate.Id == item.CatalogId, cancellationToken);
        if (catalog is null)
        {
            await FinishAsync(database, notifier, item, IngestStatus.Failed, "Catalog no longer exists.", cancellationToken);
            return;
        }

        var download = item.DownloadId is { } downloadId
            ? await database.Downloads.FirstOrDefaultAsync(candidate => candidate.Id == downloadId, cancellationToken)
            : null;

        // Every ingest item is download-backed today. A null download means the source torrent was removed
        // after this item started (its DownloadId is set null on delete, its source files cascade away) —
        // a terminal condition, not a transient one. Fail it with a clear reason instead of letting the
        // Identify stage defer on an empty file list forever. See docs/planning/torrents-and-organizer.md.
        if (download is null)
        {
            await FinishAsync(database, notifier, item, IngestStatus.Failed,
                "The source download was removed before processing finished. Re-add the torrent to ingest it again.", cancellationToken);
            return;
        }

        var sourceFiles = await database.SourceFiles.Where(file => file.DownloadId == download.Id).ToListAsync(cancellationToken);

        var context = new IngestContext
        {
            Item = item,
            Catalog = catalog,
            Download = download,
            SourceFiles = sourceFiles,
            Paths = CatalogPaths.For(catalog),
        };

        item.Status = IngestStatus.Running;

        foreach (var stage in stages)
        {
            if (!stage.ShouldRun(context))
            {
                continue;
            }

            item.Stage = stage.Stage;
            await BroadcastAsync(notifier, item, cancellationToken);

            var job = await jobService.StartAsync($"stage:{stage.Key}", "ingest", item.Id, cancellationToken);
            StageResult result;
            try
            {
                result = await stage.RunAsync(context, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutdown, not a failure: leave the item Running with its lease; the reconciler
                // re-drives it after the lease expires on the next start.
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Stage {Stage} threw for ingest {IngestItem}.", stage.Key, item.Id);
                result = new StageResult.Failed(exception.Message, Retryable: true);
            }

            switch (result)
            {
                case StageResult.Completed:
                    if (!item.StagesCompleted.Contains(stage.Key))
                    {
                        item.StagesCompleted.Add(stage.Key);
                    }

                    item.LastError = null;
                    item.UpdatedAt = DateTimeOffset.UtcNow;
                    await database.SaveChangesAsync(cancellationToken);
                    await jobService.CompleteAsync(job, cancellationToken);
                    continue;

                case StageResult.Deferred deferred:
                    await jobService.CompleteAsync(job, cancellationToken);
                    await ParkAsync(database, notifier, item, IngestStatus.Pending, null, DateTimeOffset.UtcNow + deferred.RetryAfter, cancellationToken);
                    return;

                case StageResult.NeedsReview review:
                    item.ReviewCandidates = JsonSerializer.Serialize(review.Candidates);
                    await jobService.CompleteAsync(job, cancellationToken);
                    await ParkAsync(database, notifier, item, IngestStatus.NeedsReview, review.Reason, null, cancellationToken);
                    return;

                case StageResult.Failed failed:
                    item.AttemptCount++;
                    await jobService.FailAsync(job, failed.Error, cancellationToken);

                    if (failed.Retryable && item.AttemptCount < MaxAttempts)
                    {
                        await ParkAsync(database, notifier, item, IngestStatus.Pending, failed.Error, DateTimeOffset.UtcNow + Backoff(item.AttemptCount), cancellationToken);
                    }
                    else
                    {
                        await FinishAsync(database, notifier, item, IngestStatus.Failed, failed.Error, cancellationToken);
                    }

                    return;
            }
        }

        // All processing stages succeeded. The download backing is now disposable unless the torrent is
        // still seeding: reclaim the files/ seed copy and drop the Download + SourceFiles, leaving only the
        // published library item. A fresh read guards against a concurrent stop-seeding flipping the state.
        var teardownId = await ResolveTeardownAsync(database, download, cancellationToken);
        if (teardownId is not null)
        {
            item.DownloadId = null; // Detach the published record; FinishAsync persists it before teardown.
        }

        await FinishAsync(database, notifier, item, IngestStatus.Done, null, cancellationToken);

        if (teardownId is { } downloadToTeardown)
        {
            await TeardownDownloadBackingAsync(downloadToTeardown, cancellationToken);
        }
    }

    /// <summary>Returns the download id whose backing should be torn down now (content published and the
    /// torrent not seeding), or null to keep it. Reads the state fresh to catch a stop-seeding that flipped
    /// it after this drive began.</summary>
    private static async Task<Guid?> ResolveTeardownAsync(
        MediaServerDbContext database, Download? download, CancellationToken cancellationToken)
    {
        if (download is null)
        {
            return null;
        }

        var state = await database.Downloads.AsNoTracking()
            .Where(candidate => candidate.Id == download.Id)
            .Select(candidate => (DownloadState?)candidate.State)
            .FirstOrDefaultAsync(cancellationToken);

        return state is null or DownloadState.Seeding ? null : download.Id;
    }

    private async Task TeardownDownloadBackingAsync(Guid downloadId, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<DownloadCleanupService>()
                .TeardownAsync(downloadId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Best-effort housekeeping: the item is already published and detached. A failure leaves an
            // orphaned download row / files/ seed copy (reclaimable later) — never un-publish over it.
            logger.LogError(exception, "Failed to tear down download backing {DownloadId} after publish.", downloadId);
        }
    }

    private async Task<bool> TryClaimAsync(MediaServerDbContext database, IngestItem item, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (item.LeaseUntil is { } until && until > now && item.LeaseOwner != _instanceId)
        {
            return false; // Another worker holds the lease.
        }

        item.LeaseOwner = _instanceId;
        item.LeaseUntil = now + LeaseDuration;
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false; // Lost a concurrent claim race.
        }
    }

    private async Task ParkAsync(
        MediaServerDbContext database, IRealtimeNotifier notifier, IngestItem item,
        IngestStatus status, string? error, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken)
    {
        item.Status = status;
        item.LastError = error;
        item.NextAttemptAt = nextAttemptAt;
        item.LeaseOwner = null;
        item.LeaseUntil = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        await BroadcastAsync(notifier, item, cancellationToken);
    }

    private async Task FinishAsync(
        MediaServerDbContext database, IRealtimeNotifier notifier, IngestItem item,
        IngestStatus status, string? error, CancellationToken cancellationToken)
    {
        item.Status = status;
        item.LastError = error;
        item.NextAttemptAt = null;
        item.LeaseOwner = null;
        item.LeaseUntil = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        await BroadcastAsync(notifier, item, cancellationToken);

        if (status == IngestStatus.Done)
        {
            logger.LogInformation("Ingest item {IngestItem} published as media item {MediaItem}.", item.Id, item.MediaItemId);
        }
    }

    private static Task BroadcastAsync(IRealtimeNotifier notifier, IngestItem item, CancellationToken cancellationToken) =>
        notifier.IngestStageChangedAsync(new IngestStageChanged(
            item.Id, item.DownloadId, item.CatalogId, item.Stage.ToString(), item.Status.ToString(), item.LastError), cancellationToken);

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt) * 5, 300));
}
