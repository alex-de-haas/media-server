using Imposter.Abstractions;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.IO;
using MediaServer.Api.Probe;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.Transcoding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: GenerateImposter(typeof(ITranscodeEngine))]
[assembly: GenerateImposter(typeof(IMediaProbe))]

namespace MediaServer.Api.Tests.Transcoding;

public sealed class VideoPartJoinServiceTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private MediaServerDbContext Db => _db.Context;
    private readonly string _root = Directory.CreateTempSubdirectory("ms-join-").FullName;
    private readonly Guid _movie = Guid.NewGuid();
    private readonly Guid _first = Guid.NewGuid();
    private readonly Guid _second = Guid.NewGuid();
    private readonly LibraryMoveQueue _moves = new();
    private ITranscodeEngine _engine = null!;
    private IMediaProbe _probe = null!;
    private JobSnapshot? _snapshot;
    private bool _available = true;
    private Exception? _submissionError;
    private TranscodeJobRequest? _submitted;
    private int _submissions;
    private string? _descriptorId;
    private bool _alternateIdFormat;
    private readonly TaskCompletionSource _operationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _submissionBlock;
    private Task? _inspectionBlock;
    private Task? _probeBlock;
    private Exception? _probeError;
    private double _outputDuration = 2;

    public VideoPartJoinServiceTests()
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Root = _root, Type = CatalogType.Movie, CreatedAt = now, UpdatedAt = now };
        Db.Catalogs.Add(catalog);
        Db.MediaItems.Add(new MediaItem { Id = _movie, PublicId = "movie", CatalogId = catalog.Id, Kind = MediaKind.Movie,
            Title = "Parts", AddedAt = now, UpdatedAt = now, DefaultSourceId = _first });
        Db.MediaSources.AddRange(new MediaSource { Id = _first, MediaItemId = _movie, Path = "part1.mkv", Container = "mkv", DurationTicks = TimeSpan.TicksPerSecond, CreatedAt = now },
            new MediaSource { Id = _second, MediaItemId = _movie, Path = "part2.mkv", Container = "mkv", DurationTicks = TimeSpan.TicksPerSecond, CreatedAt = now });
        Db.SaveChanges();
        File.WriteAllText(Path.Combine(_root, "part1.mkv"), "first original");
        File.WriteAllText(Path.Combine(_root, "part2.mkv"), "second original");
        var engine = ITranscodeEngine.Imposter();
        engine.GetToolingAsync(Arg<CancellationToken>.Any()).Returns((CancellationToken ct) => Task.FromResult(new TranscodeTooling(false, _available)));
        engine.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any())
            .Returns(async (TranscodeJobRequest request, CancellationToken ct) =>
            {
                _submitted = request; _submissions++;
                if (_submissionBlock is { } block) { _operationEntered.TrySetResult(); await block.WaitAsync(ct); }
                if (_submissionError is { } error) throw error;
                var id = _descriptorId ?? (_alternateIdFormat ? request.ClientJobId!.Value.ToString("D").ToUpperInvariant() : request.ClientJobId!.Value.ToString("n"));
                _snapshot ??= Snapshot(id, "Queued");
                return new JobDescriptor(id, request.InputRelativePath, request.OutputRelativePath, 2, 28);
            });
        engine.GetSnapshot(Arg<string>.Any()).Returns((string id) => _snapshot!);
        engine.InspectAsync(Arg<string>.Any(), Arg<CancellationToken>.Any()).Returns(async (string id, CancellationToken ct) =>
        {
            if (_inspectionBlock is { } block) { _operationEntered.TrySetResult(); await block.WaitAsync(ct); }
            return _snapshot!;
        });
        engine.CancelAsync(Arg<string>.Any(), Arg<CancellationToken>.Any()).Returns(Task.CompletedTask);
        _engine = engine.Instance();
        var probe = IMediaProbe.Imposter();
        probe.ProbeAsync(Arg<string>.Any(), Arg<CancellationToken>.Any()).Returns(async (string path, CancellationToken ct) =>
        {
            if (_probeBlock is { } block) { _operationEntered.TrySetResult(); await block.WaitAsync(ct); }
            if (_probeError is { } error) throw error;
            return new ProbeResult("mkv", (long)(_outputDuration * TimeSpan.TicksPerSecond), null, 28,
                [new ProbedStream(StreamType.Video, 0, "h264", null, null, 160, 90, 25, 8, null, null, null, null, true, false, null)]);
        });
        _probe = probe.Instance();
    }

    private static JobSnapshot Snapshot(string id, string state) => new(id, "Join parts", state, state == "Completed", 50, 0, 1, 0, null);
    private MediaServerSettings Settings => new() { CatalogMountRoots = [new CatalogMount("movies", _root)] };
    private VideoPartJoinService Service(MediaServerDbContext? context = null)
    {
        var db = context ?? Db;
        return new(db, _engine, new CatalogPathSandbox(), Settings, new LibraryMoveGuard(db, _moves),
            new TranscodeOutputImporter(db, new CatalogPathSandbox(), _probe, NullLogger<TranscodeOutputImporter>.Instance), NullLogger<VideoPartJoinService>.Instance);
    }
    private Task<TranscodeJobResponse> Create(params Guid[] ids) => Service().CreateAsync(new(ids.Length == 0 ? [_first, _second] : ids), default);

    [Fact]
    public async Task CreatesOrderedInputs_AndLocksBothUntilOutputIsImportedExactlyOnce()
    {
        var response = await Create(_second, _first);
        Assert.Equal("Join", response.Kind);
        Assert.Equal(new[] { "part2.mkv", "part1.mkv" }, _submitted!.JoinInputs!.Select(input => input.Path));
        Assert.Null(_submitted.VideoCodec);
        Assert.Equal(response.Id, _submitted.ClientJobId);
        await Assert.ThrowsAsync<LibraryFileBusyException>(() => LibraryFileMutation.RequireSourceAvailableAsync(Db, _first, default));
        await Assert.ThrowsAsync<LibraryFileBusyException>(() => LibraryFileMutation.RequireSourceAvailableAsync(Db, _second, default));
        await Assert.ThrowsAsync<TranscodeConflictException>(() => Create());
        var job = await Db.TranscodeJobs.SingleAsync();
        File.WriteAllText(Path.Combine(_root, job.OutputPath!), "joined output");
        _snapshot = Snapshot(job.EngineJobId, "Completed");
        await Service().ReconcileAsync(job, default);
        await Service().ReconcileAsync(job, default);
        Assert.True(job.OutputImported);
        Assert.Equal(3, await Db.MediaSources.CountAsync());
        Assert.Equal("Joined", (await Db.MediaSources.SingleAsync(s => s.Id != _first && s.Id != _second)).VersionName);
        Assert.Equal(_first, (await Db.MediaItems.SingleAsync()).DefaultSourceId);
        await LibraryFileMutation.RequireSourceAvailableAsync(Db, _first, default);
        Assert.Equal("first original", File.ReadAllText(Path.Combine(_root, "part1.mkv")));
        Assert.Equal("second original", File.ReadAllText(Path.Combine(_root, "part2.mkv")));
    }

    [Fact]
    public async Task ActualRenameAndDeleteServicesRefuseEitherPartAndTheMovie()
    {
        await Create();
        var rename = new LibrarySourceService(Db, new CatalogPathSandbox(), NullLogger<LibrarySourceService>.Instance);
        var delete = new LibraryDeleteService(Db, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));
        foreach (var source in new[] { _first, _second })
        {
            await Assert.ThrowsAsync<LibraryFileBusyException>(() => rename.RenameVersionAsync(source, "changed", default));
            await Assert.ThrowsAsync<LibraryFileBusyException>(() => delete.DeleteSourceAsync(source, true, default));
        }
        await Assert.ThrowsAsync<LibraryFileBusyException>(() => delete.DeleteAsync(_movie, true, true, default));
        Assert.Equal(2, await Db.MediaSources.CountAsync());
        Assert.True(File.Exists(Path.Combine(_root, "part2.mkv")));
    }

    [Theory]
    [InlineData(TranscodeJobState.Queued)]
    [InlineData(TranscodeJobState.Running)]
    [InlineData(TranscodeJobState.Completed)]
    public async Task CatalogDeletion_ProtectsActiveJoinAndAllowsDeletionAfterRelease(TranscodeJobState state)
    {
        await Create();
        var job = await Db.TranscodeJobs.SingleAsync();
        job.State = state;
        await Db.SaveChangesAsync();
        var catalogs = new CatalogService(Db, new FilesystemInspector(), Settings);

        await Assert.ThrowsAsync<LibraryFileBusyException>(() => catalogs.DeleteAsync(job.CatalogId, default));

        Assert.Equal(2, await Db.MediaSources.CountAsync());
        Assert.Single(await Db.TranscodeJobs.ToListAsync());
        Assert.Single(await Db.Catalogs.ToListAsync());
        job.State = TranscodeJobState.Cancelled;
        await Db.SaveChangesAsync();
        Assert.True(await catalogs.DeleteAsync(job.CatalogId, default));
        Assert.Empty(await Db.TranscodeJobs.ToListAsync());
        Assert.True(File.Exists(Path.Combine(_root, "part1.mkv")));
        Assert.True(File.Exists(Path.Combine(_root, "part2.mkv")));
    }

    [Fact]
    public async Task RejectsInvalidInputsUnavailableEngineAndMovingItem()
    {
        await Assert.ThrowsAsync<TranscodeRequestException>(() => Create(_first, _first));
        await Assert.ThrowsAsync<TranscodeRequestException>(() => Create(_first, Guid.NewGuid()));
        await Assert.ThrowsAsync<TranscodeRequestException>(() => Create(_first));
        _available = false;
        await Assert.ThrowsAsync<TranscodeRequestException>(() => Create());
        _available = true; _moves.TryReserve(_movie);
        await Assert.ThrowsAsync<TranscodeConflictException>(() => Create());
        Assert.Empty(Db.TranscodeJobs);
    }

    [Fact]
    public async Task RejectsMissingFilesAndCrossTitleParts()
    {
        File.Delete(Path.Combine(_root, "part2.mkv"));
        await Assert.ThrowsAsync<TranscodeRequestException>(() => Create());
        File.WriteAllText(Path.Combine(_root, "part2.mkv"), "second");
        var movie = await Db.MediaItems.SingleAsync();
        var other = new MediaItem { Id = Guid.NewGuid(), PublicId = "other", CatalogId = movie.CatalogId, Kind = MediaKind.Movie,
            Title = "Other", AddedAt = movie.AddedAt, UpdatedAt = movie.UpdatedAt };
        Db.MediaItems.Add(other); (await Db.MediaSources.SingleAsync(s => s.Id == _second)).MediaItemId = other.Id;
        await Db.SaveChangesAsync();
        await Assert.ThrowsAsync<TranscodeRequestException>(() => Create());
        Assert.Equal(0, _submissions);
    }

    [Fact]
    public async Task LostResponse_PreservesReservationAndRetriesSameIdAfterRestart()
    {
        _submissionError = new HttpRequestException("lost response");
        var result = await Create();
        Assert.Equal("Queued", result.State);
        var originalRequest = _submitted;
        var job = await Db.TranscodeJobs.SingleAsync();
        job.LastSubmissionAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Db.SaveChangesAsync();
        _submissionError = null;
        using var restarted = _db.Create();
        var restored = await restarted.TranscodeJobs.SingleAsync();
        await Service(restarted).ReconcileAsync(restored, default);
        Assert.Equal(originalRequest!.ClientJobId, _submitted!.ClientJobId);
        Assert.Equal(originalRequest.JoinInputs, _submitted.JoinInputs);
        Assert.Equal(2, _submissions);
    }

    [Fact]
    public async Task EngineRefusal_ReleasesReservationAndKeepsOriginals()
    {
        _submissionError = new InvalidOperationException("Audio tracks differ");
        Assert.Contains("Audio tracks differ", (await Assert.ThrowsAsync<TranscodeRequestException>(() => Create())).Message);
        Assert.Equal(TranscodeJobState.Failed, (await Db.TranscodeJobs.SingleAsync()).State);
        await LibraryFileMutation.RequireItemAvailableAsync(Db, _movie, default);
        Assert.Equal(2, await Db.MediaSources.CountAsync());
    }

    [Fact]
    public async Task CancellationAndJobRemoval_KeepLocksUntilEngineConfirmsTermination()
    {
        var response = await Create();
        var service = new TranscodeService(Db, _engine, new CatalogPathSandbox(), Settings,
            new LibraryMoveGuard(Db, _moves), NullLogger<TranscodeService>.Instance);
        await service.CancelAsync(response.Id, default);
        var job = await Db.TranscodeJobs.SingleAsync();
        Assert.True(job.CancellationRequested);
        await Assert.ThrowsAsync<LibraryFileBusyException>(() => service.RemoveAsync(job.Id, false, default));
        await LibraryFileMutation.RequireSourceAvailableAsync(Db, Guid.NewGuid(), default);
        await Assert.ThrowsAsync<LibraryFileBusyException>(() => LibraryFileMutation.RequireSourceAvailableAsync(Db, _second, default));
        _snapshot = Snapshot(job.EngineJobId, "Cancelled");
        await Service().ReconcileAsync(job, default);
        await LibraryFileMutation.RequireSourceAvailableAsync(Db, _second, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptedJobId_IsTrackedWithoutReleasingReservation(bool equivalentUuid)
    {
        _alternateIdFormat = equivalentUuid;
        _descriptorId = equivalentUuid ? null : "engine-assigned-id";
        await Create();
        var job = await Db.TranscodeJobs.SingleAsync();
        Assert.Equal(equivalentUuid ? job.Id.ToString("D").ToUpperInvariant() : _descriptorId, job.EngineJobId);
        Assert.Equal(TranscodeJobState.Queued, job.State);
        await Assert.ThrowsAsync<LibraryFileBusyException>(() => LibraryFileMutation.RequireItemAvailableAsync(Db, _movie, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostResponse_RecoversDurationAndRejectsTruncatedOutput(bool knownSourceDurations)
    {
        if (!knownSourceDurations)
        {
            foreach (var source in await Db.MediaSources.ToListAsync()) source.DurationTicks = 0;
            await Db.SaveChangesAsync();
        }
        _submissionError = new HttpRequestException("response lost after acceptance");
        await Create();
        var job = await Db.TranscodeJobs.SingleAsync();
        if (knownSourceDurations) Assert.Equal(2, job.ExpectedDurationSeconds);
        _snapshot = Snapshot(job.EngineJobId, "Completed");
        _submissionError = null;
        _outputDuration = 1;
        await File.WriteAllTextAsync(Path.Combine(_root, job.OutputPath!), "truncated output");
        await Service().ReconcileAsync(job, default);
        Assert.Equal(2, job.ExpectedDurationSeconds);
        Assert.Equal(TranscodeJobState.Failed, job.State);
        Assert.Contains("unexpected duration", job.Error);
        Assert.Equal(2, await Db.MediaSources.CountAsync());
        Assert.Equal(knownSourceDurations ? 1 : 2, _submissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedJoin_TransientImportFailure_RetainsReservationAndRetries(bool databaseFailure)
    {
        await Create();
        var job = await Db.TranscodeJobs.SingleAsync();
        _snapshot = Snapshot(job.EngineJobId, "Completed");
        await File.WriteAllTextAsync(Path.Combine(_root, job.OutputPath!), "joined output");
        if (databaseFailure)
            await Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_join_import BEFORE INSERT ON MediaSources BEGIN SELECT RAISE(ABORT, 'temporary write failure'); END;");
        else _probeError = new IOException("temporary probe failure");

        await Service().ReconcileAsync(job, default);

        Assert.Equal(TranscodeJobState.Completed, job.State);
        Assert.False(job.OutputImported);
        Assert.Equal(2, await Db.MediaSources.CountAsync());
        await Assert.ThrowsAsync<LibraryFileBusyException>(() => LibraryFileMutation.RequireItemAvailableAsync(Db, _movie, default));
        if (databaseFailure) await Db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_join_import;");
        _probeError = null;
        await Service().ReconcileAsync(job, default);
        Assert.True(job.OutputImported);
        Assert.Equal(3, await Db.MediaSources.CountAsync());
        await Service().ReconcileAsync(job, default);
        Assert.Equal(3, await Db.MediaSources.CountAsync());
    }

    [Theory]
    [InlineData("submission")]
    [InlineData("inspection")]
    [InlineData("import")]
    public async Task SlowJoinOperation_DoesNotHoldLibraryGate_OrRunDuplicateReconciliation(string stage)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task operation;
        TranscodeJob job;
        if (stage == "submission")
        {
            _submissionBlock = release.Task;
            operation = Create();
            await _operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            job = await Db.TranscodeJobs.SingleAsync();
        }
        else
        {
            await Create();
            job = await Db.TranscodeJobs.SingleAsync();
            if (stage == "inspection") _inspectionBlock = release.Task;
            else
            {
                _snapshot = Snapshot(job.EngineJobId, "Completed");
                _probeBlock = release.Task;
                await File.WriteAllTextAsync(Path.Combine(_root, job.OutputPath!), "joined output");
            }
            operation = Service().ReconcileAsync(job, default);
            await _operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        try
        {
            Assert.False(operation.IsCompleted);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using (await LibraryFileMutation.EnterAsync(timeout.Token))
                await LibraryFileMutation.RequireItemAvailableAsync(Db, Guid.NewGuid(), timeout.Token);
            await Service().ReconcileAsync(job, timeout.Token);
            Assert.False(operation.IsCompleted);
            Assert.Equal(1, _submissions);
        }
        finally
        {
            release.SetResult();
            await operation;
        }
        Assert.Equal(stage == "import" ? 3 : 2, await Db.MediaSources.CountAsync());
    }

    [Fact]
    public async Task CompletedJobWithMissingOutputFailsImport_WithoutAddingVersion()
    {
        await Create(); var job = await Db.TranscodeJobs.SingleAsync();
        _snapshot = Snapshot(job.EngineJobId, "Completed");
        await Service().ReconcileAsync(job, default);
        Assert.Equal(TranscodeJobState.Failed, job.State);
        Assert.Equal(2, await Db.MediaSources.CountAsync());
    }

    public void Dispose() { _db.Dispose(); Directory.Delete(_root, true); }
}
