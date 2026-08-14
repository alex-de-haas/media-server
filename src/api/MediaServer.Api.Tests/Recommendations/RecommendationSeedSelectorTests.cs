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

    [Theory]
    [InlineData(5, 6.5)]
    [InlineData(4, 4.0)]
    [InlineData(3, 1.7)]
    public async Task EachStarIsWorthItsPlaceOnTheCurve(int stars, double expected)
    {
        // The scale is not linear on purpose: the break between "no regrets" (3) and "loved it" (4) is
        // the large one, while 5 is a stricter grade of the same statement 4 makes.
        var movie = AddMovie("Rated", tmdbId: "1");
        AddPlay(movie.Id, "2026-07-24T20:00:00Z");
        Rate(movie.Id, stars);

        Assert.Equal(expected, Assert.Single(await Select()).Weight, precision: 6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ALowRatedTitleNeverSeeds(int stars)
    {
        // Asking TMDb what is like a film the viewer would not repeat spends one of twenty requests
        // fetching candidates the feed then has to push back down. It is still evidence — the taste
        // profile reads its facets as negatives — but never a seed.
        var disliked = AddMovie("Disliked", tmdbId: "1");
        var ordinary = AddMovie("Ordinary", tmdbId: "2");
        AddPlay(disliked.Id, "2026-07-24T20:00:00Z");
        AddPlay(ordinary.Id, "2026-07-24T20:00:00Z");
        Rate(disliked.Id, stars);

        Assert.Equal("2", Assert.Single(await Select()).Identity.TmdbId);
    }

    [Fact]
    public async Task ARatingDoesNotFadeWithTime()
    {
        // The whole point of the scale: a rating is a standing statement about taste, so a film loved
        // two years ago still outweighs one watched yesterday and never thought about again. Under the
        // 90-day half-life it would have decayed to 0.02 against that 1.0.
        var loved = AddMovie("Loved long ago", tmdbId: "1");
        var yesterday = AddMovie("Watched yesterday", tmdbId: "2");
        AddPlay(loved.Id, "2024-07-24T20:00:00Z");
        AddPlay(yesterday.Id, "2026-07-24T20:00:00Z");
        Rate(loved.Id, 5);

        var seeds = await Select();

        Assert.Equal(["1", "2"], seeds.Select(seed => seed.Identity.TmdbId));
        Assert.Equal(6.5, seeds[0].Weight, precision: 6);
    }

    [Fact]
    public async Task RecencyStillOrdersTitlesInsideOneRatingBand()
    {
        // Rated weights are constants, so recency does its work in the tiebreak rather than as a
        // multiplier: it chooses between titles the viewer values equally.
        var older = AddMovie("Older five", tmdbId: "1");
        var newer = AddMovie("Newer five", tmdbId: "2");
        AddPlay(older.Id, "2026-01-24T20:00:00Z");
        AddPlay(newer.Id, "2026-07-24T20:00:00Z");
        Rate(older.Id, 5);
        Rate(newer.Id, 5);

        var seeds = await Select();

        Assert.Equal(["2", "1"], seeds.Select(seed => seed.Identity.TmdbId));
        Assert.Equal(seeds[0].Weight, seeds[1].Weight, precision: 6);
    }

    [Fact]
    public async Task ARatingSupersedesTheFavoriteBoostRatherThanCompoundingWithIt()
    {
        // Both express the same feeling; multiplying them would price one row at nearly ten ordinary
        // viewings. The rating is the more specific statement, so it wins outright.
        var both = AddMovie("Favorited and rated", tmdbId: "1");
        var rated = AddMovie("Rated only", tmdbId: "2");
        AddPlay(both.Id, "2026-07-24T20:00:00Z");
        AddPlay(rated.Id, "2026-07-24T20:00:00Z");
        MarkFavorite(both.Id);
        Rate(both.Id, 4);
        Rate(rated.Id, 4);

        var seeds = await Select();

        Assert.Equal(seeds[0].Weight, seeds[1].Weight, precision: 6);
    }

    [Fact]
    public async Task AFavoriteRatedTwoStarsStopsSeedingAltogether()
    {
        // The shelf placement is curation, the two stars are the judgement — and the judgement wins.
        var favorite = AddMovie("Favorited but poor", tmdbId: "1");
        AddPlay(favorite.Id, "2026-07-24T20:00:00Z");
        MarkFavorite(favorite.Id);
        Rate(favorite.Id, 2);

        Assert.Empty(await Select());
    }

    [Fact]
    public async Task AnUnratedWatchIsTheUnitEveryRatingIsPricedIn()
    {
        // With nobody rating anything the engine must rank exactly as it did before ratings existed,
        // or this is a silent change for every existing user.
        var movie = AddMovie("Today", tmdbId: "1");
        AddPlay(movie.Id, "2026-07-25T12:00:00Z");

        Assert.Equal(1.0, Assert.Single(await Select()).Weight, precision: 6);
    }

    [Fact]
    public async Task UnratedWatchesRetireFromTheWeightedSlotsOnceEnoughRatingsExist()
    {
        // No decay on ratings means a three-star title outranks any unrated watch permanently, so the
        // weighted slots fill with what the viewer actually graded.
        for (var index = 0; index < RecommendationSeedSelector.DefaultWeightedSeeds + 4; index++)
        {
            var rated = AddMovie($"Rated {index}", tmdbId: $"r{index}");
            AddPlay(rated.Id, "2025-01-20T20:00:00Z");
            Rate(rated.Id, 3);
        }

        for (var index = 0; index < 6; index++)
        {
            var fresh = AddMovie($"Fresh {index}", tmdbId: $"u{index}");
            AddPlay(fresh.Id, "2026-07-24T20:00:00Z");
        }

        var seeds = await Select();

        Assert.Equal(RecommendationSeedSelector.MaxSeeds, seeds.Count);
        Assert.All(
            seeds.Take(RecommendationSeedSelector.DefaultWeightedSeeds),
            seed => Assert.StartsWith("r", seed.Identity.TmdbId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFreshUnratedWatchStillReachesTheFeedThroughTheReservedSlots()
    {
        // Without the reserve the feed would stop noticing what the viewer watched this week the
        // moment their ratings filled the budget — and a feed that has not moved in a month is dead.
        for (var index = 0; index < RecommendationSeedSelector.MaxSeeds + 5; index++)
        {
            var rated = AddMovie($"Rated {index}", tmdbId: $"r{index}");
            AddPlay(rated.Id, "2025-01-20T20:00:00Z");
            Rate(rated.Id, 5);
        }

        var fresh = AddMovie("Watched last night", tmdbId: "u1");
        AddPlay(fresh.Id, "2026-07-24T20:00:00Z");

        var seeds = await Select();

        Assert.Equal(RecommendationSeedSelector.MaxSeeds, seeds.Count);
        Assert.Contains(seeds, seed => seed.Identity.TmdbId == "u1");
    }

    [Fact]
    public async Task ADislikedTitleCannotEnterThroughTheReservedSlots()
    {
        // The reserve is for titles weight left behind, not for ones the viewer ruled out.
        for (var index = 0; index < RecommendationSeedSelector.MaxSeeds + 5; index++)
        {
            var rated = AddMovie($"Rated {index}", tmdbId: $"r{index}");
            AddPlay(rated.Id, "2025-01-20T20:00:00Z");
            Rate(rated.Id, 5);
        }

        var disliked = AddMovie("Watched last night and hated", tmdbId: "u1");
        AddPlay(disliked.Id, "2026-07-24T20:00:00Z");
        Rate(disliked.Id, 1);

        Assert.DoesNotContain(await Select(), seed => seed.Identity.TmdbId == "u1");
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

    private void MarkFavorite(Guid itemId) => UpdateUserData(itemId, row => row.IsFavorite = true);

    private void Rate(Guid itemId, int stars) => UpdateUserData(itemId, row => row.Rating = stars);

    private void UpdateUserData(Guid itemId, Action<UserItemData> apply)
    {
        var row = _database.UserItemData.FirstOrDefault(
            data => data.AppUserId == _userId && data.MediaItemId == itemId);
        if (row is null)
        {
            row = new UserItemData { Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = itemId };
            _database.UserItemData.Add(row);
        }

        apply(row);
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
