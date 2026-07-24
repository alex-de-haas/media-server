using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// Seed selection is the whole of the built-in engine's personalization — TMDb only answers "what is
/// like X", so which X's are chosen, and how strongly each counts, is the recommendation.
/// </summary>
public sealed class RecommendationSeedSelectorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-25T12:00:00Z"));
    private readonly int _userId;
    private readonly int _otherUserId;
    private readonly Guid _catalogId = Guid.NewGuid();

    public RecommendationSeedSelectorTests()
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
    public async Task AnEpisodePlaySeedsItsSeriesNotTheEpisode()
    {
        // "More like this show" is the useful question; "more like episode 4" is not a thing TMDb
        // can answer, and the episode has no TMDb id of its own here anyway.
        var series = AddSeries("Severance", tmdbId: "95396");
        var episode = AddEpisode(series, season: 2, number: 3);
        AddPlay(episode.Id, "2026-07-20T20:00:00Z");

        var seed = Assert.Single(await Select());

        Assert.Equal(RecommendationKind.Series, seed.Identity.Kind);
        Assert.Equal("95396", seed.Identity.TmdbId);
    }

    [Fact]
    public async Task EveryEpisodeOfOneSeriesCollapsesToOneSeed()
    {
        // Otherwise a binge would spend the whole seed budget on a single show and crowd out
        // everything else the user watched.
        var series = AddSeries("Severance", tmdbId: "95396");
        for (var number = 1; number <= 10; number++)
        {
            AddPlay(AddEpisode(series, season: 1, number: number).Id, $"2026-07-{10 + number}T20:00:00Z");
        }

        Assert.Single(await Select());
    }

    [Fact]
    public async Task RecentTitlesOutweighOldOnes()
    {
        var recent = AddMovie("Recent", tmdbId: "1");
        var old = AddMovie("Old", tmdbId: "2");
        AddPlay(recent.Id, "2026-07-24T20:00:00Z");
        // Two half-lives back: worth roughly a quarter as much.
        AddPlay(old.Id, "2026-01-26T20:00:00Z");

        var seeds = await Select();

        Assert.Equal(["1", "2"], seeds.Select(seed => seed.Identity.TmdbId));
        Assert.True(seeds[0].Weight > seeds[1].Weight * 2);
    }

    [Fact]
    public async Task AFavoriteOutweighsAnOrdinaryPlayOfTheSameAge()
    {
        var favorite = AddMovie("Favorite", tmdbId: "1");
        var ordinary = AddMovie("Ordinary", tmdbId: "2");
        AddPlay(favorite.Id, "2026-07-20T20:00:00Z");
        AddPlay(ordinary.Id, "2026-07-20T20:00:00Z");
        MarkFavorite(favorite.Id);

        var seeds = await Select();

        Assert.Equal("1", seeds[0].Identity.TmdbId);
        Assert.True(seeds[0].Weight > seeds[1].Weight);
    }

    [Fact]
    public async Task ARewatchedMovieOutweighsASingleViewingOfTheSameAge()
    {
        var rewatched = AddMovie("Rewatched", tmdbId: "1");
        var once = AddMovie("Once", tmdbId: "2");
        AddPlay(rewatched.Id, "2026-07-20T20:00:00Z");
        AddPlay(rewatched.Id, "2026-07-21T20:00:00Z");
        AddPlay(once.Id, "2026-07-21T20:00:00Z");

        var seeds = await Select();

        Assert.Equal("1", seeds[0].Identity.TmdbId);
    }

    [Fact]
    public async Task UndatedMarksStillSeedButCarryNoRecencyBonus()
    {
        // A library migrated from aggregate counts holds only timeless marks; dropping them would
        // make it look like nobody had watched anything.
        var timeless = AddMovie("Timeless", tmdbId: "1");
        var dated = AddMovie("Dated", tmdbId: "2");
        AddPlay(timeless.Id, watchedAt: null, origin: PlaybackHistoryOrigin.Manual);
        AddPlay(dated.Id, "2026-07-24T20:00:00Z");

        var seeds = await Select();

        Assert.Equal(2, seeds.Count);
        Assert.Equal("2", seeds[0].Identity.TmdbId);
    }

    [Fact]
    public async Task ItemsWithoutATmdbIdAreSkipped()
    {
        // Nothing to ask TMDb about; reporting it is not this component's job.
        var unidentified = AddMovie("Unidentified", tmdbId: null);
        AddPlay(unidentified.Id, "2026-07-24T20:00:00Z");

        Assert.Empty(await Select());
    }

    [Fact]
    public async Task ATmdbIdInTheProvidersMapIsAcceptedToo()
    {
        // Items identified by IMDb still often carry a TMDb id alongside.
        var movie = AddMovie("Mapped", tmdbId: null);
        movie.IdentityProvider = "imdb";
        movie.IdentityProviderId = "tt1375666";
        movie.Providers["tmdb"] = "27205";
        _database.SaveChanges();
        AddPlay(movie.Id, "2026-07-24T20:00:00Z");

        Assert.Equal("27205", Assert.Single(await Select()).Identity.TmdbId);
    }

    [Fact]
    public async Task TheSeedBudgetIsCappedBecauseEachOneCostsARequest()
    {
        for (var index = 0; index < RecommendationSeedSelector.MaxSeeds + 7; index++)
        {
            var movie = AddMovie($"Movie {index}", tmdbId: index.ToString());
            AddPlay(movie.Id, "2026-07-20T20:00:00Z");
        }

        Assert.Equal(RecommendationSeedSelector.MaxSeeds, (await Select()).Count);
    }

    [Fact]
    public async Task AnotherUsersHistoryNeverSeedsThisUsersFeed()
    {
        var movie = AddMovie("Theirs", tmdbId: "1");
        AddPlay(movie.Id, "2026-07-24T20:00:00Z", appUserId: _otherUserId);

        Assert.Empty(await Select());
    }

    [Fact]
    public async Task WithNoHistoryThereAreNoSeeds()
    {
        AddMovie("Never watched", tmdbId: "1");

        Assert.Empty(await Select());
    }

    private Task<IReadOnlyList<RecommendationSeed>> Select() =>
        new RecommendationSeedSelector(_database, _time).SelectAsync(_userId, CancellationToken.None);

    private AppUser NewUser(string hostUserId, string email) => new()
    {
        HostUserId = hostUserId, Email = email, DisplayName = email, Role = AppUserRole.User,
        CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
    };

    private MediaItem AddMovie(string title, string? tmdbId) => Add(MediaKind.Movie, title, tmdbId, null);

    private MediaItem AddSeries(string title, string tmdbId) => Add(MediaKind.Series, title, tmdbId, null);

    private MediaItem AddEpisode(MediaItem series, int season, int number)
    {
        var item = Add(MediaKind.Episode, $"S{season}E{number}", null, series.Id);
        item.ParentIndexNumber = season;
        item.IndexNumber = number;
        _database.SaveChanges();
        return item;
    }

    private MediaItem Add(MediaKind kind, string title, string? tmdbId, Guid? seriesId)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = _catalogId, Kind = kind, Title = title, SeriesId = seriesId,
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        if (tmdbId is not null)
        {
            item.IdentityProvider = "tmdb";
            item.IdentityProviderId = tmdbId;
        }

        _database.MediaItems.Add(item);
        _database.SaveChanges();
        return item;
    }

    private void MarkFavorite(Guid itemId)
    {
        _database.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = itemId, IsFavorite = true,
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
