using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using MediaServer.Api.Library;
using MediaServer.Api.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// Coverage for syncing a catalog with its disk. The half that imports new files is
/// <see cref="LibraryImportServiceTests"/>'s; what is exercised here is the half that removes what the
/// disk no longer backs — and, before any of it, the mount rule that decides whether the disk is even
/// answering. Every test uses a real temp root, because the whole question is what the filesystem says.
/// </summary>
public sealed class CatalogScanServiceTests : IDisposable
{
    private readonly CatalogScanQueue _scanQueue = new();
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-scan-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingCore _core = new();

    private readonly int _userId;

    public CatalogScanServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        CatalogPaths.For(_root).EnsureCreated();

        var user = new AppUser
        {
            HostUserId = "user-1",
            DisplayName = "Viewer",
            Role = AppUserRole.Admin,
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
        };
        _database.AppUsers.Add(user);
        _database.SaveChanges();
        _userId = user.Id;
    }

    /// <summary>
    /// A second context on the same connection. The removal path writes through <c>ExecuteUpdate</c>,
    /// which bypasses the change tracker, so the seeding context would keep answering with the rows as
    /// they were before the scan touched them.
    /// </summary>
    private MediaServerDbContext Verify() =>
        new(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);

    private CatalogScanService Service()
    {
        var sandbox = new CatalogPathSandbox();
        var probe = new CatalogFileProbe(_database, sandbox);
        return new CatalogScanService(
            _database,
            probe,
            new FilesystemInspector(),
            new CatalogHealthService(_database, new FilesystemInspector(), probe, new MediaServerSettings(), _core,
                NullLogger<CatalogHealthService>.Instance),
            new LibraryImportService(_database, new PipelineQueue(), NullLogger<LibraryImportService>.Instance),
            new LibraryDeleteService(_database, new LibraryFileEraser(sandbox, NullLogger<LibraryFileEraser>.Instance)),
            _core,
            _scanQueue,
            NullLogger<CatalogScanService>.Instance);
    }

    [Fact]
    public async Task A_catalog_whose_every_file_is_unreadable_is_offline_not_emptied()
    {
        // The failure this rule exists for: the root is there (an unmounted bind presents as an empty
        // directory) and every library file under it is gone at once. That is a volume, not a library.
        var catalog = SeedCatalog();
        SeedMovie(catalog, "Inception (2010)/Inception.mkv", favorite: true);
        SeedMovie(catalog, "Heat (1995)/Heat.mkv");

        var report = (await Service().ScanAsync(catalog.Id, CancellationToken.None)).Report;

        Assert.NotNull(report);
        Assert.True(report!.Offline);
        Assert.Equal(0, report.TitlesGhosted);
        Assert.Equal(0, report.TitlesPurged);
        await using var verify = Verify();
        Assert.Equal(2, await verify.MediaItems.CountAsync(item => item.PublicId != null));
        Assert.NotNull((await verify.Catalogs.SingleAsync(candidate => candidate.Id == catalog.Id)).OfflineSince);
        Assert.Equal(1, _core.CountFor($"media-server:catalog-offline:{catalog.Id}"));
    }

    [Fact]
    public async Task A_finished_scan_records_when_it_ran()
    {
        // Stamped by the scan rather than by whatever started it, because the nightly job and the
        // synchronous route open no job row: reading scan state from jobs reported a catalog scanned
        // nightly for months as never scanned, and an empty search result then says "nothing has looked
        // at this" about a library that is fully read.
        var catalog = SeedCatalog();
        SeedMovie(catalog, "Heat (1995)/Heat.mkv");
        WriteFile("Heat (1995)/Heat.mkv");

        await Service().ScanAsync(catalog.Id, CancellationToken.None);

        await using var verify = Verify();
        Assert.NotNull((await verify.Catalogs.SingleAsync(entry => entry.Id == catalog.Id)).LastScannedAt);
    }

    [Fact]
    public async Task An_offline_catalog_is_not_recorded_as_scanned()
    {
        // Paired with the case above, and the reason the stamp is not unconditional: a volume that could
        // not be read was not scanned, and recording otherwise turns "the disk is missing" into "the
        // library is empty" — which is exactly the sentence this state exists to prevent.
        var catalog = SeedCatalog();
        SeedMovie(catalog, "Inception (2010)/Inception.mkv");

        var report = (await Service().ScanAsync(catalog.Id, CancellationToken.None)).Report;
        Assert.True(report!.Offline);

        await using var verify = Verify();
        Assert.Null((await verify.Catalogs.SingleAsync(entry => entry.Id == catalog.Id)).LastScannedAt);
    }

    [Fact]
    public async Task A_scan_refuses_to_start_while_another_holds_the_catalog()
    {
        // The reservation lives here rather than in the queue that admits MCP requests, because this is
        // the one point every entry point passes through. Held one level up it protected only the path
        // that went through it, so the synchronous route and the nightly job could still walk the same
        // disk at the same time — which is what the guard was advertised to prevent.
        var catalog = SeedCatalog();
        WriteFile("Heat (1995)/Heat.mkv");
        SeedMovie(catalog, "Heat (1995)/Heat.mkv");

        _scanQueue.TryReserve(catalog.Id);
        var refused = await Service().ScanAsync(catalog.Id, CancellationToken.None);
        Assert.True(refused.AlreadyRunning);
        Assert.Null(refused.Report);

        // Beside the same call once the hold is released, or a service that refused everything would
        // pass the assertion above.
        _scanQueue.Release(catalog.Id);
        Assert.NotNull((await Service().ScanAsync(catalog.Id, CancellationToken.None)).Report);
    }

    [Fact]
    public async Task One_surviving_file_proves_the_volume_and_the_rest_really_are_deletions()
    {
        var catalog = SeedCatalog();
        var kept = SeedMovie(catalog, "Heat (1995)/Heat.mkv");
        WriteFile("Heat (1995)/Heat.mkv");
        var watched = SeedMovie(catalog, "Inception (2010)/Inception.mkv", favorite: true);
        var untouched = SeedMovie(catalog, "Tenet (2020)/Tenet.mkv");

        var report = (await Service().ScanAsync(catalog.Id, CancellationToken.None)).Report;

        Assert.NotNull(report);
        Assert.False(report!.Offline);
        Assert.Equal(3, report.SourcesChecked);
        Assert.Equal(2, report.MissingFiles);
        Assert.Equal(1, report.TitlesGhosted);
        Assert.Equal(1, report.TitlesPurged);

        await using var verify = Verify();
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == kept)).PublicId);
        var ghost = await verify.MediaItems.SingleAsync(item => item.Id == watched);
        Assert.Null(ghost.PublicId);
        Assert.NotNull(ghost.RemovedAt);
        Assert.False(await verify.MediaItems.AnyAsync(item => item.Id == untouched));
        Assert.Equal(1, _core.CountFor($"media-server:catalog-scan:{catalog.Id}"));
    }

    [Fact]
    public async Task A_rating_alone_keeps_a_vanished_title_as_a_ghost()
    {
        var catalog = SeedCatalog();
        SeedMovie(catalog, "Heat (1995)/Heat.mkv");
        WriteFile("Heat (1995)/Heat.mkv");
        var rated = SeedMovie(catalog, "Inception (2010)/Inception.mkv", rating: 4);

        var report = (await Service().ScanAsync(catalog.Id, CancellationToken.None)).Report;

        Assert.Equal(1, report!.TitlesGhosted);
        await using var verify = Verify();
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == rated)).RemovedAt);
        Assert.Equal(4, (await verify.UserItemData.SingleAsync(data => data.MediaItemId == rated)).Rating);
    }

    [Fact]
    public async Task A_gone_version_drops_from_an_item_that_keeps_another()
    {
        // Two versions of one film, one of them deleted by hand. The film is not gone — only that copy —
        // and the pin it was holding must not survive it.
        var catalog = SeedCatalog();
        var movieId = SeedMovie(catalog, "Inception (2010)/Inception.mkv");
        WriteFile("Inception (2010)/Inception.mkv");
        var goneSourceId = AddSource(movieId, "Inception (2010)/Inception - 4K.mkv");
        await _database.MediaItems.Where(item => item.Id == movieId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.DefaultSourceId, goneSourceId));

        var report = (await Service().ScanAsync(catalog.Id, CancellationToken.None)).Report;

        Assert.Equal(1, report!.VersionsRemoved);
        Assert.Equal(0, report.TitlesGhosted);
        Assert.Equal(0, report.TitlesPurged);
        await using var verify = Verify();
        var movie = await verify.MediaItems.SingleAsync(item => item.Id == movieId);
        Assert.NotNull(movie.PublicId);
        Assert.Null(movie.DefaultSourceId);
        Assert.Single(await verify.MediaSources.Where(source => source.MediaItemId == movieId).ToListAsync());
    }

    [Fact]
    public async Task A_gone_sidecar_drops_its_track_from_a_file_that_is_still_there()
    {
        var catalog = SeedCatalog();
        var movieId = SeedMovie(catalog, "Inception (2010)/Inception.mkv");
        WriteFile("Inception (2010)/Inception.mkv");
        var sourceId = await _database.MediaSources.Where(source => source.MediaItemId == movieId)
            .Select(source => source.Id).SingleAsync();
        _database.MediaStreams.Add(new MediaStream
        {
            Id = Guid.NewGuid(), MediaSourceId = sourceId, StreamType = StreamType.Audio, Index = 0,
            IsExternal = true, ExternalPath = "Inception (2010)/Inception.ru.mka", Codec = "ac3",
        });
        await _database.SaveChangesAsync();

        var report = (await Service().ScanAsync(catalog.Id, CancellationToken.None)).Report;

        Assert.Equal(1, report!.SidecarsRemoved);
        await using var verify = Verify();
        Assert.False(await verify.MediaStreams.AnyAsync(stream => stream.IsExternal));
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == movieId)).PublicId);
    }

    [Fact]
    public async Task A_vanished_episode_ghosts_its_series_when_it_was_the_last_one()
    {
        var catalog = SeedCatalog(CatalogType.Series);
        var (seriesId, _, episodeId) = SeedEpisode(catalog, "Show/S01E01.mkv", watched: true);

        var report = (await Service().ScanAsync(catalog.Id, CancellationToken.None)).Report;

        // Nothing else resolves in this catalog, so the mount rule guards it: the pass must not act.
        Assert.True(report!.Offline);

        // With a second episode still on disk the volume is proven and the gone one is a real deletion.
        WriteFile("Show/S01E02.mkv");
        SeedEpisode(catalog, "Show/S01E02.mkv", watched: false, seriesId: seriesId);
        var second = (await Service().ScanAsync(catalog.Id, CancellationToken.None)).Report;

        Assert.False(second!.Offline);
        Assert.Equal(1, second.MissingFiles);
        await using var verify = Verify();
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == episodeId)).RemovedAt);
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == seriesId)).PublicId);
    }

    [Fact]
    public async Task An_empty_catalog_is_neither_offline_nor_touched()
    {
        var catalog = SeedCatalog();

        var report = (await Service().ScanAsync(catalog.Id, CancellationToken.None)).Report;

        Assert.False(report!.Offline);
        Assert.Equal(0, report.SourcesChecked);
        await using var verify = Verify();
        Assert.Null((await verify.Catalogs.SingleAsync(candidate => candidate.Id == catalog.Id)).OfflineSince);
    }

    [Fact]
    public async Task A_missing_root_is_offline_without_reading_anything()
    {
        var catalog = SeedCatalog(root: Path.Combine(Path.GetTempPath(), "ms-scan-absent-" + Guid.NewGuid().ToString("N")));
        SeedMovie(catalog, "Inception (2010)/Inception.mkv");

        var report = (await Service().ScanAsync(catalog.Id, CancellationToken.None)).Report;

        Assert.True(report!.Offline);
        await using var verify = Verify();
        Assert.NotNull((await verify.Catalogs.SingleAsync(candidate => candidate.Id == catalog.Id)).OfflineSince);
        Assert.Equal(1, await verify.MediaItems.CountAsync(item => item.PublicId != null));
    }

    [Fact]
    public async Task A_readable_file_brings_a_catalog_marked_offline_back()
    {
        var catalog = SeedCatalog();
        SeedMovie(catalog, "Heat (1995)/Heat.mkv");
        WriteFile("Heat (1995)/Heat.mkv");
        catalog.OfflineSince = DateTimeOffset.UtcNow.AddHours(-1);
        await _database.SaveChangesAsync();

        await Service().ScanAsync(catalog.Id, CancellationToken.None);

        await using var verify = Verify();
        Assert.Null((await verify.Catalogs.SingleAsync(candidate => candidate.Id == catalog.Id)).OfflineSince);
        Assert.Equal(1, _core.CountFor($"media-server:catalog-online:{catalog.Id}"));
    }

    [Fact]
    public async Task Scanning_every_catalog_reports_each_one()
    {
        var movies = SeedCatalog();
        SeedMovie(movies, "Heat (1995)/Heat.mkv");
        WriteFile("Heat (1995)/Heat.mkv");
        var absent = SeedCatalog(root: Path.Combine(Path.GetTempPath(), "ms-scan-absent-" + Guid.NewGuid().ToString("N")));

        var report = await Service().ScanAllAsync(CancellationToken.None);

        Assert.Equal(2, report.Catalogs.Count);
        Assert.Equal(1, report.CatalogsScanned);
        Assert.Equal(1, report.CatalogsOffline);
        Assert.True(report.Catalogs.Single(entry => entry.CatalogId == absent.Id).Offline);
    }

    private Catalog SeedCatalog(CatalogType type = CatalogType.Movie, string? root = null)
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = type == CatalogType.Movie ? "Movies" : "Series",
            Type = type,
            Root = root ?? _root,
            NamingTemplate = "{Title} ({Year})",
            CreatedAt = now,
            UpdatedAt = now,
        };
        _database.Catalogs.Add(catalog);
        _database.SaveChanges();
        return catalog;
    }

    private Guid SeedMovie(Catalog catalog, string relativePath, bool favorite = false, int? rating = null)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = catalog.Id,
            Kind = MediaKind.Movie,
            Title = relativePath.Split('/')[0],
            IdentityProvider = "tmdb",
            IdentityProviderId = Guid.NewGuid().ToString("N")[..6],
            LibraryPath = relativePath,
            AddedAt = now,
            UpdatedAt = now,
        };
        _database.MediaItems.Add(item);
        _database.SaveChanges();
        AddSource(item.Id, relativePath);

        if (favorite || rating is not null)
        {
            _database.UserItemData.Add(new UserItemData
            {
                Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = item.Id, IsFavorite = favorite, Rating = rating,
            });
            _database.SaveChanges();
        }

        return item.Id;
    }

    private (Guid SeriesId, Guid SeasonId, Guid EpisodeId) SeedEpisode(
        Catalog catalog, string relativePath, bool watched, Guid? seriesId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var series = seriesId is { } existing
            ? _database.MediaItems.Single(item => item.Id == existing)
            : new MediaItem
            {
                Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id,
                Kind = MediaKind.Series, Title = "Show", IdentityProvider = "tmdb", IdentityProviderId = "1399",
                AddedAt = now, UpdatedAt = now,
            };
        var season = seriesId is not null
            ? _database.MediaItems.Single(item => item.SeriesId == series.Id && item.Kind == MediaKind.Season)
            : new MediaItem
            {
                Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id,
                Kind = MediaKind.Season, Title = "Season 1", ParentId = series.Id, SeriesId = series.Id,
                IndexNumber = 1, AddedAt = now, UpdatedAt = now,
            };
        var episode = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id,
            Kind = MediaKind.Episode, Title = "Episode", ParentId = season.Id, SeasonId = season.Id,
            SeriesId = series.Id, LibraryPath = relativePath, AddedAt = now, UpdatedAt = now,
        };

        if (seriesId is null)
        {
            _database.MediaItems.AddRange(series, season);
        }

        _database.MediaItems.Add(episode);
        _database.SaveChanges();
        AddSource(episode.Id, relativePath);

        if (watched)
        {
            _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
            {
                Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = episode.Id, CreatedAt = now,
                WatchedAt = now, Origin = PlaybackHistoryOrigin.LocalPlayback,
            });
            _database.SaveChanges();
        }

        return (series.Id, season.Id, episode.Id);
    }

    private Guid AddSource(Guid mediaItemId, string relativePath)
    {
        var source = new MediaSource
        {
            Id = Guid.NewGuid(),
            MediaItemId = mediaItemId,
            Container = "matroska",
            Path = relativePath,
            SizeBytes = 1,
            DurationTicks = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _database.MediaSources.Add(source);
        _database.SaveChanges();
        return source.Id;
    }

    private void WriteFile(string relative, int bytes = 1024)
    {
        var absolute = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, new byte[bytes]);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingCore : IHostyCoreClient
    {
        public List<string?> Notifications { get; } = [];

        public bool IsEnabled => true;

        public int CountFor(string dedupeKey) => Notifications.Count(key => key == dedupeKey);

        public Task<bool> PublishNotificationAsync(
            CoreNotificationLevel level, string title, string? body, string? link, string? dedupeKey,
            string target = HostyCoreClient.BroadcastTarget, CancellationToken cancellationToken = default)
        {
            Notifications.Add(dedupeKey);
            return Task.FromResult(true);
        }

        public Task<CoreBackupResult?> CreateBackupAsync(string? note, CancellationToken cancellationToken) =>
            Task.FromResult<CoreBackupResult?>(null);

        public Task<IReadOnlyList<CoreDirectoryUser>?> ListDirectoryUsersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CoreDirectoryUser>?>([]);
    }
}
