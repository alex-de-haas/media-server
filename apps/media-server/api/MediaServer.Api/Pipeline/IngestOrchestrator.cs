using System.Text.Json;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Jobs;
using MediaServer.Api.Realtime;
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
        var sourceFiles = download is null
            ? new List<SourceFile>()
            : await database.SourceFiles.Where(file => file.DownloadId == download.Id).ToListAsync(cancellationToken);

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

        await FinishAsync(database, notifier, item, IngestStatus.Done, null, cancellationToken);
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
