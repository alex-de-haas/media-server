using System.Text;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.Transcoding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using MediaServer.Api.Probe;
using Imposter.Abstractions;

namespace MediaServer.Api.Tests.Transcoding;

/// <summary>
/// The whole conversion path for an episode, against a real database and a recording engine: a job lands
/// beside the episode in its season folder, a merge reads the sidecar beside it, and the two refusals a
/// version can meet — belonging to a series extra, and a path a version already holds — say so in words
/// that fit a title of either kind. The label and path arithmetic itself is covered by
/// <see cref="TranscodeServiceTests"/>.
/// </summary>
public sealed class TranscodeServiceConversionTests : IDisposable
{
    private const string SeasonFolder = "Breaking Bad (2008)/Season 01";
    private const string EpisodeRelative = SeasonFolder + "/Breaking Bad S01E01.mkv";
    private const string ExtraRelative = "Breaking Bad (2008)/extras/Making Of.mkv";

    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly string _root;
    private readonly RecordingTranscodeEngine _engine = new();

    private Guid _episodeId;
    private Guid _episodeSourceId;
    private Guid _extraSourceId;

    public TranscodeServiceConversionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ms-convert-" + Guid.NewGuid().ToString("N"));
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

    private MediaServerSettings Settings => new()
    {
        CatalogMountRoots = [new CatalogMount("catalogs", _root)],
    };

    private TranscodeService Service() =>
        new(_context, _engine, new CatalogPathSandbox(), Settings,
            new LibraryMoveGuard(_context, new LibraryMoveQueue()),
            NullLogger<TranscodeService>.Instance);

    private Task<TranscodeJobResponse> ConvertAsync(CreateTranscodeRequest request) =>
        Service().CreateAsync(request, CancellationToken.None);


    [Fact]
    public async Task Read_only_input_requires_an_export_before_contacting_engine()
    {
        (await _context.Catalogs.SingleAsync()).ReadOnly = true;
        await _context.SaveChangesAsync();
        await Assert.ThrowsAsync<ReadOnlyCatalogException>(() =>
            ConvertAsync(new(_episodeSourceId, "copy", null, null)));
        Assert.Null(_engine.Seen);
        Assert.Empty(_context.TranscodeJobs);
    }

    [Fact]
    public async Task Export_reads_original_mount_and_owns_separate_output_without_adding_a_library_version()
    {
        var outputRoot = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "ms-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputRoot);
        try
        {
            _engine.Tooling = new(false, IdempotentTranscodes: true);
            (await _context.Catalogs.SingleAsync()).ReadOnly = true;
            await _context.SaveChangesAsync();
            var settings = new MediaServerSettings
            {
                CatalogMountRoots = Settings.CatalogMountRoots,
                TranscodeOutputRoots = [new("results", outputRoot)],
            };
            var service = new TranscodeService(_context, _engine, new CatalogPathSandbox(), settings,
                new LibraryMoveGuard(_context, new LibraryMoveQueue()), NullLogger<TranscodeService>.Instance);
            var count = await _context.MediaSources.CountAsync();
            var response = await service.CreateAsync(new(_episodeSourceId, "copy", null, null, OutputMountLabel: "results"), default);
            Assert.Equal("catalogs", _engine.Seen!.InputMountLabel);
            Assert.Equal("results", _engine.Seen.OutputMountLabel);
            Assert.StartsWith(response.Id.ToString("N") + "/", _engine.Seen.OutputRelativePath);
            var job = await _context.TranscodeJobs.SingleAsync();
            var path = new TranscodeExports(settings).Resolve(job);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "export");
            var probe = IMediaProbe.Imposter();
            probe.ProbeAsync(Arg<string>.Any(), Arg<CancellationToken>.Any())
                .Returns(Task.FromResult(new ProbeResult("mkv", 6, null, 1000, [])));
            var importer = new TranscodeOutputImporter(_context, new CatalogPathSandbox(), probe.Instance(),
                NullLogger<TranscodeOutputImporter>.Instance, settings);
            Assert.True(await importer.ImportAsync(job, default));
            Assert.Equal(count, await _context.MediaSources.CountAsync());
            job.State = TranscodeJobState.Completed;
            await _context.SaveChangesAsync();
            Assert.Equal(path, await service.ExportFileAsync(job.Id, default));
            var deletion = new LibraryDeleteService(_context,
                new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));
            await Assert.ThrowsAsync<LibraryFileBusyException>(() => deletion.DeleteSourceAsync(_episodeSourceId, false, default));
            await Assert.ThrowsAsync<LibraryFileBusyException>(() => deletion.DeleteEpisodeAsync(_episodeId, false, false, default));
            var series = await _context.MediaItems.SingleAsync(item => item.Kind == MediaKind.Series);
            await Assert.ThrowsAsync<LibraryFileBusyException>(() => deletion.DeleteAsync(series.Id, false, true, default));
            await Assert.ThrowsAsync<LibraryFileBusyException>(() => new CatalogService(_context, null!, settings).DeleteAsync(job.CatalogId, default));
            Assert.Equal(job.Id, (await _context.TranscodeJobs.SingleAsync()).Id);
            Assert.True(File.Exists(path));
            Assert.True(await service.RemoveAsync(job.Id, true, default));
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFileSystemEntries(outputRoot));
            Assert.True(File.Exists(Path.Combine(_root, EpisodeRelative)));
        }
        finally { Directory.Delete(outputRoot, true); }
    }

    [Fact]
    public async Task Export_requires_engine_capability_before_persisting_or_submitting()
    {
        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() =>
            ConvertAsync(new(_episodeSourceId, "copy", null, null, OutputMountLabel: "results")));
        Assert.Contains("0.14.0", error.Message);
        Assert.Empty(await _context.TranscodeJobs.ToListAsync());
        Assert.Null(_engine.Seen);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Unconfirmed_exports_reconcile_the_same_id_before_completion_or_cancellation(bool accepted, bool cancel)
    {
        var output = Directory.CreateDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "export-retry-" + Guid.NewGuid().ToString("N")));
        try
        {
            var settings = new MediaServerSettings { CatalogMountRoots = Settings.CatalogMountRoots,
                TranscodeOutputRoots = [new("results", output.FullName)] };
            var engine = ITranscodeEngine.Imposter();
            var requests = new List<TranscodeJobRequest>();
            JobSnapshot? snapshot = null;
            engine.GetToolingAsync(Arg<CancellationToken>.Any()).Returns(Task.FromResult(new TranscodeTooling(false, IdempotentTranscodes: true)));
            engine.CreateAsync(Arg<TranscodeJobRequest>.Any(), Arg<CancellationToken>.Any())
                .Returns((TranscodeJobRequest request, CancellationToken _) =>
                {
                    requests.Add(request);
                    if (accepted || requests.Count > 1)
                        snapshot = new(request.ClientJobId!.Value.ToString("N"), "export", "Running", false, 5, 0, 0, 0, null);
                    return requests.Count == 1
                        ? Task.FromException<JobDescriptor>(new HttpRequestException("lost response"))
                        : Task.FromResult(new JobDescriptor(request.ClientJobId!.Value.ToString("N"), request.InputRelativePath, request.OutputRelativePath, 1, 1));
                });
            engine.InspectAsync(Arg<string>.Any(), Arg<CancellationToken>.Any())
                .Returns((string id, CancellationToken _) => { if (snapshot is not null) Assert.Equal(snapshot.JobId, id); return Task.FromResult(snapshot!); });
            engine.CancelAsync(Arg<string>.Any(), Arg<CancellationToken>.Any())
                .Returns((string id, CancellationToken _) => { Assert.Equal(snapshot!.JobId, id); snapshot = snapshot with { State = "Cancelled" }; return Task.CompletedTask; });
            var service = new TranscodeService(_context, engine.Instance(), new CatalogPathSandbox(), settings,
                new LibraryMoveGuard(_context, new LibraryMoveQueue()), NullLogger<TranscodeService>.Instance);
            var response = await service.CreateAsync(new(_episodeSourceId, "copy", null, null, OutputMountLabel: "results"), default);
            var job = await _context.TranscodeJobs.SingleAsync();
            Assert.Equal(response.Id.ToString("N"), job.EngineJobId);
            Assert.Contains("unconfirmed", job.Error);
            Assert.NotNull(job.ExportSubmissionJson);
            await Assert.ThrowsAsync<LibraryFileBusyException>(() => service.RemoveAsync(job.Id, true, default));
            if (cancel) await service.CancelAsync(job.Id, default);
            job.LastSubmissionAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await _context.SaveChangesAsync();
            var probe = IMediaProbe.Imposter();
            probe.ProbeAsync(Arg<string>.Any(), Arg<CancellationToken>.Any())
                .Returns(Task.FromResult(new ProbeResult("mkv", 10, null, 100, [])));
            var importer = new TranscodeOutputImporter(_context, new CatalogPathSandbox(), probe.Instance(),
                NullLogger<TranscodeOutputImporter>.Instance, settings);
            await service.ReconcileExportAsync(job, importer, default);
            Assert.Equal(accepted ? 1 : 2, requests.Count);
            Assert.All(requests, request => Assert.Equal(response.Id, request.ClientJobId));
            if (requests.Count == 2) Assert.Equal(System.Text.Json.JsonSerializer.Serialize(requests[0]), System.Text.Json.JsonSerializer.Serialize(requests[1]));
            if (cancel) Assert.Equal(TranscodeJobState.Cancelled, job.State);
            else
            {
                Assert.Equal(TranscodeJobState.Running, job.State);
                snapshot = snapshot! with { State = "Completed", Complete = true, PercentComplete = 100 };
                var path = new TranscodeExports(settings).Resolve(job);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "output");
                await service.ReconcileExportAsync(job, importer, default);
                Assert.Equal(TranscodeJobState.Completed, job.State);
                Assert.True(job.OutputImported);
                Assert.Equal(path, await service.ExportFileAsync(job.Id, default));
            }
        }
        finally { output.Delete(true); }
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("hevc")]
    public async Task Explicit_video_and_name_reach_engine_with_exact_audio_and_subtitles(string codec)
    {
        _engine.Tooling = new(false, ExplicitVideoSelection: true);
        var video = new MediaStream { Id = Guid.NewGuid(), MediaSourceId = _episodeSourceId, StreamType = StreamType.Video, Index = 4, Height = 480 };
        _context.MediaStreams.Add(video);
        await _context.SaveChangesAsync();
        await ConvertAsync(new(_episodeSourceId, codec, null, null, MaxHeight: codec == "copy" ? null : 720,
            AudioStreamIndexes: [1], SubtitleStreamIndexes: [], VideoStreamIndex: 4,
            MetadataEdits: [new(video.Id, Title: "Selected picture")]));
        Assert.Equal(4, _engine.Seen!.VideoStreamIndex);
        Assert.Null(_engine.Seen.MaxHeight);
        Assert.Equal([1], _engine.Seen.AudioStreamIndexes);
        Assert.Empty(_engine.Seen.SubtitleStreamIndexes!);
        Assert.Equal(new EngineMetadataOverride(0, 4, null, "Selected picture"), Assert.Single(_engine.Seen.MetadataOverrides!));
    }

    [Fact]
    public async Task Old_engine_cannot_silently_ignore_explicit_video()
    {
        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() => ConvertAsync(new(_episodeSourceId, "copy", null, null,
            AudioStreamIndexes: [1], SubtitleStreamIndexes: [], VideoStreamIndex: 0)));
        Assert.Contains("Update Transcode Engine", error.Message);
        Assert.Null(_engine.Seen);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(999)]
    public async Task Wrong_or_missing_video_is_rejected(int index)
    {
        _engine.Tooling = new(false, ExplicitVideoSelection: true);
        await Assert.ThrowsAsync<TranscodeRequestException>(() => ConvertAsync(new(_episodeSourceId, "copy", null, null,
            AudioStreamIndexes: [1], SubtitleStreamIndexes: [], VideoStreamIndex: index)));
        Assert.Null(_engine.Seen);
    }

    [Theory]
    [InlineData("mjpeg", "copy")]
    [InlineData("PNG", "copy")]
    [InlineData("bmp", "copy")]
    [InlineData("gif", "hevc")]
    [InlineData("webp", "hevc")]
    [InlineData("mjpeg", "hevc")]
    public async Task Cover_art_cannot_replace_the_film_video(string imageCodec, string outputCodec)
    {
        _engine.Tooling = new(false, ExplicitVideoSelection: true);
        _context.MediaStreams.Add(new MediaStream
        {
            Id = Guid.NewGuid(), MediaSourceId = _episodeSourceId, StreamType = StreamType.Video,
            Index = 4, Codec = imageCodec,
        });
        await _context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() => ConvertAsync(new(
            _episodeSourceId, outputCodec, null, null,
            AudioStreamIndexes: [1], SubtitleStreamIndexes: [], VideoStreamIndex: 4)));

        Assert.Contains("cover art", error.Message);
        Assert.Null(_engine.Seen);
        Assert.Empty(await _context.TranscodeJobs.ToListAsync());
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("hevc")]
    public async Task Mjpeg_is_selectable_when_it_is_the_only_video(string outputCodec)
    {
        _engine.Tooling = new(false, ExplicitVideoSelection: true);
        var video = await _context.MediaStreams.SingleAsync(s => s.MediaSourceId == _episodeSourceId && s.Index == 0);
        video.Codec = "mjpeg";
        await _context.SaveChangesAsync();

        await ConvertAsync(new(_episodeSourceId, outputCodec, null, null,
            AudioStreamIndexes: [1], SubtitleStreamIndexes: [], VideoStreamIndex: 0));

        Assert.Equal(0, _engine.Seen!.VideoStreamIndex);
    }

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
        var catalogId = Guid.NewGuid();
        _episodeId = Guid.NewGuid();
        _episodeSourceId = Guid.NewGuid();
        _extraSourceId = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var extraId = Guid.NewGuid();

        _context.Catalogs.Add(new Catalog
        {
            Id = catalogId, Name = "Shows", Type = CatalogType.Series, Root = _root, CreatedAt = now, UpdatedAt = now,
        });
        _context.MediaItems.AddRange(
            new MediaItem
            {
                Id = seriesId, PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalogId,
                Kind = MediaKind.Series, Title = "Breaking Bad", Year = 2008, AddedAt = now, UpdatedAt = now,
            },
            new MediaItem
            {
                Id = _episodeId, PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalogId,
                Kind = MediaKind.Episode, Title = "Pilot", ParentId = seriesId, SeriesId = seriesId,
                ParentIndexNumber = 1, IndexNumber = 1, LibraryPath = EpisodeRelative, AddedAt = now, UpdatedAt = now,
            },
            new MediaItem
            {
                Id = extraId, PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalogId,
                Kind = MediaKind.Video, Title = "Making Of", ParentId = seriesId, SeriesId = seriesId,
                LibraryPath = ExtraRelative, AddedAt = now, UpdatedAt = now,
            });
        _context.MediaSources.AddRange(
            new MediaSource
            {
                Id = _episodeSourceId, MediaItemId = _episodeId, Container = "mkv", Path = EpisodeRelative,
                SizeBytes = 1000, DurationTicks = 1, CreatedAt = now,
            },
            new MediaSource
            {
                Id = _extraSourceId, MediaItemId = extraId, Container = "mkv", Path = ExtraRelative,
                SizeBytes = 1000, DurationTicks = 1, CreatedAt = now,
            });
        _context.MediaStreams.AddRange(
            new MediaStream
            {
                Id = Guid.NewGuid(), MediaSourceId = _episodeSourceId, StreamType = StreamType.Video, Index = 0,
                Codec = "hevc", Width = 3840, Height = 2160,
            },
            new MediaStream
            {
                Id = Guid.NewGuid(), MediaSourceId = _episodeSourceId, StreamType = StreamType.Audio, Index = 1,
                Codec = "eac3", Language = "eng", Channels = 6,
            },
            new MediaStream
            {
                Id = Guid.NewGuid(), MediaSourceId = _extraSourceId, StreamType = StreamType.Video, Index = 0,
                Codec = "h264", Width = 1920, Height = 1080,
            });
        _context.SaveChanges();

        WriteFile(EpisodeRelative);
        WriteFile(ExtraRelative);
    }

    private void WriteFile(string relative)
    {
        var absolute = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "video");
    }

    [Fact]
    public async Task An_episode_converts_beside_itself_in_the_season_folder()
    {
        // The output is named beside its input by path, so an episode's version lands in the season folder
        // with the episode token in its name — the same rule a movie's does, with nothing written for it.
        var job = await ConvertAsync(new CreateTranscodeRequest(_episodeSourceId, "hevc", null, null, MaxHeight: 1080));

        Assert.Equal(SeasonFolder + "/Breaking Bad S01E01 - HEVC 1080p.mkv", job.OutputPath);
        Assert.Equal(_episodeId, job.MediaItemId);
        Assert.Equal(EpisodeRelative, _engine.Seen!.InputRelativePath);
        Assert.Equal(1080, _engine.Seen.MaxHeight);
    }

    [Fact]
    public async Task An_episodes_sidecar_merges_the_way_a_movies_does()
    {
        var sidecarRelative = SeasonFolder + "/Breaking Bad S01E01.rus.mka";
        WriteFile(sidecarRelative);
        var dub = new MediaStream
        {
            Id = Guid.NewGuid(), MediaSourceId = _episodeSourceId, StreamType = StreamType.Audio, Index = 1000,
            Codec = "ac3", Language = "rus", IsExternal = true, ExternalPath = sidecarRelative,
        };
        _context.MediaStreams.Add(dub);
        await _context.SaveChangesAsync();

        var job = await ConvertAsync(new CreateTranscodeRequest(_episodeSourceId, null, null, null, MergeStreamIds: [dub.Id]));

        Assert.Equal(SeasonFolder + "/Breaking Bad S01E01 - Merged.mkv", job.OutputPath);
        Assert.Equal("copy", job.VideoCodec);
        var input = Assert.Single(_engine.Seen!.AdditionalInputs!);
        Assert.Equal(sidecarRelative, input.RelativePath);
    }

    [Theory]
    [InlineData("copy", MediaKind.Movie, true)]
    [InlineData("copy", MediaKind.Episode, false)]
    [InlineData("hevc", MediaKind.Movie, false)]
    [InlineData("hevc", MediaKind.Episode, true)]
    public async Task Another_versions_external_tracks_can_join_the_selected_container(string codec, MediaKind kind, bool embeddedAudio)
    {
        _engine.Tooling = new(false, ExplicitVideoSelection: true);
        (await _context.MediaItems.FindAsync(_episodeId))!.Kind = kind;
        var sibling = new MediaSource { Id = Guid.NewGuid(), MediaItemId = _episodeId, Container = "mkv", Path = SeasonFolder + "/Original.mkv" };
        var audioPath = SeasonFolder + "/Original.eng.mka";
        var subtitlePath = SeasonFolder + "/Original.eng.srt";
        WriteFile(audioPath);
        WriteFile(subtitlePath);
        var audio = new MediaStream { Id = Guid.NewGuid(), MediaSourceId = sibling.Id, StreamType = StreamType.Audio, Index = 1000, IsExternal = true, ExternalPath = audioPath };
        var subtitle = new MediaStream { Id = Guid.NewGuid(), MediaSourceId = sibling.Id, StreamType = StreamType.Subtitle, Index = 1001, IsExternal = true, ExternalPath = subtitlePath };
        _context.MediaSources.Add(sibling);
        _context.MediaStreams.AddRange(audio, subtitle);
        await _context.SaveChangesAsync();

        var job = await ConvertAsync(new(_episodeSourceId, codec, null, null,
            VideoStreamIndex: 0, AudioStreamIndexes: embeddedAudio ? [1] : [], SubtitleStreamIndexes: [],
            MergeStreamIds: [subtitle.Id, audio.Id],
            MetadataEdits: [new(audio.Id, Title: "Dub"), new(subtitle.Id, Title: "Translation")]));

        Assert.Equal(_episodeId, job.MediaItemId);
        Assert.Equal(0, _engine.Seen!.VideoStreamIndex);
        Assert.Equal(embeddedAudio ? [1] : [], _engine.Seen.AudioStreamIndexes);
        Assert.Collection(_engine.Seen.AdditionalInputs!, input =>
        {
            Assert.Equal("catalogs", input.MountLabel);
            Assert.Equal(audioPath, input.RelativePath);
            Assert.Equal([0], input.AudioStreamIndexes);
            Assert.Null(input.SubtitleStreamIndexes);
        }, input =>
        {
            Assert.Equal("catalogs", input.MountLabel);
            Assert.Equal(subtitlePath, input.RelativePath);
            Assert.Equal([0], input.SubtitleStreamIndexes);
            Assert.Null(input.AudioStreamIndexes);
        });
        Assert.Equal(new EngineMetadataOverride(1, 0, null, "Dub"), _engine.Seen.MetadataOverrides![0]);
        Assert.Equal(new EngineMetadataOverride(2, 0, null, "Translation"), _engine.Seen.MetadataOverrides[1]);
        Assert.True(File.Exists(Path.Combine(_root, audioPath)));
        Assert.True(File.Exists(Path.Combine(_root, subtitlePath)));
    }

    [Theory]
    [InlineData(true, StreamType.Audio, true)]
    [InlineData(false, StreamType.Audio, false)]
    [InlineData(false, StreamType.Video, true)]
    public async Task Merge_refuses_another_title_embedded_tracks_and_external_video(bool foreignTitle, StreamType type, bool external)
    {
        _engine.Tooling = new(false, ExplicitVideoSelection: true);
        var path = SeasonFolder + "/Rejected.mka";
        WriteFile(path);
        var track = new MediaStream { Id = Guid.NewGuid(), MediaSourceId = foreignTitle ? _extraSourceId : _episodeSourceId,
            StreamType = type, Index = 1000, IsExternal = external, ExternalPath = path };
        _context.MediaStreams.Add(track);
        await _context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() => ConvertAsync(new(_episodeSourceId, "copy", null, null,
            VideoStreamIndex: 0, AudioStreamIndexes: [1], SubtitleStreamIndexes: [], MergeStreamIds: [track.Id])));

        Assert.Contains("external audio or subtitle file of this title", error.Message);
        Assert.Null(_engine.Seen);
        Assert.Empty(await _context.TranscodeJobs.ToListAsync());
    }

    [Fact]
    public async Task A_series_extra_is_refused_by_name()
    {
        // An extra has no surface it could be reached from; admitting it would be a promise nothing
        // displays, so the refusal says what the version belongs to rather than "not a movie".
        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() =>
            ConvertAsync(new CreateTranscodeRequest(_extraSourceId, "hevc", null, null)));

        Assert.Contains("series extra", error.Message);
        Assert.Contains("movie or an episode", error.Message);
    }

    [Fact]
    public async Task A_path_a_version_already_holds_is_refused_in_words_that_fit_an_episode()
    {
        // The refusal used to say "this movie"; an episode's version meets it for the same reason and must
        // not be told it is a film.
        _context.MediaSources.Add(new MediaSource
        {
            Id = Guid.NewGuid(), MediaItemId = _episodeId, Container = "mkv",
            Path = SeasonFolder + "/Breaking Bad S01E01 - Remux.mkv", CreatedAt = DateTimeOffset.UtcNow,
        });
        await _context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<TranscodeVersionNameException>(() =>
            ConvertAsync(new CreateTranscodeRequest(_episodeSourceId, "copy", null, null)));

        Assert.Equal("A version with this name already exists. Choose another name.", error.Message);
        Assert.DoesNotContain("movie", error.Message);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("hevc")]
    public async Task Custom_version_name_survives_job_reload_and_output_import(string codec)
    {
        var response = await ConvertAsync(new(_episodeSourceId, codec, null, null, VersionName: "  My   cut.  "));
        Assert.Equal(SeasonFolder + "/Breaking Bad S01E01 - My cut.mkv", response.OutputPath);
        Assert.Equal(response.OutputPath, _engine.Seen!.OutputRelativePath);
        _context.ChangeTracker.Clear();
        var job = await _context.TranscodeJobs.SingleAsync();
        Assert.Equal("My cut", job.VersionName);
        WriteFile(job.OutputPath!);
        var probe = IMediaProbe.Imposter();
        probe.ProbeAsync(Arg<string>.Any(), Arg<CancellationToken>.Any())
            .Returns(Task.FromResult(new ProbeResult("mkv", 1, null, 1000, [])));
        var importer = new TranscodeOutputImporter(_context, new CatalogPathSandbox(), probe.Instance(), NullLogger<TranscodeOutputImporter>.Instance);
        Assert.True(await importer.ImportAsync(job, CancellationToken.None));
        Assert.Equal("My cut", (await _context.MediaSources.SingleAsync(source => source.Path == job.OutputPath)).VersionName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../outside")]
    [InlineData("folder\\name")]
    [InlineData("bad:name")]
    [InlineData("...")]
    [InlineData("name\u0000")]
    public async Task Invalid_version_names_are_refused_before_engine_submission(string name)
    {
        await Assert.ThrowsAsync<TranscodeVersionNameException>(() => ConvertAsync(new(_episodeSourceId, "copy", null, null, VersionName: name)));
        Assert.Null(_engine.Seen);
    }

    [Fact]
    public async Task Too_long_name_is_refused()
    {
        await Assert.ThrowsAsync<TranscodeVersionNameException>(() => ConvertAsync(new(_episodeSourceId, "copy", null, null, VersionName: new string('x', 121))));
        Assert.Null(_engine.Seen);
    }

    [Theory]
    [InlineData(MediaKind.Movie, false, 216)]
    [InlineData(MediaKind.Movie, false, 217)]
    [InlineData(MediaKind.Movie, true, 216)]
    [InlineData(MediaKind.Movie, true, 217)]
    [InlineData(MediaKind.Episode, false, 216)]
    [InlineData(MediaKind.Episode, false, 217)]
    [InlineData(MediaKind.Episode, true, 216)]
    [InlineData(MediaKind.Episode, true, 217)]
    public async Task Named_output_reserves_filename_bytes_for_the_engine_temporary_file(MediaKind kind, bool unicode, int outputBytes)
    {
        var name = new string(unicode ? 'я' : 'x', 80);
        // Movie: "title - name.mkv"; episode: "title S01E01 - name.mkv".
        var title = new string('T', outputBytes - Encoding.UTF8.GetByteCount(name) - (kind == MediaKind.Movie ? 7 : 14));
        var item = await _context.MediaItems.SingleAsync(item => item.Id == _episodeId);
        if (kind == MediaKind.Movie)
        {
            item.Kind = kind;
            item.Title = title;
            item.SeriesId = null;
            item.ParentId = null;
            item.LibraryPath = $"{title}/source.mkv";
            (await _context.MediaSources.SingleAsync(source => source.Id == _episodeSourceId)).Path = item.LibraryPath;
            WriteFile(item.LibraryPath);
        }
        else (await _context.MediaItems.SingleAsync(series => series.Id == item.SeriesId)).Title = title;
        await _context.SaveChangesAsync();

        var request = new CreateTranscodeRequest(_episodeSourceId, "copy", null, null, VersionName: name);
        if (outputBytes == 216)
        {
            var job = await ConvertAsync(request);
            Assert.Equal(outputBytes, Encoding.UTF8.GetByteCount(Path.GetFileName(job.OutputPath!)));
            Assert.NotNull(_engine.Seen);
        }
        else
        {
            var error = await Assert.ThrowsAsync<TranscodeVersionNameException>(() => ConvertAsync(request));
            Assert.Contains("filename is too long", error.Message);
            Assert.Null(_engine.Seen);
            Assert.Empty(await _context.TranscodeJobs.ToListAsync());
        }
    }

    [Theory]
    [InlineData("label")]
    [InlineData("path")]
    [InlineData("file")]
    [InlineData("job")]
    public async Task Existing_names_and_outputs_cannot_be_overwritten(string conflict)
    {
        var target = SeasonFolder + "/Breaking Bad S01E01 - My cut.mkv";
        if (conflict == "file") WriteFile(target);
        else if (conflict == "job") await ConvertAsync(new(_episodeSourceId, "copy", null, null, VersionName: "MY CUT"));
        else
        {
            _context.MediaSources.Add(new MediaSource { Id = Guid.NewGuid(), MediaItemId = _episodeId, Container = "mkv",
                Path = conflict == "path" ? target : SeasonFolder + "/other-file.mkv", VersionName = conflict == "label" ? "MY CUT" : null,
                CreatedAt = DateTimeOffset.UtcNow });
            await _context.SaveChangesAsync();
        }
        var before = _engine.Seen;
        var error = await Assert.ThrowsAsync<TranscodeVersionNameException>(() => ConvertAsync(new(_episodeSourceId, "copy", null, null, VersionName: "My cut")));
        Assert.Contains("Choose another name", error.Message);
        Assert.Same(before, _engine.Seen);
        if (conflict == "file") Assert.Equal("video", File.ReadAllText(Path.Combine(_root, target)));
    }

    [Fact]
    public async Task Naming_a_movie_version_uses_its_title_without_stacking_source_suffixes()
    {
        var item = await _context.MediaItems.SingleAsync(item => item.Id == _episodeId);
        item.Kind = MediaKind.Movie;
        item.Title = "Arrival"; item.Year = 2016; item.SeriesId = null;
        item.LibraryPath = "Arrival (2016)/Arrival (2016) - Old.mkv";
        var source = await _context.MediaSources.SingleAsync(source => source.Id == _episodeSourceId);
        source.Path = item.LibraryPath; source.VersionName = "Old";
        WriteFile(source.Path);
        await _context.SaveChangesAsync();
        var job = await ConvertAsync(new(_episodeSourceId, "copy", null, null, VersionName: "Remux"));
        Assert.Equal("Arrival (2016)/Arrival (2016) - Remux.mkv", job.OutputPath);
    }
}
