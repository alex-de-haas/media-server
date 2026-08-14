using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Recommendations.Generation;
using MediaServer.Api.Recommendations.Profile;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The feed service answers what the engine deliberately does not: is this already held, already
/// watched, already dismissed — and whose feed is this anyway.
/// </summary>
public sealed class RecommendationFeedServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-25T12:00:00Z"));
    private readonly int _userId;
    private readonly int _otherUserId;
    private readonly Guid _catalogId = Guid.NewGuid();
    private readonly StubSource _tmdb = new();
    private Guid _seedId;

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

        // Every test needs something watched, because the engine only speaks when the viewer has.
        // Two seeds, one of each kind, because a candidate inherits its seed's kind — a series
        // suggestion can only come from a series seed. Both are deliberately unremarkable: what
        // these tests are about is what happens to a candidate afterwards, not how it was chosen.
        _seedId = AddItem(MediaKind.Movie, "The seed", "seed").Id;
        AddPlay(_seedId);
        var seedShow = AddItem(MediaKind.Series, "The series seed", "series-seed");
        AddPlay(AddItem(MediaKind.Episode, "Seed S1E1", null, seedShow.Id).Id);
    }

    /// <summary>Answers from a fixed table instead of the network — the real boundary, stubbed.</summary>
    private sealed class StubSource : ITmdbRecommendationSource
    {
        public List<TmdbRecommendedTitle> Movies { get; } = [];

        public List<TmdbRecommendedTitle> Series { get; } = [];

        public Task<IReadOnlyList<TmdbRecommendedTitle>> ForSeedAsync(
            RecommendationIdentity seed, TmdbRecommendationGenerator generator, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TmdbRecommendedTitle>>(
                generator != TmdbRecommendationGenerator.Seeds
                    ? []
                    : seed.Kind == RecommendationKind.Series ? Series : Movies);

        public Task<IReadOnlyList<TmdbRecommendedTitle>> ForListAsync(
            TmdbRecommendationGenerator generator,
            RecommendationKind kind,
            string cacheKey,
            string path,
            TimeSpan lifetime,
            IReadOnlyList<string> arrays,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TmdbRecommendedTitle>>(
                kind == RecommendationKind.Series ? Series : []);
    }

    [Fact]
    public async Task ATitleTheLibraryHoldsIsMarkedAndCarriesItsLocalIds()
    {
        // The difference between "play this" and "go find this" is the whole point of the flag.
        var movie = AddItem(MediaKind.Movie, "Local Title", "27205");
        SuggestTitled("27205", "TMDb Title");

        var item = Assert.Single((await Build()).Items);

        Assert.True(item.InLibrary);
        // The media-item id specifically: the detail routes are {id:guid} and resolve by it.
        Assert.Equal(movie.Id, item.MediaItemId);
        // The library's own title wins: that is the name shown everywhere else in the app.
        Assert.Equal("Local Title", item.Title);
    }

    [Fact]
    public async Task ATitleTheLibraryLacksIsOfferedAsADiscovery()
    {
        SuggestTitled("27205", "Inception");

        var item = Assert.Single((await Build()).Items);

        Assert.False(item.InLibrary);
        Assert.Null(item.MediaItemId);
        Assert.Equal("Inception", item.Title);
    }

    [Fact]
    public async Task AHeldTitleWithNoTmdbPosterFallsBackToItsLibraryArtwork()
    {
        // `held` and `collections` build candidates from library rows and carry no TMDb path, so
        // without this the suggestions the instance already owns — the ones worth showing most —
        // would all render as "No poster".
        var movie = AddItem(MediaKind.Movie, "Local Title", "27205");
        _database.ImageAssets.Add(new ImageAsset
        {
            Id = Guid.NewGuid(), MediaItemId = movie.Id, ImageType = ImageType.Primary,
            Provider = "tmdb", RemotePath = "https://cdn/local.jpg", Tag = "tag-1",
        });
        _database.SaveChanges();
        _tmdb.Movies.Add(new TmdbRecommendedTitle("27205", "TMDb Title", 2010, null));

        Assert.Equal("https://cdn/local.jpg", Assert.Single((await Build()).Items).PosterUrl);
    }

    [Fact]
    public async Task ATmdbPosterWinsOverTheLibraryCopyWhenTheCandidateHasOne()
    {
        // The path came with the list the candidate arrived in; preferring it keeps a discovery and a
        // held title looking the same, and costs nothing.
        AddItem(MediaKind.Movie, "Local Title", "27205");
        _tmdb.Movies.Add(new TmdbRecommendedTitle("27205", "TMDb Title", 2010, "/tmdb.jpg"));

        Assert.Equal(
            "https://image.tmdb.org/t/p/w500/tmdb.jpg",
            Assert.Single((await Build()).Items).PosterUrl);
    }

    [Fact]
    public async Task AWatchedMovieIsNeverRecommended()
    {
        var movie = AddItem(MediaKind.Movie, "Seen", "27205");
        MarkPlayed(movie.Id);
        Suggest("27205");

        Assert.Empty((await Build()).Items);
    }

    [Fact]
    public async Task ASeriesWithAnyEpisodePlayedIsNeverRecommended()
    {
        // A part-watched show belongs to Next Up; suggesting it as discovery would be nonsense.
        var series = AddItem(MediaKind.Series, "Started", "95396");
        var episode = AddItem(MediaKind.Episode, "S1E1", null, series.Id);
        AddPlay(episode.Id);
        SuggestSeries("95396", "Started");

        Assert.Empty((await Build()).Items);
    }

    [Fact]
    public async Task WatchingAnySecondCopyOfATitleStillExcludesIt()
    {
        // A 4K edition beside a regular one is one title to the viewer. Tracking only one copy would
        // recommend something they finished last night on the other.
        var regular = AddItem(MediaKind.Movie, "Dune", "438631");
        var fourK = AddItem(MediaKind.Movie, "Dune 4K", "438631");
        MarkPlayed(fourK.Id);
        Suggest("438631");

        Assert.Empty((await Build()).Items);
        Assert.NotEqual(regular.Id, fourK.Id);
    }

    [Fact]
    public async Task AMultiCopyTitleLinksToAStableCopy()
    {
        // Adding a second edition must not change the link a user already follows.
        var first = AddItem(MediaKind.Movie, "Dune", "438631");
        _time.Advance(TimeSpan.FromDays(1));
        AddItem(MediaKind.Movie, "Dune 4K", "438631");
        Suggest("438631");

        Assert.Equal(first.Id, Assert.Single((await Build()).Items).MediaItemId);
    }

    [Fact]
    public async Task AnotherUsersViewingDoesNotFilterThisUsersFeed()
    {
        var movie = AddItem(MediaKind.Movie, "Seen by them", "27205");
        MarkPlayed(movie.Id, _otherUserId);
        Suggest("27205");

        Assert.Single((await Build()).Items);
    }

    [Fact]
    public async Task AHiddenTitleStaysOutUntilItIsRestored()
    {
        Suggest("27205");
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
        Suggest("27205");
        await Service().HideAsync(
            _otherUserId, new RecommendationIdentity(RecommendationKind.Movie, "27205"),
            _time.GetUtcNow(), CancellationToken.None);

        Assert.Single((await Build()).Items);
    }

    [Fact]
    public async Task FilteringHappensAfterRankingSoTheFeedIsNotLeftShort()
    {
        // Excluding watched titles from the ranked head must not simply shorten the result.
        var seen = AddItem(MediaKind.Movie, "Seen", "1");
        MarkPlayed(seen.Id);
        Suggest("1", "2", "3");

        var items = (await Build(limit: 2)).Items;

        Assert.Equal(2, items.Count);
        Assert.Equal(["2", "3"], items.Select(item => item.TmdbId));
    }

    [Fact]
    public async Task TheKindFilterNarrowsTheFeed()
    {
        Suggest("1");
        SuggestSeries("2", "A Show");

        var movies = (await Build(kind: RecommendationKind.Movie)).Items;
        var series = (await Build(kind: RecommendationKind.Series)).Items;

        Assert.Equal("Movie", Assert.Single(movies).Kind);
        Assert.Equal("Series", Assert.Single(series).Kind);
    }








    private RecommendationFeedService Service() => new(
        _database, Engine(), NullLogger<RecommendationFeedService>.Instance);

    /// <summary>
    /// The real engine, with only the behavioural seed generator wired.
    /// </summary>
    /// <remarks>
    /// These tests are about what the feed service does <em>after</em> ranking — held, watched,
    /// hidden, whose feed this is — so the ranking itself is kept as boring as possible. Wiring the
    /// local generators would make every assertion depend on what the fixture's library happens to
    /// hold, which is the opposite of what is being tested here.
    /// </remarks>
    private RecommendationEngine Engine() => new(
        _database,
        new RecommendationSeedSelector(_database, _time),
        [SeedListGenerator.Recommendations(_tmdb)],
        new TitleFacetReader(_database),
        new TasteProfileCache(),
        new TasteProfileBuilder(_database, new TitleFacetReader(_database), new LibraryFacetIndexCache(), _time),
        new RecommendationScorer(),
        new RecommendationReranker(),
        new RecommendationPreferenceStore(_database),
        NullLogger<RecommendationEngine>.Instance);




    private Task<RecommendationFeedDto> Build(RecommendationKind? kind = null, int limit = 20) =>
        Service().BuildAsync(_userId, kind, limit, CancellationToken.None);

    /// <summary>What TMDb suggests for the seed, in order. The engine's only input here.</summary>
    private void Suggest(params string[] tmdbIds)
    {
        foreach (var tmdbId in tmdbIds)
        {
            _tmdb.Movies.Add(new TmdbRecommendedTitle(tmdbId, $"Title {tmdbId}", 2024, null));
        }
    }

    private void SuggestTitled(string tmdbId, string title, string? posterPath = null) =>
        _tmdb.Movies.Add(new TmdbRecommendedTitle(tmdbId, title, 2024, posterPath));

    private void SuggestSeries(string tmdbId, string title) =>
        _tmdb.Series.Add(new TmdbRecommendedTitle(tmdbId, title, 2022, null));

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
