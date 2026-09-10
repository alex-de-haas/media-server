using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Transcoding;

/// <summary>Exactly two version ids, in playback order.</summary>
public sealed record CreateJoinRequest(IReadOnlyList<Guid> SourceIds);

/// <summary>Durable join admission, engine submission and completion. Original sources are never changed.</summary>
public sealed class VideoPartJoinService(MediaServerDbContext database, ITranscodeEngine engine,
    ICatalogPathSandbox sandbox, MediaServerSettings settings, LibraryMoveGuard moveGuard,
    TranscodeOutputImporter importer, ILogger<VideoPartJoinService> logger)
{
    public async Task<TranscodeJobResponse> CreateAsync(CreateJoinRequest request, CancellationToken ct)
    {
        if (request.SourceIds is not { Count: 2 } || request.SourceIds.Distinct().Count() != 2)
            throw new TranscodeRequestException("Choose exactly two different versions, in playback order.");
        if (!(await engine.GetToolingAsync(ct)).VideoPartJoining)
            throw new TranscodeRequestException("The connected transcode engine does not support joining video parts. Update or connect the engine.");
        using var mutation = await LibraryFileMutation.EnterAsync(ct);
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
        var id = Guid.NewGuid();
        var output = TranscodeService.BuildOutputRelative(first.Path, $"Joined {id:N}");
        var catalog = item.Catalog ?? throw new TranscodeRequestException("The movie's catalog is unavailable.");
        foreach (var path in new[] { first.Path, second.Path })
            if (!sandbox.TryResolve(catalog, path, out var absolute) || !File.Exists(absolute))
                throw new TranscodeRequestException("A selected part is missing from the catalog.");
        if (!sandbox.TryResolve(catalog, output, out var destination) || File.Exists(destination) ||
            await database.MediaSources.AnyAsync(s => s.MediaItemId == item.Id && s.Path == output, ct) ||
            await database.TranscodeJobs.AnyAsync(j => j.CatalogId == catalog.Id && j.OutputPath == output, ct))
            throw new TranscodeConflictException("The output filename is already reserved.");
        var job = new TranscodeJob
        {
            Id = id, EngineJobId = id.ToString("n"), Kind = TranscodeJobKind.Join,
            MediaSourceId = first.Id, SecondSourceId = second.Id, MediaItemId = item.Id, CatalogId = catalog.Id,
            InputPath = first.Path, SecondInputPath = second.Path, OutputPath = output,
            Name = "Join parts", VideoCodec = "copy", HardwareAcceleration = "none",
            State = TranscodeJobState.Queued, CreatedAt = DateTimeOffset.UtcNow,
        };
        // Validate mount mapping before taking a durable reservation. Persist before the network call,
        // so losing the response cannot leave an untracked writer using either source.
        var engineRequest = RequestFor(job, catalog);
        database.TranscodeJobs.Add(job);
        await database.SaveChangesAsync(ct);
        try
        {
            await SubmitAsync(job, engineRequest, ct);
        }
        catch (InvalidOperationException exception)
        {
            Fail(job, exception.Message);
            await database.SaveChangesAsync(CancellationToken.None);
            throw new TranscodeRequestException(exception.Message);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            job.Error = "Waiting to confirm the engine submission; retrying with the same job id.";
            await database.SaveChangesAsync(CancellationToken.None);
            logger.LogWarning(exception, "Join {JobId} submission needs reconciliation.", job.Id);
        }
        return TranscodeJobResponse.From(job, engine.GetSnapshot(job.EngineJobId));
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
        var descriptor = await engine.CreateAsync(request, ct);
        if (descriptor.JobId != job.EngineJobId)
            throw new InvalidOperationException("The engine did not preserve the join's job id.");
        job.ExpectedDurationSeconds = descriptor.DurationSeconds;
        job.Error = null;
        await database.SaveChangesAsync(ct);
    }

    /// <summary>Restores an interrupted submission using its stable id, then imports a completed output once.</summary>
    public async Task ReconcileAsync(TranscodeJob job, CancellationToken ct)
    {
        using var mutation = await LibraryFileMutation.EnterAsync(ct);
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
        catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException || exception is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Join {JobId}: engine is unreachable; retaining both input reservations.", job.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Fail(job, exception.Message);
            await database.SaveChangesAsync(ct);
            logger.LogWarning(exception, "Join {JobId} failed while reconciling.", job.Id);
        }
    }

    private static void Fail(TranscodeJob job, string reason)
    {
        job.State = TranscodeJobState.Failed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.Error = reason;
    }
}
