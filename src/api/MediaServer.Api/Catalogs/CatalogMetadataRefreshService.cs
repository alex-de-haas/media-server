using System.Collections.Concurrent;
using System.Threading.Channels;
using MediaServer.Api.Data;
using MediaServer.Api.Jobs;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Catalogs;

/// <summary>How many items a catalog refresh looked at and how they fared.</summary>
public sealed record CatalogRefreshReport(int Total, int Refreshed, int Failed);

/// <summary>A queued request to refresh metadata for one catalog, paired with its observable job.</summary>
public sealed record CatalogRefreshRequest(Guid CatalogId, Guid JobId);

/// <summary>The outcome of asking to start a refresh; drives the endpoint's status code.</summary>
public enum CatalogRefreshRequestStatus
{
    Started,
    AlreadyRunning,
    NotFound,
}

public sealed record CatalogRefreshRequestResult(CatalogRefreshRequestStatus Status, Guid? JobId);

/// <summary>The live state of one running catalog refresh, for the management UI.</summary>
public sealed record CatalogRefreshStatus(Guid CatalogId, Guid JobId, int Progress);

/// <summary>
/// In-process queue handing catalog ids to <see cref="CatalogRefreshWorker"/>. A single reader drains it,
/// so refreshes run one catalog at a time — which also paces provider traffic globally. An in-memory set of
/// active catalogs is the race-free source of truth for "a refresh is in flight" (one process owns it):
/// <see cref="TryReserve"/> admits a catalog atomically, <see cref="Release"/> frees it when the run ends.
/// </summary>
public interface ICatalogRefreshQueue
{
    /// <summary>Atomically reserves the catalog. False when a refresh is already queued or running for it.</summary>
    bool TryReserve(Guid catalogId);

    /// <summary>Frees a reservation once its run finishes (success, failure, or shutdown).</summary>
    void Release(Guid catalogId);

    void Enqueue(CatalogRefreshRequest request);

    IAsyncEnumerable<CatalogRefreshRequest> DequeueAllAsync(CancellationToken cancellationToken);
}

public sealed class CatalogRefreshQueue : ICatalogRefreshQueue
{
    private readonly Channel<CatalogRefreshRequest> _channel = Channel.CreateUnbounded<CatalogRefreshRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, byte> _active = new();

    public bool TryReserve(Guid catalogId) => _active.TryAdd(catalogId, 0);

    public void Release(Guid catalogId) => _active.TryRemove(catalogId, out _);

    public void Enqueue(CatalogRefreshRequest request) => _channel.Writer.TryWrite(request);

    public IAsyncEnumerable<CatalogRefreshRequest> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Validates and admits a catalog-wide metadata refresh: rejects an unknown catalog, refuses to start a
/// second run while one is already in flight, and otherwise opens a job and queues the work.
/// </summary>
public sealed class CatalogRefreshCoordinator(MediaServerDbContext database, JobService jobs, ICatalogRefreshQueue queue)
{
    public async Task<CatalogRefreshRequestResult> RequestAsync(Guid catalogId, CancellationToken cancellationToken)
    {
        var exists = await database.Catalogs.AsNoTracking().AnyAsync(catalog => catalog.Id == catalogId, cancellationToken);
        if (!exists)
        {
            return new CatalogRefreshRequestResult(CatalogRefreshRequestStatus.NotFound, null);
        }

        // Atomic admit: closes the check-then-start race between two concurrent requests for the same catalog
        // (a DB "is one running?" read could let both pass). The worker releases it when the run ends.
        if (!queue.TryReserve(catalogId))
        {
            return new CatalogRefreshRequestResult(CatalogRefreshRequestStatus.AlreadyRunning, null);
        }

        try
        {
            var job = await jobs.StartAsync(CatalogMetadataRefreshService.JobType, "catalog", catalogId, cancellationToken);
            queue.Enqueue(new CatalogRefreshRequest(catalogId, job.Id));
            return new CatalogRefreshRequestResult(CatalogRefreshRequestStatus.Started, job.Id);
        }
        catch
        {
            // Never leak the reservation if we couldn't open/queue the job.
            queue.Release(catalogId);
            throw;
        }
    }

    /// <summary>The catalogs with a refresh currently in flight, with their job id and progress.</summary>
    public async Task<IReadOnlyList<CatalogRefreshStatus>> ListActiveAsync(CancellationToken cancellationToken) =>
        await database.Jobs.AsNoTracking()
            .Where(job => job.Type == CatalogMetadataRefreshService.JobType && job.Status == JobStatus.Running && job.RelatedId != null)
            .Select(job => new CatalogRefreshStatus(job.RelatedId!.Value, job.Id, job.Progress))
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Re-runs the idempotent enrich step for every identified item in a catalog to pull fresh provider
/// metadata and images, reporting progress on the supplied <see cref="Job"/>. Each item is enriched in its
/// own DI scope so the EF change tracker stays bounded across a large catalog, and items are paced so a
/// refresh doesn't hammer the metadata provider. See <c>docs/features/metadata/feature.md</c>.
/// </summary>
public sealed class CatalogMetadataRefreshService(
    MediaServerDbContext database,
    IServiceScopeFactory scopeFactory,
    JobService jobs,
    ILogger<CatalogMetadataRefreshService> logger)
{
    public const string JobType = "catalog:refresh-metadata";

    // Enrich issues several provider calls per item (one per supported language, plus images), so pace
    // items to stay well under TMDb's rate limit rather than fetching in a tight loop.
    private static readonly TimeSpan ItemDelay = TimeSpan.FromMilliseconds(250);

    public async Task<CatalogRefreshReport> RunAsync(Guid catalogId, Job job, CancellationToken cancellationToken)
    {
        // Detached: the catalog is only read (for its metadata-language override) and reused across the
        // per-item scopes below; enrich never mutates it.
        var catalog = await database.Catalogs.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == catalogId, cancellationToken);
        if (catalog is null)
        {
            return new CatalogRefreshReport(0, 0, 0);
        }

        // Only identified items are refreshable (same gate as the per-item refresh); unidentified leaves
        // and container rows without an identity (e.g. seasons) have nothing authoritative to refresh from.
        var itemIds = await database.MediaItems.AsNoTracking()
            .Where(item => item.CatalogId == catalogId && item.IdentityProvider != null && item.IdentityProviderId != null)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);

        var total = itemIds.Count;
        var refreshed = 0;
        var failed = 0;
        var lastPercent = 0;

        for (var index = 0; index < total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (await EnrichOneAsync(catalog, itemIds[index], cancellationToken))
            {
                case EnrichOutcome.Refreshed:
                    refreshed++;
                    break;
                case EnrichOutcome.Failed:
                    failed++;
                    break;
                // Skipped (deleted mid-refresh): counts toward neither refreshed nor failed.
            }

            // Persist/broadcast only when the whole-number percent advances, so a big catalog emits at most
            // ~100 progress events rather than one per item.
            var percent = (int)((index + 1) * 100L / total);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                await jobs.ProgressAsync(job, percent, cancellationToken);
            }

            if (index < total - 1)
            {
                await Task.Delay(ItemDelay, cancellationToken);
            }
        }

        logger.LogInformation(
            "Catalog {Catalog} metadata refresh: {Refreshed}/{Total} refreshed, {Failed} failed.",
            catalogId, refreshed, total, failed);
        return new CatalogRefreshReport(total, refreshed, failed);
    }

    private async Task<EnrichOutcome> EnrichOneAsync(Catalog catalog, Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            // A fresh scope per item: enrich tracks the item plus its metadata/images/credits, so isolating
            // each keeps the change tracker (and SaveChanges cost) from growing with the catalog size.
            using var scope = scopeFactory.CreateScope();
            var scopedDatabase = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var enrich = scope.ServiceProvider.GetRequiredService<EnrichService>();

            var item = await scopedDatabase.MediaItems.FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
            if (item is null)
            {
                return EnrichOutcome.Skipped; // Deleted between selection and enrich — neither refreshed nor failed.
            }

            await enrich.EnrichAsync(catalog, item, cancellationToken);
            return EnrichOutcome.Refreshed;
        }
        // Catch any per-item failure — including a provider timeout, which surfaces as a (Task)Canceled with
        // our token un-cancelled — but let a genuine caller-requested cancellation propagate to stop the run.
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Catalog refresh: failed to enrich item {Item}.", itemId);
            return EnrichOutcome.Failed;
        }
    }

    private enum EnrichOutcome
    {
        Refreshed,
        Failed,
        Skipped,
    }
}

/// <summary>Drains <see cref="ICatalogRefreshQueue"/>, running one catalog refresh at a time.</summary>
public sealed class CatalogRefreshWorker(IServiceScopeFactory scopeFactory, ICatalogRefreshQueue queue, ILogger<CatalogRefreshWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailOrphanedJobsAsync(stoppingToken);

        try
        {
            await foreach (var request in queue.DequeueAllAsync(stoppingToken))
            {
                await ProcessAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ProcessAsync(CatalogRefreshRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
            var service = scope.ServiceProvider.GetRequiredService<CatalogMetadataRefreshService>();

            var job = await database.Jobs.FirstOrDefaultAsync(candidate => candidate.Id == request.JobId, cancellationToken);
            if (job is null)
            {
                return; // The job row vanished (shouldn't happen) — nothing to drive.
            }

            try
            {
                await service.RunAsync(request.CatalogId, job, cancellationToken);
                await jobs.CompleteAsync(job, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down — leave the row Running; the next start reconciles it to Failed.
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Catalog {Catalog} metadata refresh failed.", request.CatalogId);
                await jobs.FailAsync(job, exception.Message, CancellationToken.None);
            }
        }
        finally
        {
            // Free the reservation on every path (done, failed, vanished, or shutdown) so a later refresh can run.
            queue.Release(request.CatalogId);
        }
    }

    /// <summary>
    /// A single app instance owns this queue, so any refresh job still marked Running at startup was
    /// stranded by a restart. Mark it Failed so it doesn't show as forever-active and a new run can start.
    /// </summary>
    private async Task FailOrphanedJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();

            var orphaned = await database.Jobs
                .Where(job => job.Type == CatalogMetadataRefreshService.JobType && job.Status == JobStatus.Running)
                .ToListAsync(cancellationToken);
            foreach (var job in orphaned)
            {
                await jobs.FailAsync(job, "Interrupted by a restart.", cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to reconcile orphaned catalog refresh jobs on startup.");
        }
    }
}
