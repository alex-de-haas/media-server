using MediaServer.Api.Data;
using MediaServer.Api.Realtime;

namespace MediaServer.Api.Jobs;

/// <summary>
/// Persists observable <see cref="Job"/> rows and broadcasts their lifecycle over the realtime stream so the UI
/// activity view can follow background work. See <c>docs/features/background-tasks.md</c>.
/// </summary>
public sealed class JobService(MediaServerDbContext database, IRealtimeNotifier notifier)
{
    public async Task<Job> StartAsync(string type, string? relatedType, Guid? relatedId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Type = type,
            RelatedType = relatedType,
            RelatedId = relatedId,
            Status = JobStatus.Running,
            Progress = 0,
            StartedAt = now,
            UpdatedAt = now,
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
        };
        database.Jobs.Add(job);
        await database.SaveChangesAsync(cancellationToken);
        await notifier.JobChangedAsync(RealtimeEvents.JobStarted, ToEvent(job), cancellationToken);
        return job;
    }

    /// <summary>Updates a running job's progress (0–100) and broadcasts it. Caller throttles call frequency.
    /// A byte-moving job may pass its live <paramref name="bytesPerSecond"/>/<paramref name="etaSeconds"/> to
    /// ride along on the broadcast — they're transient (not persisted) and default to null for other jobs.</summary>
    public async Task ProgressAsync(
        Job job, int progress, CancellationToken cancellationToken, long? bytesPerSecond = null, long? etaSeconds = null)
    {
        var clamped = Math.Clamp(progress, 0, 100);
        if (job.Progress == clamped)
        {
            return; // No change — don't churn UpdatedAt, the DB, or the realtime feed.
        }

        job.Progress = clamped;
        job.UpdatedAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(cancellationToken);
        await notifier.JobChangedAsync(RealtimeEvents.JobProgress, ToEvent(job, bytesPerSecond, etaSeconds), cancellationToken);
    }

    public async Task CompleteAsync(Job job, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Completed;
        job.Progress = 100;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.UpdatedAt = job.CompletedAt.Value;
        await database.SaveChangesAsync(cancellationToken);
        await notifier.JobChangedAsync(RealtimeEvents.JobCompleted, ToEvent(job), cancellationToken);
    }

    public async Task FailAsync(Job job, string error, CancellationToken cancellationToken)
    {
        job.Status = JobStatus.Failed;
        job.Error = error;
        job.AttemptCount++;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.UpdatedAt = job.CompletedAt.Value;
        await database.SaveChangesAsync(cancellationToken);
        await notifier.JobChangedAsync(RealtimeEvents.JobFailed, ToEvent(job), cancellationToken);
    }

    private static JobEvent ToEvent(Job job, long? bytesPerSecond = null, long? etaSeconds = null) =>
        new(job.Id, job.Type, job.RelatedType, job.RelatedId, job.Status.ToString(), job.Progress, job.Error, bytesPerSecond, etaSeconds);
}
