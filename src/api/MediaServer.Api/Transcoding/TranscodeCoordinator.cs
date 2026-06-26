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

    /// <summary>Imports a completed job's output as a new movie version, exactly once per job.</summary>
    private async Task PromoteAsync(IServiceScope scope, MediaServerDbContext database, TranscodeJob job, CancellationToken cancellationToken)
    {
        if (!_imported.TryAdd(job.Id, 0))
        {
            return;
        }

        try
        {
            var importer = scope.ServiceProvider.GetRequiredService<TranscodeOutputImporter>();
            if (await importer.ImportAsync(job, cancellationToken))
            {
                logger.LogInformation("Transcode job {JobId} completed → {Output}.", job.EngineJobId, job.OutputPath);
            }
            else
            {
                // The engine reported completion but the output is gone — surface it as a failure.
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
