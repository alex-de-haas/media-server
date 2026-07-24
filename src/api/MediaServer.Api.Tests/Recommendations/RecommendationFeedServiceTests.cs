using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The feed service answers what providers cannot: is this already held, already watched, already
/// dismissed — and whose feed is this anyway.
/// </summary>
public sealed class RecommendationFeedServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-25T12:00:00Z"));
    private readonly int _userId;
    private readonly int _otherUserId;
    private readonly Guid _catalogId = Guid.NewGuid();
    private readonly List<StubProvider> _providers = [];

    public RecommendationFeedServiceTests()
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

    private sealed class StubProvider(string key, params RecommendationCandidate[] candidates)
        : IRecommendationProvider
    {
        public string Key => key;

        public string DisplayName => key;

        public bool Available { get; set; } = true;

        public bool Throws { get; set; }

        public Task<bool> IsAvailableAsync(int appUserId, CancellationToken cancellationToken) =>
            Task.FromResult(Available);

        public Task<IReadOnlyList<RecommendationCandidate>> GetAsync(
            int appUserId, int limit, CancellationToken cancellationToken) =>
            Throws
                ? Task.FromException<IReadOnlyList<RecommendationCandidate>>(new InvalidOperationException("boom"))
                : Task.FromResult<IReadOnlyList<RecommendationCandidate>>(candidates);
    }

    [Fact]
    public async Task ATitleTheLibraryHoldsIsMarkedAndCarriesItsLocalIds()
    {
        // The difference between "play this" and "go find this" is the whole point of the flag.
        var movie = AddItem(MediaKind.Movie, "Local Title", "27205");
        Provider("library", Candidate("27205", 0, "TMDb Title"));

        var item = Assert.Single((await Build()).Items);

        Assert.True(item.InLibrary);
        Assert.Equal(movie.Id, item.MediaItemId);
        // The library's own title wins: that is the name shown everywhere else in the app.
        Assert.Equal("Local Title", item.Title);
    }

    [Fact]
    public async Task ATitleTheLibraryLacksIsOfferedAsADiscovery()
    {
        Provider("library", Candidate("27205", 0, "Inception"));

        var item = Assert.Single((await Build()).Items);

        Assert.False(item.InLibrary);
        Assert.Null(item.MediaItemId);
        Assert.Equal("Inception", item.Title);
    }

    [Fact]
    public async Task AWatchedMovieIsNeverRecommended()
    {
        var movie = AddItem(MediaKind.Movie, "Seen", "27205");
        MarkPlayed(movie.Id);
        Provider("library", Candidate("27205", 0));

        Assert.Empty((await Build()).Items);
    }

    [Fact]
    public async Task ASeriesWithAnyEpisodePlayedIsNeverRecommended()
    {
        // A part-watched show belongs to Next Up; suggesting it as discovery would be nonsense.
        var series = AddItem(MediaKind.Series, "Started", "95396");
        var episode = AddItem(MediaKind.Episode, "S1E1", null, series.Id);
        AddPlay(episode.Id);
        Provider("library", new RecommendationCandidate(
            new RecommendationIdentity(RecommendationKind.Series, "95396"), "Started", 2022, null, 0));

        Assert.Empty((await Build()).Items);
    }

    [Fact]
    public async Task AnotherUsersViewingDoesNotFilterThisUsersFeed()
    {
        var movie = AddItem(MediaKind.Movie, "Seen by them", "27205");
        MarkPlayed(movie.Id, _otherUserId);
        Provider("library", Candidate("27205", 0));

        Assert.Single((await Build()).Items);
    }

    [Fact]
    public async Task AHiddenTitleStaysOutUntilItIsRestored()
    {
        Provider("library", Candidate("27205", 0));
        var service = Service();
        var identity = new RecommendationIdentity(RecommendationKind.Movie, "27205");

        await service.HideAsync(_userId, identity, _time.GetUtcNow(), CancellationToken.None);
        Assert.Empty((await Build()).Items);

        await service.UnhideAsync(_userId, identity, CancellationToken.None);
        Assert.Single((await Build()).Items);
    }

    [Fact]
    public async Task HidingTwiceIsTheSameIntentNotAnError()
    {
        var service = Service();
        var identity = new RecommendationIdentity(RecommendationKind.Movie, "27205");

        await service.HideAsync(_userId, identity, _time.GetUtcNow(), CancellationToken.None);
        await service.HideAsync(_userId, identity, _time.GetUtcNow(), CancellationToken.None);

        Assert.Single(_database.RecommendationHides);
    }

    [Fact]
    public async Task OneUsersHideDoesNotAffectAnother()
    {
        Provider("library", Candidate("27205", 0));
        await Service().HideAsync(
            _otherUserId, new RecommendationIdentity(RecommendationKind.Movie, "27205"),
            _time.GetUtcNow(), CancellationToken.None);

        Assert.Single((await Build()).Items);
    }

    [Fact]
    public async Task FilteringHappensAfterFusionSoTheFeedIsNotLeftShort()
    {
        // Excluding watched titles from the fused head must not simply shorten the result.
        var seen = AddItem(MediaKind.Movie, "Seen", "1");
        MarkPlayed(seen.Id);
        Provider("library", Candidate("1", 0), Candidate("2", 1), Candidate("3", 2));

        var items = (await Build(limit: 2)).Items;

        Assert.Equal(2, items.Count);
        Assert.Equal(["2", "3"], items.Select(item => item.TmdbId));
    }

    [Fact]
    public async Task TheKindFilterNarrowsTheFeed()
    {
        Provider(
            "library",
            Candidate("1", 0),
            new RecommendationCandidate(
                new RecommendationIdentity(RecommendationKind.Series, "2"), "A Show", 2022, null, 1));

        var movies = (await Build(kind: RecommendationKind.Movie)).Items;
        var series = (await Build(kind: RecommendationKind.Series)).Items;

        Assert.Equal("Movie", Assert.Single(movies).Kind);
        Assert.Equal("Series", Assert.Single(series).Kind);
    }

    [Fact]
    public async Task WithNoPreferenceEverySourceIsUsedAndReported()
    {
        Provider("library", Candidate("1", 0));
        Provider("trakt", Candidate("2", 0));

        var feed = await Build();

        Assert.Equal(["library", "trakt"], feed.Sources.Select(source => source.Key).Order());
        Assert.Equal(["library", "trakt"], feed.SelectedSources.Order());
        Assert.Equal(2, feed.Items.Count);
    }

    [Fact]
    public async Task APreferenceNarrowsWhichSourcesContribute()
    {
        Provider("library", Candidate("1", 0));
        Provider("trakt", Candidate("2", 0));

        await Service().SetSourcesAsync(_userId, ["trakt"], _time.GetUtcNow(), CancellationToken.None);
        var feed = await Build();

        Assert.Equal("2", Assert.Single(feed.Items).TmdbId);
        // Unselected sources are still reported, so the control can offer them back.
        Assert.Equal(2, feed.Sources.Count);
    }

    [Fact]
    public async Task ClearingThePreferenceRestoresEverySource()
    {
        Provider("library", Candidate("1", 0));
        Provider("trakt", Candidate("2", 0));
        var service = Service();

        await service.SetSourcesAsync(_userId, ["trakt"], _time.GetUtcNow(), CancellationToken.None);
        await service.SetSourcesAsync(_userId, null, _time.GetUtcNow(), CancellationToken.None);

        Assert.Equal(2, (await Build()).Items.Count);
    }

    [Fact]
    public async Task APreferenceNamingOnlyVanishedSourcesFallsBackRatherThanEmptying()
    {
        // A user who selected Trakt and later disconnected it must not be left staring at nothing.
        Provider("library", Candidate("1", 0));
        await Service().SetSourcesAsync(_userId, ["trakt"], _time.GetUtcNow(), CancellationToken.None);

        var feed = await Build();

        Assert.Single(feed.Items);
        Assert.Equal(["library"], feed.SelectedSources);
    }

    [Fact]
    public async Task AnUnavailableProviderIsNeitherAskedNorOffered()
    {
        Provider("library", Candidate("1", 0));
        var trakt = Provider("trakt", Candidate("2", 0));
        trakt.Available = false;

        var feed = await Build();

        Assert.Equal("library", Assert.Single(feed.Sources).Key);
        Assert.Equal("1", Assert.Single(feed.Items).TmdbId);
    }

    [Fact]
    public async Task AProviderThatThrowsDoesNotCostTheUserTheOthers()
    {
        Provider("library", Candidate("1", 0));
        var trakt = Provider("trakt", Candidate("2", 0));
        trakt.Throws = true;

        Assert.Equal("1", Assert.Single((await Build()).Items).TmdbId);
    }

    /// <summary>Answers from a fixed table; the real one costs a TMDb request per title.</summary>
    private sealed class StubPosters : ITmdbPosterLookup
    {
        public Dictionary<RecommendationIdentity, string> Urls { get; } = [];

        public List<RecommendationIdentity> Asked { get; } = [];

        public Task<IReadOnlyDictionary<RecommendationIdentity, string>> ForAsync(
            IReadOnlyCollection<RecommendationIdentity> identities, CancellationToken cancellationToken)
        {
            Asked.AddRange(identities);
            return Task.FromResult<IReadOnlyDictionary<RecommendationIdentity, string>>(
                identities.Where(Urls.ContainsKey).ToDictionary(identity => identity, identity => Urls[identity]));
        }
    }

    private readonly StubPosters _posters = new();

    private RecommendationFeedService Service()
    {
        var registry = new RecommendationProviderRegistry(
            _providers, NullLogger<RecommendationProviderRegistry>.Instance);
        return new RecommendationFeedService(
            _database, registry, _posters, NullLogger<RecommendationFeedService>.Instance);
    }

    [Fact]
    public async Task ACandidateWithoutArtworkGetsItsPosterLookedUp()
    {
        // Trakt returns none, so without this every Trakt-only suggestion renders as a grey box.
        Provider("trakt", Candidate("27205", 0));
        _posters.Urls[new RecommendationIdentity(RecommendationKind.Movie, "27205")] = "https://img/p.jpg";

        var item = Assert.Single((await Build()).Items);

        Assert.Equal("https://img/p.jpg", item.PosterUrl);
    }

    [Fact]
    public async Task PostersAreOnlyLookedUpForCardsThatSurvivedFiltering()
    {
        // Each lookup is a TMDb request; paying for candidates nobody will see would be waste.
        var seen = AddItem(MediaKind.Movie, "Seen", "1");
        MarkPlayed(seen.Id);
        Provider("trakt", Candidate("1", 0), Candidate("2", 1));

        await Build(limit: 1);

        Assert.Equal("2", Assert.Single(_posters.Asked).TmdbId);
    }

    [Fact]
    public async Task ACandidateThatAlreadyHasArtworkIsNotLookedUpAgain()
    {
        Provider("library", new RecommendationCandidate(
            new RecommendationIdentity(RecommendationKind.Movie, "1"), "Has one", 2024, "https://img/have.jpg", 0));

        var item = Assert.Single((await Build()).Items);

        Assert.Equal("https://img/have.jpg", item.PosterUrl);
        Assert.Empty(_posters.Asked);
    }

    private Task<RecommendationFeedDto> Build(RecommendationKind? kind = null, int limit = 20) =>
        Service().BuildAsync(_userId, kind, limit, CancellationToken.None);

    private StubProvider Provider(string key, params RecommendationCandidate[] candidates)
    {
        var provider = new StubProvider(key, candidates);
        _providers.Add(provider);
        return provider;
    }

    private static RecommendationCandidate Candidate(string tmdbId, int rank, string? title = null) =>
        new(new RecommendationIdentity(RecommendationKind.Movie, tmdbId), title ?? $"Title {tmdbId}", 2024, null, rank);

    private AppUser NewUser(string hostUserId, string email) => new()
    {
        HostUserId = hostUserId, Email = email, DisplayName = email, Role = AppUserRole.User,
        CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
    };

    private MediaItem AddItem(MediaKind kind, string title, string? tmdbId, Guid? seriesId = null)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = _catalogId,
            Kind = kind, Title = title, SeriesId = seriesId,
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

    private void MarkPlayed(Guid itemId, int? appUserId = null)
    {
        _database.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = appUserId ?? _userId, MediaItemId = itemId, Played = true, PlayCount = 1,
        });
        _database.SaveChanges();
    }

    private void AddPlay(Guid itemId)
    {
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = itemId,
            CreatedAt = _time.GetUtcNow(), WatchedAt = _time.GetUtcNow(),
            Origin = PlaybackHistoryOrigin.LocalPlayback,
        });
        _database.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
