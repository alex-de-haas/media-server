using System.Collections.Concurrent;
using System.Threading.Channels;
using MediaServer.Api.Data;
using MediaServer.Api.Jobs;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Catalogs;

/// <summary>One queued catalog scan, and the job row that reports it.</summary>
public sealed record CatalogScanRequest(Guid CatalogId, Guid JobId);

public enum CatalogScanRequestStatus
{
    Started,
    AlreadyRunning,
    NotFound,
}

public sealed record CatalogScanRequestResult(CatalogScanRequestStatus Status, Guid? JobId);

/// <summary>A catalog currently being scanned, with the job driving it.</summary>
public sealed record CatalogScanStatus(Guid CatalogId, Guid JobId, int Progress);

/// <summary>When a catalog was last scanned, and whether one is running now.</summary>
/// <remarks>
/// Read from the job rows rather than a column on the catalog. Those rows already record what
/// happened and when, are never pruned, and a second source would be a second thing to keep in step —
/// which is how "last scanned" ends up disagreeing with the scan that actually ran.
/// </remarks>
public sealed record CatalogScanState(Guid CatalogId, bool Scanning, DateTimeOffset? LastCompletedAt)
{
    /// <summary>True when nothing has ever finished scanning this catalog.</summary>
    /// <remarks>
    /// The distinction an empty search result depends on: "nothing matched" and "nothing has been
    /// looked at" are different answers, and only one of them is about the library.
    /// </remarks>
    public bool NeverScanned => LastCompletedAt is null && !Scanning;
}

public interface ICatalogScanQueue
{
    /// <summary>Atomically reserves the catalog. False when a scan is already queued or running for it.</summary>
    bool TryReserve(Guid catalogId);

    /// <summary>Whether a scan currently holds this catalog, through any entry point that reserves.</summary>
    bool IsReserved(Guid catalogId);

    /// <summary>Frees a reservation once its run finishes (success, failure, or shutdown).</summary>
    void Release(Guid catalogId);

    void Enqueue(CatalogScanRequest request);

    IAsyncEnumerable<CatalogScanRequest> DequeueAllAsync(CancellationToken cancellationToken);
}

public sealed class CatalogScanQueue : ICatalogScanQueue
{
    private readonly Channel<CatalogScanRequest> _channel = Channel.CreateUnbounded<CatalogScanRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, byte> _active = new();

    public bool TryReserve(Guid catalogId) => _active.TryAdd(catalogId, 0);

    public bool IsReserved(Guid catalogId) => _active.ContainsKey(catalogId);

    public void Release(Guid catalogId) => _active.TryRemove(catalogId, out _);

    public void Enqueue(CatalogScanRequest request) => _channel.Writer.TryWrite(request);

    public IAsyncEnumerable<CatalogScanRequest> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Admits a catalog scan without waiting for it: rejects an unknown catalog, refuses a second run
/// while one is in flight, and otherwise opens a job and queues the work.
/// </summary>
/// <remarks>
/// The scan endpoint awaits <see cref="CatalogScanService.ScanAsync"/> and answers when it finishes,
/// which is the right shape for a UI that wants the report and the wrong one for anything with a
/// timeout: a large catalog holds the request open for as long as the disk walk takes, and the caller
/// gets a failure for work that is still running. Deliberately a copy of the metadata-refresh
/// coordinator rather than a shared generalization — the two agree today by coincidence of shape, and
/// the refresh paces a provider where this walks a disk.
/// </remarks>
public sealed class CatalogScanCoordinator(MediaServerDbContext database, JobService jobs, ICatalogScanQueue queue)
{
    public const string JobType = "catalog-scan";

    public async Task<CatalogScanRequestResult> RequestAsync(Guid catalogId, CancellationToken cancellationToken)
    {
        var exists = await database.Catalogs.AsNoTracking().AnyAsync(catalog => catalog.Id == catalogId, cancellationToken);
        if (!exists)
        {
            return new CatalogScanRequestResult(CatalogScanRequestStatus.NotFound, null);
        }

        // A check, not a claim. The reservation is taken by the scan itself, which is the only place
        // every entry point passes through; refusing here saves opening a job that would find the
        // catalog busy, and losing the race afterwards is handled where the scan runs.
        if (queue.IsReserved(catalogId))
        {
            return new CatalogScanRequestResult(CatalogScanRequestStatus.AlreadyRunning, null);
        }

        var job = await jobs.StartAsync(JobType, "catalog", catalogId, cancellationToken);
        queue.Enqueue(new CatalogScanRequest(catalogId, job.Id));
        return new CatalogScanRequestResult(CatalogScanRequestStatus.Started, job.Id);
    }

    /// <summary>
    /// Queues a scan for every catalog. Catalogs already scanning are skipped rather than refused: the
    /// operator asked for the library, and a run already under way is the outcome they wanted.
    /// </summary>
    public async Task<int> RequestAllAsync(CancellationToken cancellationToken)
    {
        var catalogIds = await database.Catalogs.AsNoTracking()
            .OrderBy(catalog => catalog.Name)
            .Select(catalog => catalog.Id)
            .ToListAsync(cancellationToken);

        var started = 0;
        foreach (var catalogId in catalogIds)
        {
            if ((await RequestAsync(catalogId, cancellationToken)).Status == CatalogScanRequestStatus.Started)
            {
                started++;
            }
        }

        return started;
    }

    /// <summary>The catalogs with a scan in flight, with their job id and progress.</summary>
    public async Task<IReadOnlyList<CatalogScanStatus>> ListActiveAsync(CancellationToken cancellationToken) =>
        await database.Jobs.AsNoTracking()
            .Where(job => job.Type == JobType && job.Status == JobStatus.Running && job.RelatedId != null)
            .Select(job => new CatalogScanStatus(job.RelatedId!.Value, job.Id, job.Progress))
            .ToListAsync(cancellationToken);

    /// <summary>Scan state for every catalog, including the ones nothing has ever scanned.</summary>
    /// <remarks>
    /// "Last scanned" is read from the catalog, not from the job rows. The synchronous route and the
    /// nightly maintenance job scan without opening a job at all, so reading jobs reported a catalog
    /// scanned nightly for months as never scanned — and an empty search result then says "nothing has
    /// looked at this" about a library that is fully read. Jobs still answer "scanning now", which is
    /// what they are: a record of a run this coordinator started.
    /// </remarks>
    public async Task<IReadOnlyList<CatalogScanState>> ListStateAsync(CancellationToken cancellationToken)
    {
        var catalogs = await database.Catalogs.AsNoTracking()
            .Select(catalog => new { catalog.Id, catalog.LastScannedAt })
            .ToListAsync(cancellationToken);
        var running = await database.Jobs.AsNoTracking()
            .Where(job => job.Type == JobType && job.Status == JobStatus.Running && job.RelatedId != null)
            .Select(job => job.RelatedId!.Value)
            .ToListAsync(cancellationToken);

        return [.. catalogs.Select(catalog => new CatalogScanState(
            catalog.Id,
            // A scan started through the synchronous route is not a job, so it does not show here. The
            // reservation below is what stops two running at once; this reports the ones with a job.
            running.Contains(catalog.Id) || queue.IsReserved(catalog.Id),
            catalog.LastScannedAt))];
    }
}

/// <summary>Runs queued catalog scans one at a time.</summary>
public sealed class CatalogScanWorker(
    IServiceScopeFactory scopeFactory, ICatalogScanQueue queue, ILogger<CatalogScanWorker> logger)
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

    private async Task ProcessAsync(CatalogScanRequest request, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
            var scan = scope.ServiceProvider.GetRequiredService<CatalogScanService>();

            var job = await database.Jobs.FirstOrDefaultAsync(candidate => candidate.Id == request.JobId, cancellationToken);
            if (job is null)
            {
                return;
            }

            try
            {
                var outcome = await scan.ScanAsync(request.CatalogId, cancellationToken);
                if (outcome.AlreadyRunning)
                {
                    // Something else took the catalog between admit and here — the synchronous route or
                    // the nightly job. Completed rather than failed: the scan the caller wanted is
                    // happening, just not on this job.
                    logger.LogInformation(
                        "Catalog {Catalog} was already being scanned; the queued run was skipped.", request.CatalogId);
                }

                await jobs.CompleteAsync(job, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down — the row stays Running and the next start reconciles it to Failed.
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Catalog {Catalog} scan failed.", request.CatalogId);
                await jobs.FailAsync(job, exception.Message, CancellationToken.None);
            }
        }
        finally
        {
            // Nothing to release here any more: the scan service takes and gives back the reservation,
            // so a release here would free a hold this job never took.
        }
    }

    /// <summary>
    /// One instance owns this queue, so a scan job still Running at startup was stranded by a restart.
    /// Marked Failed so it neither shows as forever-active nor blocks the next run.
    /// </summary>
    private async Task FailOrphanedJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();

            var orphaned = await database.Jobs
                .Where(job => job.Type == CatalogScanCoordinator.JobType && job.Status == JobStatus.Running)
                .ToListAsync(cancellationToken);
            foreach (var job in orphaned)
            {
                await jobs.FailAsync(job, "Interrupted by a restart.", cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not reconcile stranded catalog scan jobs.");
        }
    }
}
