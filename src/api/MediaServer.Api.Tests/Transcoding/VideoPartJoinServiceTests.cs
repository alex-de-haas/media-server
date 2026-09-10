using Imposter.Abstractions;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
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

    public VideoPartJoinServiceTests()
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Root = _root, Type = CatalogType.Movie, CreatedAt = now, UpdatedAt = now };
        Db.Catalogs.Add(catalog);
        Db.MediaItems.Add(new MediaItem { Id = _movie, PublicId = "movie", CatalogId = catalog.Id, Kind = MediaKind.Movie,
            Title = "Parts", AddedAt = now, UpdatedAt = now, DefaultSourceId = _first });
        Db.MediaSources.AddRange(new MediaSource { Id = _first, MediaItemId = _movie, Path = "part1.mkv", Container = "mkv", CreatedAt = now },
            new MediaSource { Id = _second, MediaItemId = _movie, Path = "part2.mkv", Container = "mkv", CreatedAt = now });
        Db.SaveChanges();
        File.WriteAllText(Path.Combine(_root, "part1.mkv"), "first original");
        File.WriteAllText(Path.Combine(_root, "part2.mkv"), "second original");
        var engine = ITranscodeEngine.Imposter();
        engine.GetToolingAsync(Arg<CancellationToken>.Any()).Returns((CancellationToken ct) => Task.FromResult(new TranscodeTooling(false, _available)));
        engine.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any())
            .Returns((TranscodeJobRequest request, CancellationToken ct) =>
            {
                _submitted = request; _submissions++;
                if (_submissionError is { } error) return Task.FromException<JobDescriptor>(error);
                var id = request.ClientJobId!.Value.ToString("n");
                _snapshot = Snapshot(id, "Queued");
                return Task.FromResult(new JobDescriptor(id, request.InputRelativePath, request.OutputRelativePath, 2, 28));
            });
        engine.GetSnapshot(Arg<string>.Any()).Returns((string id) => _snapshot!);
        engine.InspectAsync(Arg<string>.Any(), Arg<CancellationToken>.Any()).Returns((string id, CancellationToken ct) => Task.FromResult(_snapshot!));
        engine.CancelAsync(Arg<string>.Any(), Arg<CancellationToken>.Any()).Returns(Task.CompletedTask);
        _engine = engine.Instance();
        var probe = IMediaProbe.Imposter();
        probe.ProbeAsync(Arg<string>.Any(), Arg<CancellationToken>.Any()).Returns(Task.FromResult(new ProbeResult("mkv", 2 * TimeSpan.TicksPerSecond, null, 28,
            [new ProbedStream(StreamType.Video, 0, "h264", null, null, 160, 90, 25, 8, null, null, null, null, true, false, null)])));
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
