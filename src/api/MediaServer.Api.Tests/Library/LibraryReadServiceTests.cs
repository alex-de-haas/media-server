using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// Coverage for the UI-facing read layer (<see cref="LibraryReadService"/>): listing with localized
/// titles + user data, movie detail with media streams, series detail with season rollups, and episode
/// listings. Reuses the shared in-memory SQLite fixture.
/// </summary>
public sealed class LibraryReadServiceTests : IDisposable
{
    private int _userId;

    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly LibraryReadService _library;

    private Guid _movieCatalogId;
    private Guid _movieId;
    private Guid _seriesId;
    private Guid _seasonId;
    private Guid _episodeId;
    private Guid _episode2Id;

    public LibraryReadServiceTests()
    {
        Seed();
        _context = _db.Create();
        var settings = new MediaServerSettings { SupportedLanguages = ["en-US"] };
        _library = new LibraryReadService(_context, new UserDataService(_context, TimeProvider.System), settings);
    }

    [Fact]
    public async Task List_returns_published_top_level_items_with_localized_titles_and_user_data()
    {
        SeedUserData(_movieId, played: true);

        var items = await _library.ListAsync(catalogId: null, kind: null, appUserId: _userId, CancellationToken.None);

        Assert.Equal(2, items.Count);
        var movie = Assert.Single(items, item => item.Kind == "Movie");
        Assert.Equal("Inception", movie.Title); // localized metadata title, not the raw item title
        Assert.Equal(2010, movie.Year);
        Assert.Equal("https://image.tmdb.org/p.jpg", movie.PosterUrl);
        Assert.True(movie.UserData?.Played);
        Assert.Contains(items, item => item.Kind == "Series");
    }

    [Fact]
    public async Task List_filters_by_kind_and_catalog()
    {
        var byKind = await _library.ListAsync(catalogId: null, kind: MediaKind.Movie, appUserId: null, CancellationToken.None);
        Assert.Equal("Movie", Assert.Single(byKind).Kind);

        var byCatalog = await _library.ListAsync(catalogId: _movieCatalogId, kind: null, appUserId: null, CancellationToken.None);
        Assert.Equal(_movieId, Assert.Single(byCatalog).Id);
    }

    [Fact]
    public async Task Detail_for_movie_includes_metadata_images_sources_and_streams()
    {
        var detail = await _library.GetDetailAsync(_movieId, appUserId: null, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("Movie", detail!.Kind);
        Assert.Equal("Inception", detail.Title);
        Assert.Equal("A thief who steals corporate secrets.", detail.Overview);
        Assert.Contains("Science Fiction", detail.Genres);
        Assert.Equal(TimeSpan.FromMinutes(148).Ticks, detail.RuntimeTicks);
        Assert.Equal("https://image.tmdb.org/p.jpg", detail.PosterUrl);
        Assert.Equal("https://image.tmdb.org/b.jpg", detail.BackdropUrl);
        Assert.Null(detail.Seasons);

        var source = Assert.Single(detail.MediaSources);
        Assert.Equal("matroska", source.Container);

        var video = Assert.Single(source.Streams, stream => stream.Type == "Video");
        Assert.Equal("1080p H264", video.DisplayTitle);

        var audio = Assert.Single(source.Streams, stream => stream.Type == "Audio");
        Assert.Equal("eng AC3 5.1", audio.DisplayTitle);

        Assert.Contains(source.Streams, stream => stream.Type == "Subtitle");
    }

    [Fact]
    public async Task Detail_for_series_includes_seasons_with_counts_and_watched_rollup()
    {
        SeedUserData(_episodeId, played: true);
        SeedUserData(_episode2Id, played: true);

        var detail = await _library.GetDetailAsync(_seriesId, appUserId: _userId, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("Series", detail!.Kind);
        Assert.Empty(detail.MediaSources);

        var season = Assert.Single(detail.Seasons!);
        Assert.Equal(1, season.SeasonNumber);
        Assert.Equal(2, season.EpisodeCount);
        Assert.True(season.UserData?.Played);          // both episodes played
        Assert.True(detail.UserData?.Played);          // series rollup: all children played
        Assert.Equal(0, detail.UserData?.UnplayedItemCount);
    }

    [Fact]
    public async Task Episodes_lists_ordered_episodes_with_resume_state()
    {
        SeedUserData(_episodeId, position: TimeSpan.FromMinutes(5).Ticks);

        var episodes = await _library.GetEpisodesAsync(_seriesId, seasonId: null, appUserId: _userId, CancellationToken.None);

        Assert.Equal(2, episodes.Count);
        var first = episodes[0];
        Assert.Equal(_episodeId, first.Id);            // ordered by season then episode number
        Assert.Equal("Pilot", first.Title);
        Assert.Equal(1, first.SeasonNumber);
        Assert.Equal(1, first.EpisodeNumber);
        Assert.Equal(TimeSpan.FromMinutes(5).Ticks, first.UserData?.PlaybackPositionTicks);
        Assert.Equal(2, episodes[1].EpisodeNumber);

        // Scoping to the season returns the same episodes.
        var bySeason = await _library.GetEpisodesAsync(_seriesId, seasonId: _seasonId, appUserId: _userId, CancellationToken.None);
        Assert.Equal(2, bySeason.Count);
    }

    [Fact]
    public async Task Detail_and_episodes_expose_the_tmdb_id_for_infuse_deep_links()
    {
        var movie = await _library.GetDetailAsync(_movieId, appUserId: null, CancellationToken.None);
        Assert.Equal("27205", movie!.TmdbId);

        var series = await _library.GetDetailAsync(_seriesId, appUserId: null, CancellationToken.None);
        Assert.Equal("1396", series!.TmdbId);

        var episodes = await _library.GetEpisodesAsync(_seriesId, seasonId: null, appUserId: null, CancellationToken.None);
        Assert.Equal("1396", episodes[0].SeriesTmdbId); // an episode carries its series identity
    }

    [Fact]
    public async Task Recent_returns_published_top_level_items()
    {
        var recent = await _library.GetRecentAsync(limit: 10, appUserId: null, CancellationToken.None);

        Assert.Equal(2, recent.Count);
        Assert.Contains(recent, item => item.Id == _movieId);
        Assert.Contains(recent, item => item.Id == _seriesId);
    }

    [Fact]
    public async Task Resume_returns_in_progress_leaves_with_navigation_targets()
    {
        SeedUserData(_movieId, position: TimeSpan.FromMinutes(20).Ticks);
        SeedUserData(_episodeId, position: TimeSpan.FromMinutes(5).Ticks);

        var resume = await _library.GetResumeAsync(_userId, limit: 10, CancellationToken.None);

        Assert.Equal(2, resume.Count);
        var movie = Assert.Single(resume, item => item.Kind == "Movie");
        Assert.Equal(_movieId, movie.NavId);
        Assert.Equal("Movie", movie.NavKind);

        var episode = Assert.Single(resume, item => item.Kind == "Episode");
        Assert.Equal(_seriesId, episode.NavId);        // episodes navigate to their series
        Assert.Equal("Series", episode.NavKind);
        Assert.Equal("Breaking Bad", episode.Title);   // the rail title is the series name
        Assert.StartsWith("S01E01", episode.Subtitle!);
    }

    [Fact]
    public async Task NextUp_returns_the_next_unwatched_episode_of_a_started_series()
    {
        SeedUserData(_episodeId, played: true); // watched S01E01

        var nextUp = await _library.GetNextUpAsync(_userId, limit: 10, CancellationToken.None);

        var next = Assert.Single(nextUp);
        Assert.Equal(_episode2Id, next.Id);
        Assert.Equal(_seriesId, next.NavId);
        Assert.StartsWith("S01E02", next.Subtitle!);
    }

    [Fact]
    public async Task Mark_played_and_favorite_by_id_persist_and_roundtrip()
    {
        var userData = new UserDataService(_context, TimeProvider.System);

        var played = await userData.SetPlayedAsync(_userId, _movieId, played: true, playedAt: null, CancellationToken.None);
        Assert.True(played?.Played);

        var favorited = await userData.SetFavoriteAsync(_userId, _movieId, favorite: true, CancellationToken.None);
        Assert.True(favorited?.IsFavorite);

        var detail = await _library.GetDetailAsync(_movieId, _userId, CancellationToken.None);
        Assert.True(detail?.UserData?.Played);
        Assert.True(detail?.UserData?.IsFavorite);
    }

    [Fact]
    public async Task Detail_returns_null_for_unknown_or_unpublished_item()
    {
        Assert.Null(await _library.GetDetailAsync(Guid.NewGuid(), appUserId: null, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_movie_removes_its_db_rows()
    {
        SeedUserData(_movieId, played: true);
        var service = new LibraryDeleteService(_context, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));

        var deleted = await service.DeleteAsync(_movieId, deleteFiles: false, CancellationToken.None);

        Assert.True(deleted);
        await using var verify = _db.Create();
        Assert.False(await verify.MediaItems.AnyAsync(item => item.Id == _movieId));
        Assert.False(await verify.MediaSources.AnyAsync(source => source.MediaItemId == _movieId));
        Assert.False(await verify.UserItemData.AnyAsync(data => data.MediaItemId == _movieId));
    }

    [Fact]
    public async Task Delete_series_cascades_to_its_seasons_and_episodes()
    {
        var service = new LibraryDeleteService(_context, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));

        var deleted = await service.DeleteAsync(_seriesId, deleteFiles: false, CancellationToken.None);

        Assert.True(deleted);
        await using var verify = _db.Create();
        Assert.False(await verify.MediaItems.AnyAsync(item =>
            item.Id == _seriesId || item.Id == _seasonId || item.Id == _episodeId || item.Id == _episode2Id));
    }

    [Fact]
    public async Task Delete_with_files_removes_the_library_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "ms-del-" + Guid.NewGuid().ToString("N"));
        CatalogPaths.For(root).EnsureCreated();
        var relativePath = "library/Solaris (1972)/Solaris.mkv";
        var absolutePath = Path.Combine(root, "library", "Solaris (1972)", "Solaris.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        await File.WriteAllTextAsync(absolutePath, "x");

        Guid movieId;
        await using (var seed = _db.Create())
        {
            var catalog = new Catalog
            {
                Id = Guid.NewGuid(), Name = "Films", Type = CatalogType.Movie, Root = root,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            var movie = new MediaItem
            {
                Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id,
                Kind = MediaKind.Movie, Title = "Solaris", Year = 1972,
                AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            var source = new MediaSource
            {
                Id = Guid.NewGuid(), MediaItemId = movie.Id, Container = "matroska", Path = relativePath,
                SizeBytes = 1, DurationTicks = 1, CreatedAt = DateTimeOffset.UtcNow,
            };
            seed.Catalogs.Add(catalog);
            seed.MediaItems.Add(movie);
            seed.MediaSources.Add(source);
            await seed.SaveChangesAsync();
            movieId = movie.Id;
        }

        try
        {
            var service = new LibraryDeleteService(_context, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));

            var deleted = await service.DeleteAsync(movieId, deleteFiles: true, CancellationToken.None);

            Assert.True(deleted);
            Assert.False(File.Exists(absolutePath));
            await using var verify = _db.Create();
            Assert.False(await verify.MediaItems.AnyAsync(item => item.Id == movieId));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private void SeedUserData(Guid mediaItemId, bool played = false, long position = 0, bool favorite = false)
    {
        using var context = _db.Create();
        context.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(),
            AppUserId = _userId,
            MediaItemId = mediaItemId,
            Played = played,
            PlaybackPositionTicks = position,
            IsFavorite = favorite,
            PlayCount = played ? 1 : 0,
            LastPlayedDate = DateTimeOffset.UtcNow,
        });
        context.SaveChanges();
    }

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();

        var user = new AppUser { HostUserId = "host-1", Email = "user@example.com", Role = AppUserRole.User, CreatedAt = now, LastSeenAt = now };
        context.AppUsers.Add(user);

        var movieCatalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/movies", CreatedAt = now, UpdatedAt = now };
        var seriesCatalog = new Catalog { Id = Guid.NewGuid(), Name = "Shows", Type = CatalogType.Series, Root = "/shows", CreatedAt = now, UpdatedAt = now };
        _movieCatalogId = movieCatalog.Id;
        context.Catalogs.AddRange(movieCatalog, seriesCatalog);

        // ---- Movie ----
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = movieCatalog.Id,
            Kind = MediaKind.Movie,
            Title = "Untitled.2010.1080p.BluRay", // raw title; the localized metadata title is "Inception"
            Year = 2010,
            IdentityProvider = "tmdb",
            IdentityProviderId = "27205",
            AddedAt = now,
            UpdatedAt = now,
        };
        _movieId = movie.Id;
        context.MediaItems.Add(movie);
        context.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(),
            MediaItemId = movie.Id,
            Provider = "tmdb",
            Language = "en-US",
            Title = "Inception",
            Overview = "A thief who steals corporate secrets.",
            Genres = ["Science Fiction", "Action"],
            ReleaseDate = new DateTimeOffset(2010, 7, 16, 0, 0, 0, TimeSpan.Zero),
            RuntimeTicks = TimeSpan.FromMinutes(148).Ticks,
            FetchedAt = now,
        });
        context.ImageAssets.AddRange(
            new ImageAsset { Id = Guid.NewGuid(), MediaItemId = movie.Id, ImageType = ImageType.Primary, Provider = "tmdb", RemotePath = "https://image.tmdb.org/p.jpg", Tag = "primary1", SortOrder = 0 },
            new ImageAsset { Id = Guid.NewGuid(), MediaItemId = movie.Id, ImageType = ImageType.Backdrop, Provider = "tmdb", RemotePath = "https://image.tmdb.org/b.jpg", Tag = "backdrop1", SortOrder = 0 });

        var source = new MediaSource
        {
            Id = Guid.NewGuid(),
            MediaItemId = movie.Id,
            Container = "matroska",
            Path = "library/Inception (2010)/Inception (2010).mkv",
            SizeBytes = 8_000_000_000,
            Bitrate = 12_000_000,
            DurationTicks = TimeSpan.FromMinutes(148).Ticks,
            CreatedAt = now,
        };
        context.MediaSources.Add(source);
        context.MediaStreams.AddRange(
            new MediaStream { Id = Guid.NewGuid(), MediaSourceId = source.Id, StreamType = StreamType.Video, Index = 0, Codec = "h264", Width = 1920, Height = 1080, FrameRate = 23.976, BitDepth = 8 },
            new MediaStream { Id = Guid.NewGuid(), MediaSourceId = source.Id, StreamType = StreamType.Audio, Index = 1, Codec = "ac3", Channels = 6, Language = "eng", IsDefault = true },
            new MediaStream { Id = Guid.NewGuid(), MediaSourceId = source.Id, StreamType = StreamType.Subtitle, Index = 2, Codec = "subrip", Language = "eng" });

        // ---- Series → Season → Episode ----
        var series = new MediaItem { Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = seriesCatalog.Id, Kind = MediaKind.Series, Title = "Breaking Bad", Year = 2008, IdentityProvider = "tmdb", IdentityProviderId = "1396", AddedAt = now, UpdatedAt = now };
        var season = new MediaItem { Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = seriesCatalog.Id, Kind = MediaKind.Season, Title = "Season 1", ParentId = series.Id, SeriesId = series.Id, IndexNumber = 1, AddedAt = now, UpdatedAt = now };
        var episode = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = seriesCatalog.Id,
            Kind = MediaKind.Episode,
            Title = "Pilot",
            ParentId = season.Id,
            SeriesId = series.Id,
            SeasonId = season.Id,
            ParentIndexNumber = 1,
            IndexNumber = 1,
            IdentityProvider = "tmdb",
            IdentityProviderId = "1396",
            IdentitySeasonNumber = 1,
            IdentityEpisodeNumber = 1,
            LibraryPath = "library/Breaking Bad/Season 1/S01E01.mkv",
            AddedAt = now,
            UpdatedAt = now,
        };
        var episode2 = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = seriesCatalog.Id,
            Kind = MediaKind.Episode,
            Title = "Cat's in the Bag...",
            ParentId = season.Id,
            SeriesId = series.Id,
            SeasonId = season.Id,
            ParentIndexNumber = 1,
            IndexNumber = 2,
            LibraryPath = "library/Breaking Bad/Season 1/S01E02.mkv",
            AddedAt = now,
            UpdatedAt = now,
        };
        _seriesId = series.Id;
        _seasonId = season.Id;
        _episodeId = episode.Id;
        _episode2Id = episode2.Id;
        context.MediaItems.AddRange(series, season, episode, episode2);

        context.SaveChanges();
        _userId = user.Id;
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }
}
