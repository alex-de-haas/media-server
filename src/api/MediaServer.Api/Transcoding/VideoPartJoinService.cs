using System.Collections.Concurrent;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Organizer;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Transcoding;

/// <summary>Exactly two version ids, in playback order.</summary>
public sealed record CreateJoinRequest(IReadOnlyList<Guid> SourceIds);

/// <summary>Durable join admission, engine submission and completion. Original sources are never changed.</summary>
public sealed class VideoPartJoinService(MediaServerDbContext database, ITranscodeEngine engine,
    ICatalogPathSandbox sandbox, MediaServerSettings settings, LibraryMoveGuard moveGuard,
    TranscodeOutputImporter importer, ILogger<VideoPartJoinService> logger)
{
    // Only one submission/reconciliation per job may run at a time. A busy job is retried on the
    // next coordinator tick; unrelated library mutations never wait on its HTTP/probe operations.
    private static readonly ConcurrentDictionary<Guid, byte> ActiveOperations = new();
    private sealed class JoinRejectedException(string message) : Exception(message);

    public async Task<TranscodeJobResponse> CreateAsync(CreateJoinRequest request, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        ActiveOperations.TryAdd(id, 0);
        try { return await CreateCoreAsync(id, request, ct); }
        finally { ActiveOperations.TryRemove(id, out _); }
    }

    private async Task<TranscodeJobResponse> CreateCoreAsync(Guid id, CreateJoinRequest request, CancellationToken ct)
    {
        if (request.SourceIds is not { Count: 2 } || request.SourceIds.Distinct().Count() != 2)
            throw new TranscodeRequestException("Choose exactly two different versions, in playback order.");
        if (!(await engine.GetToolingAsync(ct)).VideoPartJoining)
            throw new TranscodeRequestException("The connected transcode engine does not support joining video parts. Update or connect the engine.");
        TranscodeJob job;
        TranscodeJobRequest engineRequest;
        using (await LibraryFileMutation.EnterAsync(ct))
        {
            var sources = await database.MediaSources.Include(s => s.MediaItem).ThenInclude(i => i!.Catalog)
                .Where(s => request.SourceIds.Contains(s.Id)).ToListAsync(ct);
            if (sources.Count != 2) throw new TranscodeRequestException("A selected version no longer exists.");
            var first = sources.Single(s => s.Id == request.SourceIds[0]);
            var second = sources.Single(s => s.Id == request.SourceIds[1]);
            var item = first.MediaItem!;
            if (item.Kind != MediaKind.Movie || item.PublicId is null || second.MediaItemId != item.Id)
                throw new TranscodeRequestException("Both parts must be versions of the same published movie.");
            if (await moveGuard.IsItemMovingAsync(item.Id, ct)) throw new TranscodeConflictException(LibraryMoveGuard.MoveInProgressError);
            if (await LibraryFileMutation.HasJoinAsync(database, item.Id, ct))
                throw new TranscodeConflictException("This movie already has an active join.");
            var catalog = item.Catalog ?? throw new TranscodeRequestException("The movie's catalog is unavailable.");
            foreach (var path in new[] { first.Path, second.Path })
                if (!sandbox.TryResolve(catalog, path, out var absolute) || !File.Exists(absolute))
                    throw new TranscodeRequestException("A selected part is missing from the catalog.");
            var output = await FindOutputPathAsync(catalog, item, first.Path, ct);
            job = new TranscodeJob
            {
                Id = id, EngineJobId = id.ToString("n"), Kind = TranscodeJobKind.Join,
                MediaSourceId = first.Id, SecondSourceId = second.Id, MediaItemId = item.Id, CatalogId = catalog.Id,
                InputPath = first.Path, SecondInputPath = second.Path, OutputPath = output,
                Name = "Join parts", VideoCodec = "copy", HardwareAcceleration = "none",
                State = TranscodeJobState.Queued, CreatedAt = DateTimeOffset.UtcNow,
                ExpectedDurationSeconds = first.DurationTicks > 0 && second.DurationTicks > 0
                    ? first.DurationTicks / (double)TimeSpan.TicksPerSecond + second.DurationTicks / (double)TimeSpan.TicksPerSecond
                    : null,
            };
            // Validate mount mapping before taking a durable reservation. Persist before the network call,
            // so losing the response cannot leave an untracked writer using either source.
            engineRequest = RequestFor(job, catalog);
            database.TranscodeJobs.Add(job);
            await database.SaveChangesAsync(ct);
        }
        try
        {
            await SubmitAsync(job, engineRequest, ct);
        }
        catch (JoinRejectedException exception)
        {
            Fail(job, exception.Message);
            await database.SaveChangesAsync(CancellationToken.None);
            throw new TranscodeRequestException(exception.Message);
        }
        catch (Exception exception)
        {
            job.Error = "Waiting to confirm the engine submission; retrying with the same job id.";
            await database.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(exception, "Join {JobId} submission needs reconciliation.", job.Id);
        }
        return TranscodeJobResponse.From(job, engine.GetSnapshot(job.EngineJobId));
    }

    private async Task<string> FindOutputPathAsync(Catalog catalog, MediaItem item, string firstPath, CancellationToken ct)
    {
        // Keep the output beside Part 1, but name it from the movie rather than either part's edition.
        // Admission holds the library mutation gate until this choice is durably reserved on the job.
        var directory = Path.GetDirectoryName(firstPath);
        for (var number = 1; ; number++)
        {
            ct.ThrowIfCancellationRequested();
            var label = number == 1 ? "Joined" : $"Joined {number}";
            var fileName = Path.GetFileName(LibraryNaming.ForMovie(catalog, item, ".mkv", label));
            var output = string.IsNullOrEmpty(directory) ? fileName : $"{directory}/{fileName}";
            if (!sandbox.TryResolve(catalog, output, out var destination))
                throw new TranscodeRequestException("Could not place the output inside the catalog.");
            if (File.Exists(destination) || Directory.Exists(destination) ||
                await database.MediaSources.AnyAsync(s => s.MediaItem!.CatalogId == catalog.Id && s.Path == output, ct) ||
                await database.MediaSources.AnyAsync(s => s.MediaItemId == item.Id && s.VersionName == label, ct) ||
                await database.TranscodeJobs.AnyAsync(j => j.CatalogId == catalog.Id && j.OutputPath == output, ct))
                continue;
            return output;
        }
    }

    private TranscodeJobRequest RequestFor(TranscodeJob job, Catalog catalog)
    {
        EngineJoinInput Mount(string? path)
        {
            if (path is null || !sandbox.TryResolve(catalog, path, out var absolute) ||
                !CatalogMounts.TryResolve(settings, absolute, out var label, out var relative))
                throw new TranscodeRequestException("Both parts and their output must be under configured media mounts shared with the engine.");
            return new(label, relative);
        }
        var first = Mount(job.InputPath); var second = Mount(job.SecondInputPath); var output = Mount(job.OutputPath);
        return new(first.MountLabel, first.Path, output.MountLabel, output.Path, null, null, null,
            JoinInputs: [first, second], ClientJobId: job.Id);
    }

    private async Task SubmitAsync(TranscodeJob job, TranscodeJobRequest request, CancellationToken ct)
    {
        job.LastSubmissionAt = DateTimeOffset.UtcNow;
        await database.SaveChangesAsync(ct);
        JobDescriptor descriptor;
        try { descriptor = await engine.CreateAsync(request, ct); }
        catch (InvalidOperationException exception) { throw new JoinRejectedException(exception.Message); }
        // Once accepted, retain the actual id for inspection/cancellation. A different spelling (or
        // even a nonconforming engine id) must not be treated as a refusal that releases the sources.
        if (string.IsNullOrWhiteSpace(descriptor.JobId))
            throw new HttpRequestException("The engine returned no join id; submission needs confirmation.");
        job.EngineJobId = descriptor.JobId;
        if (descriptor.DurationSeconds is > 0) job.ExpectedDurationSeconds = descriptor.DurationSeconds;
        job.Error = null;
        await database.SaveChangesAsync(ct);
    }

    /// <summary>Restores an interrupted submission using its stable id, then imports a completed output once.</summary>
    public async Task ReconcileAsync(TranscodeJob job, CancellationToken ct)
    {
        if (!ActiveOperations.TryAdd(job.Id, 0)) return;
        try { await ReconcileCoreAsync(job, ct); }
        finally { ActiveOperations.TryRemove(job.Id, out _); }
    }

    private async Task ReconcileCoreAsync(TranscodeJob job, CancellationToken ct)
    {
        await database.Entry(job).ReloadAsync(ct);
        if (database.Entry(job).State == EntityState.Detached || job.OutputImported ||
            job.State is TranscodeJobState.Failed or TranscodeJobState.Cancelled) return;
        try
        {
            if (job.State != TranscodeJobState.Completed)
            {
                var snapshot = await engine.InspectAsync(job.EngineJobId, ct);
                if (snapshot is null)
                {
                    if (job.CancellationRequested)
                    {
                        job.State = TranscodeJobState.Cancelled;
                        job.CompletedAt = DateTimeOffset.UtcNow;
                    }
                    else if (job.LastSubmissionAt is null || DateTimeOffset.UtcNow - job.LastSubmissionAt > TimeSpan.FromSeconds(30))
                    {
                        var catalog = await database.Catalogs.SingleAsync(c => c.Id == job.CatalogId, ct);
                        await SubmitAsync(job, RequestFor(job, catalog), ct);
                    }
                }
                else
                {
                    // A lost create response may predate local duration metadata. Recover the
                    // descriptor via the engine's idempotent submission before validating any output.
                    if (job.ExpectedDurationSeconds is not > 0 && !job.CancellationRequested &&
                        snapshot.State is "Queued" or "Running" or "Completed")
                    {
                        var catalog = await database.Catalogs.SingleAsync(c => c.Id == job.CatalogId, ct);
                        await SubmitAsync(job, RequestFor(job, catalog), ct);
                    }
                    await database.Entry(job).ReloadAsync(ct);
                    if (job.CancellationRequested && snapshot.State is "Queued" or "Running")
                        await engine.CancelAsync(job.EngineJobId, ct);
                    if (Enum.TryParse<TranscodeJobState>(snapshot.State, out var state)) job.State = state;
                    job.PercentComplete = snapshot.PercentComplete;
                    job.Error = snapshot.Error;
                    if (job.State is TranscodeJobState.Completed or TranscodeJobState.Cancelled or TranscodeJobState.Failed)
                        job.CompletedAt ??= DateTimeOffset.UtcNow;
                }
                await database.SaveChangesAsync(ct);
            }
            if (job.State == TranscodeJobState.Completed)
            {
                if (job.ExpectedDurationSeconds is not > 0)
                {
                    var catalog = await database.Catalogs.SingleAsync(c => c.Id == job.CatalogId, ct);
                    await SubmitAsync(job, RequestFor(job, catalog), ct);
                }
                if (await importer.ImportAsync(job, ct))
                {
                    job.OutputImported = true;
                    job.PercentComplete = 100;
                    job.Error = null;
                }
                else Fail(job, job.Error ?? "The engine completed but the joined output is missing.");
                await database.SaveChangesAsync(ct);
            }
        }
        catch (JoinRejectedException exception)
        {
            Fail(job, exception.Message);
            await database.SaveChangesAsync(ct);
            logger.LogWarning(exception, "Join {JobId} was refused by the engine.", job.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A transport, probe or database failure says nothing about whether the engine accepted
            // the work. Reload to discard unpersisted terminal/import flags and retry on the next tick.
            await database.Entry(job).ReloadAsync(ct);
            logger.LogWarning(exception, "Join {JobId}: reconciliation needs retry; retaining input reservations.", job.Id);
        }
    }

    private static void Fail(TranscodeJob job, string reason)
    {
        job.State = TranscodeJobState.Failed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.Error = reason;
    }
}
