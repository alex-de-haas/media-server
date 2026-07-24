using MediaServer.Api.Data;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.WatchHistory;

/// <summary>
/// The calendar read: what it returns, what it refuses to invent, and whose history it will not show.
/// </summary>
public sealed class WatchHistoryCalendarServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-24T12:00:00Z"));
    private readonly int _userId;
    private readonly int _otherUserId;
    private readonly Guid _catalogId = Guid.NewGuid();

    public WatchHistoryCalendarServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        var user = NewUser("host-1", "alex@example.com");
        var other = NewUser("host-2", "sam@example.com");
        _database.AppUsers.AddRange(user, other);
        _database.SaveChanges();
        _userId = user.Id;
        _otherUserId = other.Id;

        _database.Catalogs.Add(new Catalog
        {
            Id = _catalogId, Name = "Library", Type = CatalogType.Movie, Root = "/m",
            CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
    }

    [Fact]
    public async Task ItReturnsRawPlaysInTheRangeWithoutGroupingThem()
    {
        // Two plays of one movie on one day stay two rows: grouping is the browser's job, in its own
        // time zone, and collapsing here would lose a real rewatch.
        var movie = AddMovie("Arrival");
        AddPlay(movie.Id, "2026-07-10T20:00:00Z");
        AddPlay(movie.Id, "2026-07-10T22:30:00Z");

        var result = await LoadMonthAsync();

        Assert.Equal(2, result.Events.Count);
        Assert.All(result.Events, entry => Assert.Equal("Arrival", entry.Title));
        Assert.Equal(
            [DateTimeOffset.Parse("2026-07-10T20:00:00Z"), DateTimeOffset.Parse("2026-07-10T22:30:00Z")],
            result.Events.Select(entry => entry.WatchedAt));
    }

    [Fact]
    public async Task AnEpisodeCarriesItsSeriesTitlePosterAndNumbering()
    {
        // The grid groups episodes at series level, so each row must be able to render the series card
        // without a second lookup.
        var series = AddSeries("Severance");
        AddPoster(series.Id, "https://img/severance.jpg");
        var episode = AddEpisode(series, season: 2, number: 3, title: "Who Is Alive?");
        AddPlay(episode.Id, "2026-07-08T19:42:00Z");

        var result = await LoadMonthAsync();

        var entry = Assert.Single(result.Events);
        Assert.Equal("Episode", entry.Kind);
        Assert.Equal("Who Is Alive?", entry.Title);
        Assert.Equal("Severance", entry.SeriesTitle);
        Assert.Equal(series.Id, entry.SeriesId);
        Assert.Equal("https://img/severance.jpg", entry.PosterUrl);
        Assert.Equal(2, entry.SeasonNumber);
        Assert.Equal(3, entry.EpisodeNumber);
    }

    [Fact]
    public async Task AnEpisodePrefersCanonicalNumberingOverTheDisplayOne()
    {
        // A re-mapped release (anime absolute numbering) displays one way and is identified another;
        // the calendar must name the episode the provider would recognize.
        var series = AddSeries("Frieren");
        var episode = AddEpisode(series, season: 1, number: 28, title: "Episode 28");
        episode.IdentitySeasonNumber = 2;
        episode.IdentityEpisodeNumber = 4;
        _database.SaveChanges();
        AddPlay(episode.Id, "2026-07-11T21:00:00Z");

        var entry = Assert.Single((await LoadMonthAsync()).Events);

        Assert.Equal(2, entry.SeasonNumber);
        Assert.Equal(4, entry.EpisodeNumber);
    }

    [Fact]
    public async Task PlaysOutsideTheRangeAreExcluded()
    {
        var movie = AddMovie("Dune");
        AddPlay(movie.Id, "2026-06-30T23:00:00Z");
        AddPlay(movie.Id, "2026-07-15T10:00:00Z");
        AddPlay(movie.Id, "2026-08-01T00:00:00Z");

        var result = await LoadMonthAsync();

        var entry = Assert.Single(result.Events);
        Assert.Equal(DateTimeOffset.Parse("2026-07-15T10:00:00Z"), entry.WatchedAt);
    }

    [Fact]
    public async Task TheRangeEndIsExclusive()
    {
        // Adjacent month requests must not both claim a play on the boundary instant.
        var movie = AddMovie("Tenet");
        AddPlay(movie.Id, "2026-08-01T00:00:00Z");

        var result = await Service().LoadAsync(
            _userId,
            DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            CancellationToken.None);

        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task UndatedMarksAreCountedByKindAndNeverPlacedOnTheGrid()
    {
        // A timeless mark says "watched", not "watched then" — it gets no date, and the toolbar filters
        // by kind, so one total would not be answerable.
        var movie = AddMovie("Solaris");
        var series = AddSeries("Andor");
        var episode = AddEpisode(series, season: 1, number: 1, title: "Kassa");
        AddPlay(movie.Id, watchedAt: null, origin: PlaybackHistoryOrigin.Manual);
        AddPlay(episode.Id, watchedAt: null, origin: PlaybackHistoryOrigin.Legacy);
        AddPlay(episode.Id, watchedAt: null, origin: PlaybackHistoryOrigin.Manual);

        var result = await LoadMonthAsync();

        Assert.Empty(result.Events);
        Assert.Equal(1, result.Undated.Movies);
        Assert.Equal(2, result.Undated.Episodes);
    }

    [Fact]
    public async Task LatestWatchedAtLooksBeyondTheRequestedRange()
    {
        // This is what lets an empty month offer "jump to last watched month" without loading history.
        var movie = AddMovie("Stalker");
        AddPlay(movie.Id, "2026-03-02T18:00:00Z");

        var result = await LoadMonthAsync();

        Assert.Empty(result.Events);
        Assert.Equal(DateTimeOffset.Parse("2026-03-02T18:00:00Z"), result.LatestWatchedAt);
    }

    [Fact]
    public async Task WithNoHistoryAtAllTheResponseIsEmptyRatherThanNull()
    {
        var result = await LoadMonthAsync();

        Assert.Empty(result.Events);
        Assert.Equal(0, result.Undated.Movies);
        Assert.Equal(0, result.Undated.Episodes);
        Assert.Null(result.LatestWatchedAt);
    }

    [Fact]
    public async Task AnotherUsersHistoryIsNeverReturned()
    {
        var movie = AddMovie("Solaris");
        AddPlay(movie.Id, "2026-07-12T20:00:00Z", appUserId: _otherUserId);
        AddPlay(movie.Id, watchedAt: null, origin: PlaybackHistoryOrigin.Manual, appUserId: _otherUserId);

        var result = await LoadMonthAsync();

        Assert.Empty(result.Events);
        Assert.Equal(0, result.Undated.Movies);
        Assert.Null(result.LatestWatchedAt);
    }

    [Fact]
    public async Task AMoviePlayCarriesItsOwnPosterAndNoSeriesFields()
    {
        var movie = AddMovie("Arrival");
        AddPoster(movie.Id, "https://img/arrival.jpg");
        AddPlay(movie.Id, "2026-07-10T20:00:00Z");

        var entry = Assert.Single((await LoadMonthAsync()).Events);

        Assert.Equal("Movie", entry.Kind);
        Assert.Equal("https://img/arrival.jpg", entry.PosterUrl);
        Assert.Null(entry.SeriesId);
        Assert.Null(entry.SeriesTitle);
    }

    [Fact]
    public async Task OriginTravelsWithEachPlayForProvenance()
    {
        var movie = AddMovie("Arrival");
        AddPlay(movie.Id, "2026-07-10T20:00:00Z", origin: PlaybackHistoryOrigin.ProviderSync);

        var entry = Assert.Single((await LoadMonthAsync()).Events);

        Assert.Equal("ProviderSync", entry.Origin);
    }

    private WatchHistoryCalendarService Service() => new(_database);

    private Task<WatchHistoryCalendarResponse> LoadMonthAsync() => Service().LoadAsync(
        _userId,
        DateTimeOffset.Parse("2026-07-01T00:00:00Z"),
        DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
        CancellationToken.None);

    private AppUser NewUser(string hostUserId, string email) => new()
    {
        HostUserId = hostUserId, Email = email, DisplayName = email, Role = AppUserRole.User,
        CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
    };

    private MediaItem AddMovie(string title)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = _catalogId, Kind = MediaKind.Movie, Title = title,
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(item);
        _database.SaveChanges();
        return item;
    }

    private MediaItem AddSeries(string title)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = _catalogId, Kind = MediaKind.Series, Title = title,
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(item);
        _database.SaveChanges();
        return item;
    }

    private MediaItem AddEpisode(MediaItem series, int season, int number, string title)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = _catalogId, Kind = MediaKind.Episode, Title = title,
            SeriesId = series.Id, ParentIndexNumber = season, IndexNumber = number,
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(item);
        _database.SaveChanges();
        return item;
    }

    private void AddPoster(Guid itemId, string url)
    {
        _database.ImageAssets.Add(new ImageAsset
        {
            Id = Guid.NewGuid(), MediaItemId = itemId, ImageType = ImageType.Primary,
            RemotePath = url, SortOrder = 0, Provider = "tmdb", Tag = "primary",
        });
        _database.SaveChanges();
    }

    private void AddPlay(
        Guid itemId,
        string? watchedAt = null,
        PlaybackHistoryOrigin origin = PlaybackHistoryOrigin.LocalPlayback,
        int? appUserId = null)
    {
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(),
            AppUserId = appUserId ?? _userId,
            MediaItemId = itemId,
            CreatedAt = _time.GetUtcNow(),
            WatchedAt = watchedAt is null ? null : DateTimeOffset.Parse(watchedAt),
            Origin = origin,
        });
        _database.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
