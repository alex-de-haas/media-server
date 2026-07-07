using System.Collections.Concurrent;
using System.Threading.Channels;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using MediaServer.Api.Jobs;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>A queued request to move one top-level item into a target catalog, paired with its job.</summary>
public sealed record LibraryMoveWorkItem(Guid ItemId, Guid TargetCatalogId, Guid JobId);

/// <summary>The outcome of asking to start a move; drives the endpoint's status code.</summary>
public enum LibraryMoveRequestStatus
{
    Started,
    NotFound,
    Unsupported,
    SameCatalog,
    IncompatibleType,
    CatalogOffline,
    InsufficientSpace,
    AlreadyMoving,
}

public sealed record LibraryMoveRequestResult(LibraryMoveRequestStatus Status, Guid? JobId);

/// <summary>The live state of one running move, for the UI.</summary>
public sealed record LibraryMoveStatus(Guid ItemId, Guid JobId, int Progress);

/// <summary>
/// In-process queue handing move requests to <see cref="LibraryMoveWorker"/>. A single reader drains it so
/// moves run one at a time. An in-memory set of items with a move in flight is the race-free source of
/// truth: <see cref="TryReserve"/> admits an item atomically, <see cref="Release"/> frees it when done.
/// </summary>
public interface ILibraryMoveQueue
{
    /// <summary>Atomically reserves the item. False when a move is already queued or running for it.</summary>
    bool TryReserve(Guid itemId);

    /// <summary>Frees a reservation once its run finishes (success, failure, or shutdown).</summary>
    void Release(Guid itemId);

    void Enqueue(LibraryMoveWorkItem work);

    IAsyncEnumerable<LibraryMoveWorkItem> DequeueAllAsync(CancellationToken cancellationToken);
}

public sealed class LibraryMoveQueue : ILibraryMoveQueue
{
    private readonly Channel<LibraryMoveWorkItem> _channel = Channel.CreateUnbounded<LibraryMoveWorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<Guid, byte> _active = new();

    public bool TryReserve(Guid itemId) => _active.TryAdd(itemId, 0);

    public void Release(Guid itemId) => _active.TryRemove(itemId, out _);

    public void Enqueue(LibraryMoveWorkItem work) => _channel.Writer.TryWrite(work);

    public IAsyncEnumerable<LibraryMoveWorkItem> DequeueAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Validates and admits a move: rejects an unknown/unsupported item or target, an incompatible catalog
/// type, a same-catalog no-op, an offline root, or (for a cross-volume move) insufficient free space;
/// refuses a second concurrent move of the same item; otherwise opens a job and queues the work.
/// </summary>
public sealed class LibraryMoveCoordinator(
    MediaServerDbContext database, IFilesystemInspector filesystem, JobService jobs, ILibraryMoveQueue queue)
{
    public async Task<LibraryMoveRequestResult> RequestAsync(Guid itemId, Guid targetCatalogId, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
        {
            return new LibraryMoveRequestResult(LibraryMoveRequestStatus.NotFound, null);
        }

        // Only a published top-level movie or series can move (same gate as delete).
        if (item.PublicId is null || item.ParentId is not null || item.Kind is not (MediaKind.Movie or MediaKind.Series))
        {
            return new LibraryMoveRequestResult(LibraryMoveRequestStatus.Unsupported, null);
        }

        var target = await database.Catalogs.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == targetCatalogId, cancellationToken);
        var source = await database.Catalogs.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == item.CatalogId, cancellationToken);
        if (target is null || source is null)
        {
            return new LibraryMoveRequestResult(LibraryMoveRequestStatus.NotFound, null);
        }

        if (target.Id == source.Id)
        {
            return new LibraryMoveRequestResult(LibraryMoveRequestStatus.SameCatalog, null);
        }

        if (!LibraryMoveService.IsTypeCompatible(item.Kind, target.Type))
        {
            return new LibraryMoveRequestResult(LibraryMoveRequestStatus.IncompatibleType, null);
        }

        if (!filesystem.DirectoryExists(source.Root) || !filesystem.DirectoryExists(target.Root))
        {
            return new LibraryMoveRequestResult(LibraryMoveRequestStatus.CatalogOffline, null);
        }

        // Cross-volume moves copy bytes, so pre-check free space on the target volume (mirrors torrent add).
        if (!LibraryMoveService.SameVolume(filesystem, source.Root, target.Root))
        {
            var required = await SubtreeSizeAsync(item, cancellationToken);
            if (required > filesystem.GetAvailableFreeBytes(target.Root))
            {
                return new LibraryMoveRequestResult(LibraryMoveRequestStatus.InsufficientSpace, null);
            }
        }

        // Atomic admit: closes the check-then-start race between two concurrent move requests for the same item.
        if (!queue.TryReserve(itemId))
        {
            return new LibraryMoveRequestResult(LibraryMoveRequestStatus.AlreadyMoving, null);
        }

        try
        {
            var job = await jobs.StartAsync(LibraryMoveService.JobType, "MediaItem", itemId, cancellationToken);
            queue.Enqueue(new LibraryMoveWorkItem(itemId, targetCatalogId, job.Id));
            return new LibraryMoveRequestResult(LibraryMoveRequestStatus.Started, job.Id);
        }
        catch
        {
            queue.Release(itemId); // Never leak the reservation if we couldn't open/queue the job.
            throw;
        }
    }

    /// <summary>The items with a move currently in flight, with their job id and progress.</summary>
    public async Task<IReadOnlyList<LibraryMoveStatus>> ListActiveAsync(CancellationToken cancellationToken) =>
        await database.Jobs.AsNoTracking()
            .Where(job => job.Type == LibraryMoveService.JobType && job.Status == JobStatus.Running && job.RelatedId != null)
            .Select(job => new LibraryMoveStatus(job.RelatedId!.Value, job.Id, job.Progress))
            .ToListAsync(cancellationToken);

    private async Task<long> SubtreeSizeAsync(MediaItem item, CancellationToken cancellationToken)
    {
        if (item.Kind == MediaKind.Movie)
        {
            return await database.MediaSources.Where(source => source.MediaItemId == item.Id).SumAsync(source => source.SizeBytes, cancellationToken);
        }

        // Keep the id set as an IQueryable so EF Core emits one SQL roundtrip with a subquery instead of
        // pulling the ids into memory first.
        var itemIds = database.MediaItems
            .Where(candidate => candidate.Id == item.Id || candidate.SeriesId == item.Id || candidate.ParentId == item.Id)
            .Select(candidate => candidate.Id);
        return await database.MediaSources.Where(source => itemIds.Contains(source.MediaItemId)).SumAsync(source => source.SizeBytes, cancellationToken);
    }
}

/// <summary>Drains <see cref="ILibraryMoveQueue"/>, running one move at a time.</summary>
public sealed class LibraryMoveWorker(IServiceScopeFactory scopeFactory, ILibraryMoveQueue queue, ILogger<LibraryMoveWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await FailOrphanedJobsAsync(stoppingToken);

        try
        {
            await foreach (var work in queue.DequeueAllAsync(stoppingToken))
            {
                await ProcessAsync(work, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task ProcessAsync(LibraryMoveWorkItem work, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
            var service = scope.ServiceProvider.GetRequiredService<LibraryMoveService>();

            var job = await database.Jobs.FirstOrDefaultAsync(candidate => candidate.Id == work.JobId, cancellationToken);
            if (job is null)
            {
                return; // The job row vanished (shouldn't happen) — nothing to drive.
            }

            try
            {
                var result = await service.MoveAsync(work.ItemId, work.TargetCatalogId, job, cancellationToken);
                if (result.Status == MoveResult.Kind.Ok)
                {
                    await jobs.CompleteAsync(job, cancellationToken);
                }
                else
                {
                    await jobs.FailAsync(job, $"Move failed: {result.Status}.", cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down — leave the row Running; the next start reconciles it to Failed.
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Move of item {Item} to catalog {Catalog} failed.", work.ItemId, work.TargetCatalogId);
                await jobs.FailAsync(job, exception.Message, CancellationToken.None);
            }
        }
        finally
        {
            queue.Release(work.ItemId);
        }
    }

    /// <summary>
    /// A single app instance owns this queue, so any move job still Running at startup was stranded by a
    /// restart (its files may be partially copied — a target scan re-adopts leftovers). Mark it Failed so it
    /// doesn't show as forever-active and a new move can start.
    /// </summary>
    private async Task FailOrphanedJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var jobs = scope.ServiceProvider.GetRequiredService<JobService>();

            var orphaned = await database.Jobs
                .Where(job => job.Type == LibraryMoveService.JobType && job.Status == JobStatus.Running)
                .ToListAsync(cancellationToken);
            foreach (var job in orphaned)
            {
                await jobs.FailAsync(job, "Interrupted by a restart.", cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Failed to reconcile orphaned move jobs on startup.");
        }
    }
}
