using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.Transcoding;
using Microsoft.Extensions.Logging.Abstractions;

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

        var error = await Assert.ThrowsAsync<TranscodeRequestException>(() =>
            ConvertAsync(new CreateTranscodeRequest(_episodeSourceId, "copy", null, null)));

        Assert.StartsWith("This title already has a version", error.Message);
        Assert.DoesNotContain("movie", error.Message);
    }
}
