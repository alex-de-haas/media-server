using System.Collections.Concurrent;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Transcoding;

/// <summary>
/// Bridges the (database-unaware) transcode engine to persistence: reconciles live job snapshots onto the
/// persisted <see cref="TranscodeJob"/> rows on a timer, translates the engine's start/complete/fail events
/// into state transitions, and — on completion — imports the output as a new movie version
/// (<see cref="TranscodeOutputImporter"/>).
/// </summary>
public sealed class TranscodeCoordinator(
    ITranscodeEngine engine,
    IServiceScopeFactory scopeFactory,
    ILogger<TranscodeCoordinator> logger)
    : BackgroundService
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(1500);

    // Jobs whose completion side-effect (output import) has been applied in this process, so the engine
    // event and the reconcile tick never import twice. The importer is also idempotent DB-side.
    private readonly ConcurrentDictionary<Guid, byte> _imported = new();

    // Promotions run one at a time. That dictionary keeps a single job from being promoted twice, but says
    // nothing about two *different* jobs — and an extraction picks its external stream indexes by reading
    // the rows already on the source. Two completions racing on the same source would both read the same
    // rows, both allocate the same index, and leave two external tracks a client cannot tell apart. There is
    // no unique constraint on (MediaSourceId, Index) to catch it, so the allocation is serialized instead.
    //
    // Global rather than keyed on the source: a promotion is a probe and a handful of inserts, it happens
    // only when a job finishes, and a lock per source would need its own lifetime management to earn a
    // concurrency nobody is waiting on.
    private readonly SemaphoreSlim _promotionGate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        engine.JobStarted += OnJobEvent;
        engine.JobCompleted += OnJobEvent;
        engine.JobFailed += OnJobEvent;

        try
        {
            using var timer = new PeriodicTimer(ProgressInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ReconcileAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            engine.JobStarted -= OnJobEvent;
            engine.JobCompleted -= OnJobEvent;
            engine.JobFailed -= OnJobEvent;
        }
    }

    private void OnJobEvent(object? sender, string engineJobId) => RunSafely(() => ApplyAsync(engineJobId));

    /// <summary>Reconciles every non-terminal job from its current engine snapshot.</summary>
    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var active = await database.TranscodeJobs
            .Where(job => job.State == TranscodeJobState.Queued || job.State == TranscodeJobState.Running)
            .ToListAsync(cancellationToken);

        var completed = new List<TranscodeJob>();
        foreach (var job in active)
        {
            var before = job.State;
            Apply(job, engine.GetSnapshot(job.EngineJobId));
            if (before is not TranscodeJobState.Completed && job.State is TranscodeJobState.Completed)
            {
                completed.Add(job);
            }
        }

        // EF writes only the rows that actually changed.
        await database.SaveChangesAsync(cancellationToken);

        foreach (var job in completed)
        {
            await PromoteAsync(scope, database, job, cancellationToken);
        }
    }

    /// <summary>Reconciles a single job (used by the engine's start/complete/fail events).</summary>
    private async Task ApplyAsync(string engineJobId)
    {
        using var scope = scopeFactory.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var job = await database.TranscodeJobs.FirstOrDefaultAsync(candidate => candidate.EngineJobId == engineJobId);
        if (job is null)
        {
            return;
        }

        var before = job.State;
        Apply(job, engine.GetSnapshot(engineJobId));
        await database.SaveChangesAsync();

        if (before is not TranscodeJobState.Completed && job.State is TranscodeJobState.Completed)
        {
            await PromoteAsync(scope, database, job, CancellationToken.None);
        }
    }

    /// <summary>
    /// Applies a completed job's side effect, exactly once per job. What that is depends on the kind: a
    /// conversion's single output becomes a new movie version, while an extraction's files become external
    /// streams of the source it read. Both finish into the library; neither is the other's special case.
    /// </summary>
    private async Task PromoteAsync(IServiceScope scope, MediaServerDbContext database, TranscodeJob job, CancellationToken cancellationToken)
    {
        if (!_imported.TryAdd(job.Id, 0))
        {
            return;
        }

        await _promotionGate.WaitAsync(cancellationToken);
        try
        {
            var promoted = job.Kind == TranscodeJobKind.Extract
                ? await scope.ServiceProvider.GetRequiredService<ExtractOutputImporter>().ImportAsync(job, cancellationToken)
                : await scope.ServiceProvider.GetRequiredService<TranscodeOutputImporter>().ImportAsync(job, cancellationToken);

            if (promoted)
            {
                logger.LogInformation(
                    "Transcode job {JobId} completed → {Output}.", job.EngineJobId, job.OutputPath ?? job.InputPath);
            }
            else
            {
                // The engine reported completion but something it should have produced is gone — surface it
                // as a failure. An importer that knows more (which files were missing) has already said so.
                job.State = TranscodeJobState.Failed;
                job.Error ??= "Transcode completed but the output file was missing.";
                await database.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Allow a retry on the next reconcile tick. (A genuine shutdown cancellation propagates.)
            _imported.TryRemove(job.Id, out _);
            logger.LogError(exception, "Failed to import transcode output for job {JobId}.", job.Id);
        }
        finally
        {
            _promotionGate.Release();
        }
    }

    public override void Dispose()
    {
        _promotionGate.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void Apply(TranscodeJob job, JobSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        if (MapState(snapshot.State) is { } state && job.State != state)
        {
            job.State = state;
            if (state is TranscodeJobState.Completed or TranscodeJobState.Failed or TranscodeJobState.Cancelled)
            {
                job.CompletedAt ??= DateTimeOffset.UtcNow;
            }

            if (state is TranscodeJobState.Failed)
            {
                job.Error ??= "The transcode job failed.";
            }
        }

        job.PercentComplete = snapshot.Complete ? 100 : snapshot.PercentComplete;
    }

    private static TranscodeJobState? MapState(string engineState) => engineState switch
    {
        "Queued" => TranscodeJobState.Queued,
        "Running" => TranscodeJobState.Running,
        "Completed" => TranscodeJobState.Completed,
        "Failed" => TranscodeJobState.Failed,
        "Cancelled" => TranscodeJobState.Cancelled,
        _ => null,
    };

    private void RunSafely(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Transcode coordinator event handler failed.");
            }
        });
    }
}
