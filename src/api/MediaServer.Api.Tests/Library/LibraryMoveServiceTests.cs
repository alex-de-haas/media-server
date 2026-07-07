using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using MediaServer.Api.Jobs;
using MediaServer.Api.Library;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Realtime;
using MediaServer.Api.Tests.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// Coverage for moving a published item between catalogs (<see cref="LibraryMoveService"/> +
/// <see cref="LibraryMoveCoordinator"/>): re-point (no collision) preserves the internal id and dependent
/// data; merge (collision) folds sources into the existing target as versions/episodes and prunes the
/// source; cross-volume copies then deletes; and the coordinator rejects invalid moves.
/// </summary>
public sealed class LibraryMoveServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly LibraryMoveService _service;
    private readonly LibraryMoveCoordinator _coordinator;
    private readonly LibraryMoveQueue _queue = new();
    private readonly RecordingJobNotifier _notifier = new();
    private readonly JobService _jobs;
    private readonly FakeFilesystem _filesystem;
    private readonly string _sourceRoot = Path.Combine(Path.GetTempPath(), "ms-move-src-" + Guid.NewGuid().ToString("N"));
    private readonly string _targetRoot = Path.Combine(Path.GetTempPath(), "ms-move-dst-" + Guid.NewGuid().ToString("N"));

    public LibraryMoveServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        CatalogPaths.For(_sourceRoot).EnsureCreated();
        CatalogPaths.For(_targetRoot).EnsureCreated();

        _jobs = new JobService(_database, _notifier);
        _filesystem = new FakeFilesystem(_targetRoot);
        _service = new LibraryMoveService(_database, new CatalogPathSandbox(), _filesystem, _jobs, NullLogger<LibraryMoveService>.Instance);
        _coordinator = new LibraryMoveCoordinator(_database, _filesystem, _jobs, _queue);
    }

    [Fact]
    public async Task Move_movie_repoint_changes_catalog_preserves_id_and_dependents()
    {
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Movie, "Movies HD");
        var target = await AddCatalogAsync(_targetRoot, CatalogType.Movie, "Movies 4K");
        var movie = await AddMovieAsync(source, "Inception", 2010, "tmdb", "27205", "bytes");

        var result = await MoveAsync(movie.Id, target.Id);

        Assert.Equal(MoveResult.Kind.Ok, result.Status);
        Assert.Equal(movie.Id, result.ResultId); // re-point keeps the same row

        var moved = await _database.MediaItems.AsNoTracking().SingleAsync(item => item.Id == movie.Id);
        Assert.Equal(target.Id, moved.CatalogId);
        Assert.Equal(PublicIdFactory.ForMovie(target.Id, "tmdb", "27205"), moved.PublicId);
        Assert.Equal("Inception (2010)/Inception (2010).mkv", moved.LibraryPath);

        // The file crossed to the target root and left the source root.
        Assert.True(File.Exists(Path.Combine(_targetRoot, "Inception (2010)", "Inception (2010).mkv")));
        Assert.False(File.Exists(Path.Combine(_sourceRoot, "Inception (2010)", "Inception (2010).mkv")));

        // Source row + owning ingest item followed; dependent metadata survived (keyed on the stable id).
        var mediaSource = Assert.Single(await _database.MediaSources.AsNoTracking().ToListAsync());
        Assert.Equal("Inception (2010)/Inception (2010).mkv", mediaSource.Path);
        var sourceFile = Assert.Single(await _database.SourceFiles.AsNoTracking().ToListAsync());
        Assert.Equal("Inception (2010)/Inception (2010).mkv", sourceFile.RelativePath);
        var ingest = Assert.Single(await _database.IngestItems.AsNoTracking().ToListAsync());
        Assert.Equal(target.Id, ingest.CatalogId);
        Assert.True(await _database.MetadataRecords.AsNoTracking().AnyAsync(record => record.MediaItemId == movie.Id));
    }

    [Fact]
    public async Task Move_movie_into_catalog_that_has_it_merges_as_a_version_and_prunes_source()
    {
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Movie, "Movies HD");
        var target = await AddCatalogAsync(_targetRoot, CatalogType.Movie, "Movies 4K");
        var existing = await AddMovieAsync(target, "Inception", 2010, "tmdb", "27205", "existing-4k");
        var incoming = await AddMovieAsync(source, "Inception", 2010, "tmdb", "27205", "incoming-hd");

        var result = await MoveAsync(incoming.Id, target.Id);

        Assert.Equal(MoveResult.Kind.Ok, result.Status);
        Assert.Equal(existing.Id, result.ResultId); // merged into the existing target item

        // The source movie is gone; the target now has two versions with distinct paths.
        Assert.False(await _database.MediaItems.AsNoTracking().AnyAsync(item => item.Id == incoming.Id));
        var sources = await _database.MediaSources.AsNoTracking().Where(s => s.MediaItemId == existing.Id).ToListAsync();
        Assert.Equal(2, sources.Count);
        Assert.Equal(2, sources.Select(s => s.Path).Distinct().Count());
        Assert.Contains(sources, s => s.VersionName == "Movies HD"); // labelled by the source catalog on collision

        // Both files live under the target root.
        Assert.True(File.Exists(Path.Combine(_targetRoot, "Inception (2010)", "Inception (2010).mkv")));
        Assert.True(File.Exists(Path.Combine(_targetRoot, "Inception (2010)", "Inception (2010) - Movies HD.mkv")));
        Assert.False(Directory.Exists(Path.Combine(_sourceRoot, "Inception (2010)")));

        // The owning ingest items follow to the target catalog and point at the surviving target item (not the
        // pruned source), so their durable MediaItemId is never left dangling.
        var ingestItems = await _database.IngestItems.AsNoTracking().ToListAsync();
        Assert.All(ingestItems, ingest => Assert.Equal(target.Id, ingest.CatalogId));
        Assert.All(ingestItems, ingest => Assert.Equal(existing.Id, ingest.MediaItemId));
    }

    [Fact]
    public async Task Move_series_repoint_moves_the_whole_subtree()
    {
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Series, "Series");
        var target = await AddCatalogAsync(_targetRoot, CatalogType.Series, "Series 4K");
        var series = await AddSeriesAsync(source, "The Show", "tmdb", "500", [(1, 1), (1, 2)]);

        var result = await MoveAsync(series.Id, target.Id);

        Assert.Equal(MoveResult.Kind.Ok, result.Status);
        Assert.Equal(series.Id, result.ResultId);

        // Series, season, and episodes all moved to the target catalog with re-minted ids.
        var items = await _database.MediaItems.AsNoTracking().Where(item => item.SeriesId == series.Id || item.Id == series.Id).ToListAsync();
        Assert.All(items, item => Assert.Equal(target.Id, item.CatalogId));
        var movedSeries = items.Single(item => item.Id == series.Id);
        Assert.Equal(PublicIdFactory.ForSeries(target.Id, "tmdb", "500"), movedSeries.PublicId);

        Assert.True(File.Exists(Path.Combine(_targetRoot, "The Show (2020)", "Season 01", "The Show S01E01.mkv")));
        Assert.True(File.Exists(Path.Combine(_targetRoot, "The Show (2020)", "Season 01", "The Show S01E02.mkv")));
        Assert.False(Directory.Exists(Path.Combine(_sourceRoot, "The Show (2020)")));
    }

    [Fact]
    public async Task Move_series_into_existing_series_merges_per_episode()
    {
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Series, "Series");
        var target = await AddCatalogAsync(_targetRoot, CatalogType.Series, "Series 4K");
        var targetSeries = await AddSeriesAsync(target, "The Show", "tmdb", "500", [(1, 1)]);
        var sourceSeries = await AddSeriesAsync(source, "The Show", "tmdb", "500", [(1, 1), (1, 2)]);

        var result = await MoveAsync(sourceSeries.Id, target.Id);

        Assert.Equal(MoveResult.Kind.Ok, result.Status);
        Assert.Equal(targetSeries.Id, result.ResultId);

        // The source series/season are pruned; the target series now owns both episodes.
        Assert.False(await _database.MediaItems.AsNoTracking().AnyAsync(item => item.Id == sourceSeries.Id));
        var targetEpisodes = await _database.MediaItems.AsNoTracking()
            .Where(item => item.Kind == MediaKind.Episode && item.SeriesId == targetSeries.Id).ToListAsync();
        Assert.Equal(2, targetEpisodes.Count);

        // E01 collided → merged as a second version; E02 was re-pointed under the target series.
        var e01 = targetEpisodes.Single(e => (e.IdentityEpisodeNumber ?? e.IndexNumber) == 1);
        Assert.Equal(2, await _database.MediaSources.AsNoTracking().CountAsync(s => s.MediaItemId == e01.Id));
        Assert.True(File.Exists(Path.Combine(_targetRoot, "The Show (2020)", "Season 01", "The Show S01E02.mkv")));
    }

    [Fact]
    public async Task Move_across_volumes_copies_then_deletes_the_source()
    {
        _filesystem.CrossVolume = true;
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Movie, "Movies HD");
        var target = await AddCatalogAsync(_targetRoot, CatalogType.Movie, "Movies 4K");
        var movie = await AddMovieAsync(source, "Dune", 2021, "tmdb", "438631", "payload");

        var result = await MoveAsync(movie.Id, target.Id);

        Assert.Equal(MoveResult.Kind.Ok, result.Status);
        var newAbsolute = Path.Combine(_targetRoot, "Dune (2021)", "Dune (2021).mkv");
        Assert.True(File.Exists(newAbsolute));
        Assert.Equal("payload", await File.ReadAllTextAsync(newAbsolute));
        Assert.False(File.Exists(Path.Combine(_sourceRoot, "Dune (2021)", "Dune (2021).mkv")));
    }

    [Fact]
    public async Task Move_across_volumes_reports_incremental_progress()
    {
        // A single large file must tick as its bytes copy — not sit at 0% and jump to 100% at the end (the old
        // per-file behaviour, which reported nothing useful for the common one-file movie).
        _filesystem.CrossVolume = true;
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Movie, "Movies HD");
        var target = await AddCatalogAsync(_targetRoot, CatalogType.Movie, "Movies 4K");
        var movie = await AddMovieAsync(source, "Dune", 2021, "tmdb", "438631", new string('x', 3 << 20)); // 3 MB / 1 MB chunks

        var result = await MoveAsync(movie.Id, target.Id);

        Assert.Equal(MoveResult.Kind.Ok, result.Status);
        var progress = _notifier.JobProgressValues;
        Assert.Contains(progress, percent => percent is > 0 and < 100); // at least one mid-copy tick
        Assert.Equal(100, progress[^1]); // finishes at 100
    }

    [Fact]
    public async Task Request_rejects_an_incompatible_target_type()
    {
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Movie, "Movies");
        var target = await AddCatalogAsync(_targetRoot, CatalogType.Series, "Shows");
        var movie = await AddMovieAsync(source, "Inception", 2010, "tmdb", "27205", "bytes");

        var result = await _coordinator.RequestAsync(movie.Id, target.Id, CancellationToken.None);

        Assert.Equal(LibraryMoveRequestStatus.IncompatibleType, result.Status);
        Assert.Null(result.JobId);
    }

    [Fact]
    public async Task Request_rejects_moving_into_the_same_catalog()
    {
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Movie, "Movies");
        var movie = await AddMovieAsync(source, "Inception", 2010, "tmdb", "27205", "bytes");

        var result = await _coordinator.RequestAsync(movie.Id, source.Id, CancellationToken.None);

        Assert.Equal(LibraryMoveRequestStatus.SameCatalog, result.Status);
    }

    [Fact]
    public async Task Request_refuses_a_cross_volume_move_that_does_not_fit()
    {
        _filesystem.CrossVolume = true;
        _filesystem.FreeBytes = 1; // smaller than the seeded file
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Movie, "Movies HD");
        var target = await AddCatalogAsync(_targetRoot, CatalogType.Movie, "Movies 4K");
        var movie = await AddMovieAsync(source, "Inception", 2010, "tmdb", "27205", "a-big-file");

        var result = await _coordinator.RequestAsync(movie.Id, target.Id, CancellationToken.None);

        Assert.Equal(LibraryMoveRequestStatus.InsufficientSpace, result.Status);
    }

    [Fact]
    public async Task Request_starts_a_job_and_reserves_the_item()
    {
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Movie, "Movies HD");
        var target = await AddCatalogAsync(_targetRoot, CatalogType.Movie, "Movies 4K");
        var movie = await AddMovieAsync(source, "Inception", 2010, "tmdb", "27205", "bytes");

        var result = await _coordinator.RequestAsync(movie.Id, target.Id, CancellationToken.None);

        Assert.Equal(LibraryMoveRequestStatus.Started, result.Status);
        Assert.NotNull(result.JobId);
        Assert.False(_queue.TryReserve(movie.Id)); // already reserved for the in-flight move

        var second = await _coordinator.RequestAsync(movie.Id, target.Id, CancellationToken.None);
        Assert.Equal(LibraryMoveRequestStatus.AlreadyMoving, second.Status);
    }

    [Fact]
    public async Task Request_rejects_a_move_while_a_conversion_is_active()
    {
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Movie, "Movies HD");
        var target = await AddCatalogAsync(_targetRoot, CatalogType.Movie, "Movies 4K");
        var movie = await AddMovieAsync(source, "Inception", 2010, "tmdb", "27205", "bytes");
        var mediaSource = await _database.MediaSources.SingleAsync(candidate => candidate.MediaItemId == movie.Id);

        _database.TranscodeJobs.Add(new TranscodeJob
        {
            Id = Guid.NewGuid(),
            EngineJobId = "engine-1",
            MediaSourceId = mediaSource.Id,
            MediaItemId = movie.Id,
            CatalogId = source.Id,
            InputPath = mediaSource.Path,
            OutputPath = "Inception (2010)/Inception (2010) - hevc.mkv",
            VideoCodec = "hevc",
            HardwareAcceleration = "auto",
            State = TranscodeJobState.Running,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _database.SaveChangesAsync();

        var result = await _coordinator.RequestAsync(movie.Id, target.Id, CancellationToken.None);

        Assert.Equal(LibraryMoveRequestStatus.TranscodeActive, result.Status);
        Assert.Null(result.JobId);
    }

    [Fact]
    public async Task Request_rejects_a_series_move_while_an_episode_conversion_is_active()
    {
        // Transcodes are movies-only today, but the check must cover the subtree: an episode's conversion
        // reads files the series move would relocate.
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Series, "Shows");
        var target = await AddCatalogAsync(_targetRoot, CatalogType.Series, "Shows Archive");
        var series = await AddSeriesAsync(source, "Severance", "tmdb", "95396", [(1, 1)]);
        var episode = await _database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Episode && item.SeriesId == series.Id);
        var mediaSource = await _database.MediaSources.SingleAsync(candidate => candidate.MediaItemId == episode.Id);

        _database.TranscodeJobs.Add(new TranscodeJob
        {
            Id = Guid.NewGuid(),
            EngineJobId = "engine-2",
            MediaSourceId = mediaSource.Id,
            MediaItemId = episode.Id,
            CatalogId = source.Id,
            InputPath = mediaSource.Path,
            OutputPath = "Severance (2020)/Season 01/Severance S01E01 - hevc.mkv",
            VideoCodec = "hevc",
            HardwareAcceleration = "auto",
            State = TranscodeJobState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await _database.SaveChangesAsync();

        var result = await _coordinator.RequestAsync(series.Id, target.Id, CancellationToken.None);

        Assert.Equal(LibraryMoveRequestStatus.TranscodeActive, result.Status);
    }

    [Fact]
    public async Task Guard_locks_the_item_and_its_sources_while_a_move_is_in_flight()
    {
        var source = await AddCatalogAsync(_sourceRoot, CatalogType.Series, "Shows");
        var series = await AddSeriesAsync(source, "Severance", "tmdb", "95396", [(1, 1)]);
        var episode = await _database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Episode && item.SeriesId == series.Id);
        var mediaSource = await _database.MediaSources.SingleAsync(candidate => candidate.MediaItemId == episode.Id);
        var guard = new LibraryMoveGuard(_database, _queue);

        Assert.False(await guard.IsItemMovingAsync(series.Id, CancellationToken.None));

        _queue.TryReserve(series.Id); // What RequestAsync does when it admits the move.

        // The reservation is keyed on the series; operations on the episode or its source resolve up to it.
        Assert.True(await guard.IsItemMovingAsync(series.Id, CancellationToken.None));
        Assert.True(await guard.IsItemMovingAsync(episode.Id, CancellationToken.None));
        Assert.True(await guard.IsSourceMovingAsync(mediaSource.Id, CancellationToken.None));

        _queue.Release(series.Id);
        Assert.False(await guard.IsItemMovingAsync(episode.Id, CancellationToken.None));
        Assert.False(await guard.IsSourceMovingAsync(mediaSource.Id, CancellationToken.None));
    }

    // ---- Seeding ---------------------------------------------------------------------------------------

    private async Task<MoveResult> MoveAsync(Guid itemId, Guid targetCatalogId)
    {
        var job = await _jobs.StartAsync(LibraryMoveService.JobType, "MediaItem", itemId, CancellationToken.None);
        return await _service.MoveAsync(itemId, targetCatalogId, job, CancellationToken.None);
    }

    private async Task<Catalog> AddCatalogAsync(string root, CatalogType type, string name)
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = name,
            Type = type,
            Root = root,
            NamingTemplate = "{Title} ({Year})",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _database.Catalogs.Add(catalog);
        await _database.SaveChangesAsync();
        return catalog;
    }

    private async Task<MediaItem> AddMovieAsync(Catalog catalog, string title, int year, string provider, string providerId, string content)
    {
        var now = DateTimeOffset.UtcNow;
        var relative = $"{title} ({year})/{title} ({year}).mkv";
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            Kind = MediaKind.Movie,
            Title = title,
            Year = year,
            IdentityProvider = provider,
            IdentityProviderId = providerId,
            Providers = new Dictionary<string, string> { [provider] = providerId },
            PublicId = PublicIdFactory.ForMovie(catalog.Id, provider, providerId),
            LibraryPath = relative,
            AddedAt = now,
            UpdatedAt = now,
        };
        _database.MediaItems.Add(movie);
        _database.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(), MediaItemId = movie.Id, Provider = provider, Language = "en-US", Title = title, FetchedAt = now,
        });
        await AttachSourceAsync(catalog, movie.Id, relative, content, now);
        await _database.SaveChangesAsync();
        WriteFile(catalog.Root, relative, content);
        return movie;
    }

    private async Task<MediaItem> AddSeriesAsync(Catalog catalog, string title, string provider, string seriesId, (int Season, int Episode)[] episodes)
    {
        var now = DateTimeOffset.UtcNow;
        var series = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            Kind = MediaKind.Series,
            Title = title,
            Year = 2020,
            IdentityProvider = provider,
            IdentityProviderId = seriesId,
            Providers = new Dictionary<string, string> { [provider] = seriesId },
            PublicId = PublicIdFactory.ForSeries(catalog.Id, provider, seriesId),
            AddedAt = now,
            UpdatedAt = now,
        };
        _database.MediaItems.Add(series);

        var seasons = new Dictionary<int, MediaItem>();
        foreach (var (season, episode) in episodes)
        {
            if (!seasons.TryGetValue(season, out var seasonItem))
            {
                seasonItem = new MediaItem
                {
                    Id = Guid.NewGuid(),
                    CatalogId = catalog.Id,
                    Kind = MediaKind.Season,
                    Title = $"Season {season}",
                    ParentId = series.Id,
                    SeriesId = series.Id,
                    IndexNumber = season,
                    ParentIndexNumber = season,
                    IdentityProvider = provider,
                    IdentityProviderId = seriesId,
                    IdentitySeasonNumber = season,
                    PublicId = PublicIdFactory.ForSeason(catalog.Id, provider, seriesId, season),
                    AddedAt = now,
                    UpdatedAt = now,
                };
                _database.MediaItems.Add(seasonItem);
                seasons[season] = seasonItem;
            }

            var relative = $"{title} (2020)/Season {season:D2}/{title} S{season:D2}E{episode:D2}.mkv";
            var episodeItem = new MediaItem
            {
                Id = Guid.NewGuid(),
                CatalogId = catalog.Id,
                Kind = MediaKind.Episode,
                Title = $"Episode {episode}",
                ParentId = seasonItem.Id,
                SeriesId = series.Id,
                SeasonId = seasonItem.Id,
                IndexNumber = episode,
                ParentIndexNumber = season,
                IdentityProvider = provider,
                IdentityProviderId = seriesId,
                IdentitySeasonNumber = season,
                IdentityEpisodeNumber = episode,
                LibraryPath = relative,
                PublicId = PublicIdFactory.ForEpisode(catalog.Id, provider, seriesId, season, episode),
                AddedAt = now,
                UpdatedAt = now,
            };
            _database.MediaItems.Add(episodeItem);
            await AttachSourceAsync(catalog, episodeItem.Id, relative, $"{title}-{season}-{episode}", now);
            WriteFile(catalog.Root, relative, $"{title}-{season}-{episode}");
        }

        await _database.SaveChangesAsync();
        return series;
    }

    private async Task AttachSourceAsync(Catalog catalog, Guid mediaItemId, string relative, string content, DateTimeOffset now)
    {
        var ingest = new IngestItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            MediaItemId = mediaItemId,
            Stage = IngestStage.Publish,
            Status = IngestStatus.Done,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var sourceFile = new SourceFile
        {
            Id = Guid.NewGuid(),
            IngestItemId = ingest.Id,
            RelativePath = relative,
            MediaItemId = mediaItemId,
            AssignmentStatus = SourceFileAssignmentStatus.Confirmed,
            SizeBytes = content.Length,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _database.IngestItems.Add(ingest);
        _database.SourceFiles.Add(sourceFile);
        _database.MediaSources.Add(new MediaSource
        {
            Id = Guid.NewGuid(),
            MediaItemId = mediaItemId,
            SourceFileId = sourceFile.Id,
            Container = "matroska",
            Path = relative,
            SizeBytes = content.Length,
            CreatedAt = now,
        });
        await Task.CompletedTask;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var absolute = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, content);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        foreach (var root in new[] { _sourceRoot, _targetRoot })
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>
    /// Reports both roots on one volume by default; <see cref="CrossVolume"/> makes the target report a
    /// different volume so the service takes the copy-then-delete path and the coordinator checks free space.
    /// </summary>
    private sealed class FakeFilesystem(string targetRoot) : IFilesystemInspector
    {
        public bool CrossVolume { get; set; }
        public long FreeBytes { get; set; } = long.MaxValue;

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public long GetAvailableFreeBytes(string path) => FreeBytes;

        public string GetVolumeKey(string path)
        {
            if (!CrossVolume)
            {
                return "vol-shared";
            }

            var full = Path.GetFullPath(path);
            return full.StartsWith(Path.GetFullPath(targetRoot), StringComparison.Ordinal) ? "vol-target" : "vol-source";
        }
    }

    /// <summary>Records the progress values a job broadcasts so a test can assert on the sequence of ticks.</summary>
    private sealed class RecordingJobNotifier : IRealtimeNotifier
    {
        public List<int> JobProgressValues { get; } = [];

        public Task JobChangedAsync(string eventName, JobEvent job, CancellationToken cancellationToken = default)
        {
            if (eventName == RealtimeEvents.JobProgress)
            {
                JobProgressValues.Add(job.Progress);
            }

            return Task.CompletedTask;
        }

        public Task DownloadProgressAsync(DownloadProgress progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DownloadStateChangedAsync(DownloadStateChanged change, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task IngestStageChangedAsync(IngestStageChanged change, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task VpnStatusChangedAsync(VpnStatusChanged status, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
