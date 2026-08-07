using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Probe;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.Transcoding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Transcoding;

/// <summary>
/// Writing a version's own tracks out as files beside it — the inverse of merging. What comes out has to be
/// indistinguishable from a sidecar a release shipped, because that is what makes every existing sidecar
/// operation apply to it without a line written for either.
/// </summary>
public sealed class TrackExtractionTests : IDisposable
{
    private const string VideoRelative = "The Rock (1996)/The Rock (1996).mkv";

    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly string _root;
    private readonly RecordingEngine _engine = new();

    private Guid _sourceId;
    private Guid _movieId;
    private Guid _catalogId;

    public TrackExtractionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ms-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "The Rock (1996)"));
        File.WriteAllText(Path.Combine(_root, VideoRelative.Replace('/', Path.DirectorySeparatorChar)), "video");
        _context = _db.Create();
        Seed();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ── harness ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Captures the request instead of talking to an engine, and answers with a descriptor.</summary>
    private sealed class RecordingEngine : ITranscodeEngine
    {
        private int _created;

        public TranscodeJobRequest? Seen { get; private set; }

        public Task<JobDescriptor> CreateAsync(TranscodeJobRequest request, CancellationToken cancellationToken)
        {
            Seen = request;
            // A fresh id per call, because EngineJobId is unique: a test that extracts twice is testing the
            // second attempt, not the index.
            return Task.FromResult(new JobDescriptor(
                $"engine-{++_created}", request.InputRelativePath, request.OutputRelativePath, 120, 1000,
                request.Outputs?.Select(output => output.RelativePath).ToList()));
        }

        public Task CancelAsync(string jobId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RemoveAsync(string jobId, bool deleteOutput, CancellationToken cancellationToken) => Task.CompletedTask;

        public JobSnapshot? GetSnapshot(string jobId) => null;

        public IReadOnlyList<JobSnapshot> GetAllSnapshots() => [];

#pragma warning disable CS0067 // The consumer surface raises these; nothing here does.
        public event EventHandler<string>? JobStarted;
        public event EventHandler<string>? JobCompleted;
        public event EventHandler<string>? JobFailed;
#pragma warning restore CS0067
    }

    /// <summary>Answers for whatever file it is handed, so an imported row gets its specs.</summary>
    private sealed class StubProbe(params ProbedStream[] streams) : IMediaProbe
    {
        public Task<ProbeResult> ProbeAsync(string absolutePath, CancellationToken cancellationToken) =>
            Task.FromResult(new ProbeResult("matroska", 0, null, 1024, streams));
    }

    private MediaServerSettings Settings => new()
    {
        CatalogMountRoots = [new CatalogMount("catalogs", _root)],
    };

    private TrackExtractionService Service() =>
        new(_context, _engine, new CatalogPathSandbox(), Settings,
            new LibraryMoveGuard(_context, new LibraryMoveQueue()),
            NullLogger<TrackExtractionService>.Instance);

    private ExtractOutputImporter Importer(params ProbedStream[] streams) =>
        new(_context, new CatalogPathSandbox(), new StubProbe(streams), NullLogger<ExtractOutputImporter>.Instance);

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
        _catalogId = Guid.NewGuid();
        _movieId = Guid.NewGuid();
        _sourceId = Guid.NewGuid();

        _context.Catalogs.Add(new Catalog
        {
            Id = _catalogId, Name = "Movies", Type = CatalogType.Movie, Root = _root, CreatedAt = now, UpdatedAt = now,
        });
        _context.MediaItems.Add(new MediaItem
        {
            Id = _movieId, PublicId = Guid.NewGuid().ToString("N"), CatalogId = _catalogId,
            Kind = MediaKind.Movie, Title = "The Rock", Year = 1996, AddedAt = now, UpdatedAt = now,
        });
        _context.MediaSources.Add(new MediaSource
        {
            Id = _sourceId, MediaItemId = _movieId, Container = "mkv", Path = VideoRelative,
            SizeBytes = 1000, DurationTicks = 1, CreatedAt = now,
        });
        _context.SaveChanges();
    }

    private Guid AddStream(
        StreamType type, int index, string? codec, string? language = null, string? title = null,
        bool isExternal = false, string? externalPath = null)
    {
        var id = Guid.NewGuid();
        _context.MediaStreams.Add(new MediaStream
        {
            Id = id, MediaSourceId = _sourceId, StreamType = type, Index = index, Codec = codec,
            Language = language, Title = title, IsExternal = isExternal, ExternalPath = externalPath,
        });
        _context.SaveChanges();
        return id;
    }

    private Task<TranscodeJobResponse> ExtractAsync(params Guid[] streamIds) =>
        Service().CreateAsync(new CreateExtractionRequest(_sourceId, streamIds), CancellationToken.None);

    // ── containers ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Audio_always_becomes_Matroska()
    {
        // A .mka carries its own language and title, which is why a tagged container never needs its file
        // name to be the only record of them. A raw .ac3 would manufacture that problem on purpose.
        var dub = AddStream(StreamType.Audio, 1, "ac3", "rus", "Гаврилов");

        var job = await ExtractAsync(dub);

        Assert.Equal("The Rock (1996)/The Rock (1996).rus.mka", Assert.Single(job.OutputPaths));
        var output = Assert.Single(_engine.Seen!.Outputs!);
        Assert.Null(output.Codec); // a stream copy
        Assert.Equal("rus", output.Language);
        Assert.Equal("Гаврилов", output.Title);
    }

    [Theory]
    [InlineData("subrip", ".srt", null)]
    [InlineData("ass", ".ass", null)]
    [InlineData("ssa", ".ass", null)]
    [InlineData("webvtt", ".vtt", null)]
    // The one conversion: 3GPP timed text has no file form of its own, so it cannot be extracted without
    // becoming one.
    [InlineData("mov_text", ".srt", "srt")]
    public async Task A_text_subtitle_keeps_the_format_clients_read_off_disk(string codec, string extension, string? engineCodec)
    {
        var subtitle = AddStream(StreamType.Subtitle, 2, codec, "eng");

        var job = await ExtractAsync(subtitle);

        Assert.EndsWith(extension, Assert.Single(job.OutputPaths));
        Assert.Equal(engineCodec, Assert.Single(_engine.Seen!.Outputs!).Codec);
    }

    [Theory]
    [InlineData("hdmv_pgs_subtitle")]
    [InlineData("dvd_subtitle")]
    [InlineData("dvb_subtitle")]
    public async Task A_picture_based_subtitle_is_refused(string codec)
    {
        // It already reaches the viewer by direct play from the container, no client reads it better as a
        // file, and turning one into text is OCR.
        var subtitle = AddStream(StreamType.Subtitle, 2, codec, "ger");

        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() => ExtractAsync(subtitle));

        Assert.Contains("picture-based", error.Message);
    }

    [Fact]
    public async Task A_subtitle_with_no_known_codec_says_what_to_do_about_it()
    {
        // There is no telling what file it should become, and guessing would write an .srt full of nothing.
        var subtitle = AddStream(StreamType.Subtitle, 2, codec: null, "eng");

        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() => ExtractAsync(subtitle));

        Assert.Contains("Refresh", error.Message);
    }

    [Fact]
    public async Task Video_cannot_be_extracted()
    {
        var video = AddStream(StreamType.Video, 0, "hevc");

        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() => ExtractAsync(video));

        Assert.Contains("audio and subtitle", error.Message);
    }

    // ── selection ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_sidecar_cannot_be_extracted_again()
    {
        // It is already a file; there is nothing to extract it from.
        var sidecar = AddStream(
            StreamType.Audio, 1000, "ac3", "rus", "Гаврилов",
            isExternal: true, externalPath: "The Rock (1996)/The Rock (1996).rus.mka");

        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() => ExtractAsync(sidecar));

        Assert.Contains("not a track of this version", error.Message);
    }

    [Fact]
    public async Task A_stream_of_another_version_is_refused()
    {
        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() => ExtractAsync(Guid.NewGuid()));

        Assert.Contains("not a track of this version", error.Message);
    }

    [Fact]
    public async Task Naming_nothing_is_refused()
    {
        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() => ExtractAsync());

        Assert.Contains("at least one track", error.Message);
    }

    [Fact]
    public async Task Tracks_are_ordered_by_their_position_in_the_container()
    {
        // The naming rule falls back to a companion's position when it has no title, and that fallback is
        // only stable if the order does not depend on how a client happened to list its selection.
        var second = AddStream(StreamType.Audio, 2, "ac3", "rus");
        var first = AddStream(StreamType.Audio, 1, "ac3", "rus");

        var job = await ExtractAsync(second, first);

        Assert.Equal([1, 2], _engine.Seen!.Outputs!.Select(output => output.StreamIndex));
        Assert.Equal(
            ["The Rock (1996)/The Rock (1996).rus.1.mka", "The Rock (1996)/The Rock (1996).rus.2.mka"],
            job.OutputPaths);
    }

    // ── naming ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_track_extracted_beside_an_existing_sidecar_is_told_apart_by_its_title()
    {
        // The collision is with a file on disk, which the batch alone cannot see. Without counting it the
        // plain name would be taken and the track's own label — the thing that says which dub this is —
        // would go unused.
        AddStream(
            StreamType.Audio, 1000, "ac3", "rus", "Гаврилов",
            isExternal: true, externalPath: "The Rock (1996)/The Rock (1996).rus.mka");
        var dub = AddStream(StreamType.Audio, 1, "dts", "rus", "Володарский");

        var job = await ExtractAsync(dub);

        Assert.Equal("The Rock (1996)/The Rock (1996).rus.Володарский.mka", Assert.Single(job.OutputPaths));
    }

    [Fact]
    public async Task A_lone_track_keeps_the_plain_name_clients_match_on()
    {
        var subtitle = AddStream(StreamType.Subtitle, 2, "subrip", "rus", "Полные");

        var job = await ExtractAsync(subtitle);

        Assert.Equal("The Rock (1996)/The Rock (1996).rus.srt", Assert.Single(job.OutputPaths));
    }

    // ── the job ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_job_is_an_extraction_that_composes_nothing()
    {
        var dub = AddStream(StreamType.Audio, 1, "ac3", "rus");

        var job = await ExtractAsync(dub);

        Assert.Equal("Extract", job.Kind);
        Assert.Null(job.OutputPath);
        // Nothing about a picture reaches the engine: it refuses a job that claims otherwise.
        Assert.Null(_engine.Seen!.OutputRelativePath);
        Assert.Null(_engine.Seen.MaxHeight);
        Assert.Null(_engine.Seen.QualityLevel);
        Assert.Null(_engine.Seen.AdditionalInputs);

        var stored = await _context.TranscodeJobs.Include(entity => entity.Outputs).SingleAsync();
        Assert.Equal(TranscodeJobKind.Extract, stored.Kind);
        Assert.Null(stored.OutputPath);
        var output = Assert.Single(stored.Outputs);
        Assert.Equal(1, output.SourceStreamIndex);
        Assert.Equal(StreamType.Audio, output.StreamType);
        Assert.Equal("rus", output.Language);
    }

    [Fact]
    public async Task A_listed_extraction_still_reports_the_files_it_produces()
    {
        // Its outputs are the only record of what it makes — there is no single OutputPath to fall back on —
        // so a list that does not load them reports a job that produced nothing.
        var dub = AddStream(StreamType.Audio, 1, "ac3", "rus");
        var subtitle = AddStream(StreamType.Subtitle, 2, "subrip", "eng");
        await ExtractAsync(dub, subtitle);

        var listed = Assert.Single(await new TranscodeService(
                _context, _engine, new CatalogPathSandbox(), Settings,
                new LibraryMoveGuard(_context, new LibraryMoveQueue()),
                NullLogger<TranscodeService>.Instance)
            .ListAsync(CancellationToken.None));

        Assert.Equal("Extract", listed.Kind);
        Assert.Equal(
            ["The Rock (1996)/The Rock (1996).rus.mka", "The Rock (1996)/The Rock (1996).eng.srt"],
            listed.OutputPaths);
    }

    [Fact]
    public async Task A_second_job_for_a_file_one_is_already_writing_is_refused()
    {
        var dub = AddStream(StreamType.Audio, 1, "ac3", "rus");
        await ExtractAsync(dub);

        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() => ExtractAsync(dub));

        Assert.Contains("already producing", error.Message);
    }

    [Fact]
    public async Task A_track_already_out_is_refused_while_its_file_is_still_recorded()
    {
        var dub = AddStream(StreamType.Audio, 1, "ac3", "rus");
        await ExtractAsync(dub);
        await CompleteAsync();
        await Importer(new ProbedStream(StreamType.Audio, 0, "ac3", null, null, null, null, null, null, null, 6, 48000, 640000, false, false, null))
            .ImportAsync(await _context.TranscodeJobs.SingleAsync(), CancellationToken.None);

        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() => ExtractAsync(dub));

        Assert.Contains("already a file beside this version", error.Message);
    }

    [Fact]
    public async Task Removing_the_file_makes_the_track_extractable_again()
    {
        // The job history alone must not refuse: an operator who deleted the sidecar is asking for exactly
        // this.
        var dub = AddStream(StreamType.Audio, 1, "ac3", "rus");
        await ExtractAsync(dub);
        await CompleteAsync();
        var job = await _context.TranscodeJobs.SingleAsync();
        await Importer().ImportAsync(job, CancellationToken.None);
        _context.MediaStreams.RemoveRange(await _context.MediaStreams.Where(stream => stream.IsExternal).ToListAsync());
        await _context.SaveChangesAsync();

        var repeat = await ExtractAsync(dub);

        Assert.Single(repeat.OutputPaths);
    }

    // ── promotion ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_produced_file_becomes_an_external_stream_of_the_source_it_came_out_of()
    {
        var dub = AddStream(StreamType.Audio, 1, "ac3", "rus", "Гаврилов");
        await ExtractAsync(dub);
        await CompleteAsync();

        var promoted = await Importer(new ProbedStream(
                StreamType.Audio, 0, "ac3", null, "und", null, null, null, null, null, 6, 48000, 640000, false, false, null))
            .ImportAsync(await _context.TranscodeJobs.SingleAsync(), CancellationToken.None);

        Assert.True(promoted);
        var stream = await _context.MediaStreams.SingleAsync(entry => entry.IsExternal);
        Assert.Equal("The Rock (1996)/The Rock (1996).rus.mka", stream.ExternalPath);
        Assert.Equal(StreamType.Audio, stream.StreamType);
        // External indexes start past any container's own numbering.
        Assert.Equal(1000, stream.Index);
        // The specs come from the produced file, so the row reads like any other track.
        Assert.Equal("ac3", stream.Codec);
        Assert.Equal(6, stream.Channels);
        Assert.Equal(48000, stream.SampleRate);
        // The label it was extracted under, not what the file can be read back as: a .srt has nowhere to
        // hold a language, so re-reading one would unlabel every extracted subtitle.
        Assert.Equal("rus", stream.Language);
        Assert.Equal("Гаврилов", stream.Title);
    }

    [Fact]
    public async Task External_indexes_continue_past_the_ones_already_there()
    {
        AddStream(
            StreamType.Subtitle, 1007, "subrip", "eng",
            isExternal: true, externalPath: "The Rock (1996)/The Rock (1996).eng.srt");
        var dub = AddStream(StreamType.Audio, 1, "ac3", "rus");
        await ExtractAsync(dub);
        await CompleteAsync();

        await Importer().ImportAsync(await _context.TranscodeJobs.SingleAsync(), CancellationToken.None);

        var added = await _context.MediaStreams.SingleAsync(stream => stream.Index >= 1008);
        Assert.Equal(1008, added.Index);
    }

    [Fact]
    public async Task Importing_twice_records_the_track_once()
    {
        // A completion can be observed twice — the engine event and the reconcile tick, or across a restart.
        var dub = AddStream(StreamType.Audio, 1, "ac3", "rus");
        await ExtractAsync(dub);
        await CompleteAsync();
        var job = await _context.TranscodeJobs.SingleAsync();

        Assert.True(await Importer().ImportAsync(job, CancellationToken.None));
        Assert.True(await Importer().ImportAsync(job, CancellationToken.None));

        Assert.Single(await _context.MediaStreams.Where(stream => stream.IsExternal).ToListAsync());
    }

    [Fact]
    public async Task A_missing_output_fails_the_job_while_the_rest_is_still_recorded()
    {
        // Leaving a produced file with no row pointing at it is the one outcome the sidecar model exists to
        // prevent, so a partial result is recorded rather than discarded.
        var dub = AddStream(StreamType.Audio, 1, "ac3", "rus");
        var subtitle = AddStream(StreamType.Subtitle, 2, "subrip", "eng");
        await ExtractAsync(dub, subtitle);
        await CompleteAsync(only: "The Rock (1996)/The Rock (1996).rus.mka");
        var job = await _context.TranscodeJobs.SingleAsync();

        var promoted = await Importer().ImportAsync(job, CancellationToken.None);

        Assert.False(promoted);
        Assert.Contains("eng.srt", job.Error);
        var recorded = Assert.Single(await _context.MediaStreams.Where(stream => stream.IsExternal).ToListAsync());
        Assert.Equal("The Rock (1996)/The Rock (1996).rus.mka", recorded.ExternalPath);
    }

    /// <summary>Puts the job's files on disk, as a finished engine run would.</summary>
    private async Task CompleteAsync(string? only = null)
    {
        var job = await _context.TranscodeJobs.Include(entity => entity.Outputs).SingleAsync();
        foreach (var output in job.Outputs.Where(output => only is null || output.RelativePath == only))
        {
            File.WriteAllText(Path.Combine(_root, output.RelativePath.Replace('/', Path.DirectorySeparatorChar)), "track");
        }

        job.State = TranscodeJobState.Completed;
        await _context.SaveChangesAsync();
    }
}
