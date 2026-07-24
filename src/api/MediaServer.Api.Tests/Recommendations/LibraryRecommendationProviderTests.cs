using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The built-in engine's aggregation: what several seeds agreeing means, and what must never appear
/// in the result.
/// </summary>
public sealed class LibraryRecommendationProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-25T12:00:00Z"));
    private readonly StubSource _tmdb = new();
    private readonly int _userId;
    private readonly Guid _catalogId = Guid.NewGuid();

    public LibraryRecommendationProviderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        var user = new AppUser
        {
            HostUserId = "host-1", Email = "alex@example.com", DisplayName = "Alex",
            Role = AppUserRole.User, CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
        };
        _database.AppUsers.Add(user);
        _database.SaveChanges();
        _userId = user.Id;

        _database.Catalogs.Add(new Catalog
        {
            Id = _catalogId, Name = "Library", Type = CatalogType.Movie, Root = "/m",
            CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
    }

    /// <summary>Answers from a fixed table instead of the network, and records what was asked.</summary>
    private sealed class StubSource : ITmdbRecommendationSource
    {
        public Dictionary<string, List<TmdbRecommendedTitle>> Lists { get; } = [];

        public List<RecommendationIdentity> Asked { get; } = [];

        public Task<IReadOnlyList<TmdbRecommendedTitle>> ForSeedAsync(
            RecommendationIdentity seed, CancellationToken cancellationToken)
        {
            Asked.Add(seed);
            return Task.FromResult<IReadOnlyList<TmdbRecommendedTitle>>(
                Lists.TryGetValue(seed.TmdbId, out var list) ? list : []);
        }
    }

    [Fact]
    public async Task ATitleSeveralSeedsAgreeOnOutranksOneASingleSeedLoves()
    {
        // Breadth over depth: agreement across a viewer's own taste is the stronger signal, even
        // when the lone recommendation sits at the very top of its seed's list.
        SeedWatched("1");
        SeedWatched("2");
        _tmdb.Lists["1"] = [Title("shared"), Title("only-a")];
        _tmdb.Lists["2"] = [Title("only-b"), Title("shared")];

        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Equal("shared", result[0].Identity.TmdbId);
    }

    [Fact]
    public async Task TmdbsOwnOrderBreaksTiesWithinASeed()
    {
        SeedWatched("1");
        _tmdb.Lists["1"] = [Title("first"), Title("second"), Title("third")];

        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Equal(["first", "second", "third"], result.Select(entry => entry.Identity.TmdbId));
    }

    [Fact]
    public async Task ASeedIsNeverRecommendedBackToTheUser()
    {
        // They already watched it — and one seed recommending another is not news either.
        SeedWatched("1");
        SeedWatched("2");
        _tmdb.Lists["1"] = [Title("2"), Title("fresh")];
        _tmdb.Lists["2"] = [Title("1")];

        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Equal("fresh", Assert.Single(result).Identity.TmdbId);
    }

    [Fact]
    public async Task WithNothingWatchedTheEngineSaysNothing()
    {
        // Filler (trending, popular) would not be a recommendation, and pretending otherwise is worse
        // than an empty row the UI can explain.
        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Empty(result);
        Assert.Empty(_tmdb.Asked);
    }

    [Fact]
    public async Task RanksAreDenseAndZeroBasedSoFusionCanReadPosition()
    {
        SeedWatched("1");
        _tmdb.Lists["1"] = [Title("a"), Title("b"), Title("c")];

        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Equal([0, 1, 2], result.Select(entry => entry.Rank));
    }

    [Fact]
    public async Task TheLimitIsHonoured()
    {
        SeedWatched("1");
        _tmdb.Lists["1"] = [.. Enumerable.Range(0, 15).Select(index => Title($"c{index}"))];

        var result = await Provider().GetAsync(_userId, 5, CancellationToken.None);

        Assert.Equal(5, result.Count);
    }

    [Fact]
    public async Task SeriesSeedsAskForSeriesRecommendations()
    {
        // A kind mix-up here would ask TMDb's movie endpoint about a show id and quietly return
        // someone else's film.
        var series = AddItem(MediaKind.Series, "Severance", "95396");
        var episode = AddItem(MediaKind.Episode, "S1E1", null, series.Id);
        AddPlay(episode.Id);
        _tmdb.Lists["95396"] = [Title("similar-show")];

        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Equal(RecommendationKind.Series, Assert.Single(_tmdb.Asked).Kind);
        Assert.Equal(RecommendationKind.Series, Assert.Single(result).Identity.Kind);
    }

    [Fact]
    public async Task ASeedThatTmdbCannotAnswerForIsSurvivable()
    {
        // An unknown title or a brief outage yields an empty list; the other seeds still carry the feed.
        SeedWatched("1");
        SeedWatched("2");
        _tmdb.Lists["2"] = [Title("from-the-other-seed")];

        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Equal("from-the-other-seed", Assert.Single(result).Identity.TmdbId);
    }

    [Fact]
    public async Task PosterPathsBecomeAbsoluteUrls()
    {
        SeedWatched("1");
        _tmdb.Lists["1"] = [new TmdbRecommendedTitle("a", "A Title", 2021, "/poster.jpg")];

        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Equal("https://image.tmdb.org/t/p/w500/poster.jpg", Assert.Single(result).PosterUrl);
    }

    [Fact]
    public async Task WithoutATmdbKeyTheEngineIsUnavailable()
    {
        var unconfigured = new LibraryRecommendationProvider(
            new RecommendationSeedSelector(_database, _time),
            _tmdb,
            new MediaServerSettings(),
            NullLogger<LibraryRecommendationProvider>.Instance);

        Assert.False(await unconfigured.IsAvailableAsync(_userId, CancellationToken.None));
        Assert.True(await Provider().IsAvailableAsync(_userId, CancellationToken.None));
    }

    private LibraryRecommendationProvider Provider() => new(
        new RecommendationSeedSelector(_database, _time),
        _tmdb,
        new MediaServerSettings { TmdbApiKey = "key" },
        NullLogger<LibraryRecommendationProvider>.Instance);

    private static TmdbRecommendedTitle Title(string tmdbId) => new(tmdbId, $"Title {tmdbId}", 2024, null);

    private void SeedWatched(string tmdbId) => AddPlay(AddItem(MediaKind.Movie, $"Movie {tmdbId}", tmdbId).Id);

    private MediaItem AddItem(MediaKind kind, string title, string? tmdbId, Guid? seriesId = null)
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
