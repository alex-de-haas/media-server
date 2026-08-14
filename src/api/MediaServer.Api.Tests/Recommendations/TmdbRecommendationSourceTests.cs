using System.Net;
using System.Text;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The cache in front of TMDb: what it saves, when it refuses to trust itself, and what it does when
/// TMDb is unreachable.
/// </summary>
public sealed class TmdbRecommendationSourceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-25T12:00:00Z"));
    private readonly StubHandler _handler = new();

    public TmdbRecommendationSourceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var (status, body) = Responses.Count > 0
                ? Responses.Dequeue()
                : (HttpStatusCode.ServiceUnavailable, "");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(StubHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.themoviedb.org/") };
    }

    private const string OneResult = """
        { "results": [ { "id": 27205, "title": "Inception", "release_date": "2010-07-16", "poster_path": "/p.jpg" } ] }
        """;

    private const string OneRichResult = """
        { "results": [ {
            "id": 27205, "title": "Inception", "release_date": "2010-07-16", "poster_path": "/p.jpg",
            "genre_ids": [28, 878], "vote_average": 8.4, "vote_count": 36000,
            "popularity": 92.5, "original_language": "en"
        } ] }
        """;

    [Fact]
    public async Task TheFeaturesThatCameFreeWithTheRequestAreKeptAndCached()
    {
        // The projection is all that is persisted, so anything dropped here cannot be recovered
        // without a refetch — and these fields are what the quality and popularity terms are made of.
        _handler.Responses.Enqueue((HttpStatusCode.OK, OneRichResult));
        var seed = new RecommendationIdentity(RecommendationKind.Movie, "1");

        var fetched = Assert.Single(
            await Source().ForSeedAsync(seed, TmdbRecommendationGenerator.Seeds, CancellationToken.None));
        var cached = Assert.Single(
            await Source().ForSeedAsync(seed, TmdbRecommendationGenerator.Seeds, CancellationToken.None));

        foreach (var title in new[] { fetched, cached })
        {
            Assert.Equal([28, 878], title.GenreIds);
            Assert.Equal(8.4, title.VoteAverage);
            Assert.Equal(36_000, title.VoteCount);
            Assert.Equal(92.5, title.Popularity);
            Assert.Equal("en", title.OriginalLanguage);
        }

        Assert.Single(_handler.Requests); // the second read came from the cache
    }

    [Fact]
    public async Task TwoGeneratorsForOneSeedDoNotOverwriteEachOther()
    {
        // Keyed on (Kind, TmdbId) alone, whichever list was written first would answer both questions
        // and the second signal would silently become a copy of the first.
        _handler.Responses.Enqueue((HttpStatusCode.OK, OneResult));
        _handler.Responses.Enqueue((HttpStatusCode.OK, """
            { "results": [ { "id": 155, "title": "The Dark Knight", "release_date": "2008-07-18" } ] }
            """));
        var seed = new RecommendationIdentity(RecommendationKind.Movie, "1");

        var recommendations = await Source().ForSeedAsync(seed, TmdbRecommendationGenerator.Seeds, CancellationToken.None);
        var similar = await Source().ForSeedAsync(seed, TmdbRecommendationGenerator.Similar, CancellationToken.None);

        Assert.Equal("27205", Assert.Single(recommendations).TmdbId);
        Assert.Equal("155", Assert.Single(similar).TmdbId);
        Assert.Contains("movie/1/recommendations", _handler.Requests[0], StringComparison.Ordinal);
        Assert.Contains("movie/1/similar", _handler.Requests[1], StringComparison.Ordinal);
        Assert.Equal(2, await _database.TmdbRecommendationCache.CountAsync());
    }

    [Fact]
    public async Task ARowWrittenInAnOlderShapeIsAMissRatherThanAMisreading()
    {
        // An old payload deserializes cleanly into the current shape with every new field null, which
        // the scorer would read as "no votes" instead of "never asked". Refetching once is the fix.
        _database.TmdbRecommendationCache.Add(new TmdbRecommendationCacheEntry
        {
            Id = Guid.NewGuid(), Generator = TmdbRecommendationGenerator.Seeds, Kind = RecommendationKind.Movie,
            TmdbId = "1", PayloadVersion = 0, FetchedAt = _time.GetUtcNow(),
            Payload = """[{"id":"27205","t":"Inception","y":2010,"p":"/p.jpg"}]""",
        });
        await _database.SaveChangesAsync();
        _handler.Responses.Enqueue((HttpStatusCode.OK, OneRichResult));

        var result = Assert.Single(await Source().ForSeedAsync(
            new RecommendationIdentity(RecommendationKind.Movie, "1"),
            TmdbRecommendationGenerator.Seeds,
            CancellationToken.None));

        Assert.Single(_handler.Requests);
        Assert.Equal(8.4, result.VoteAverage);
        // Refreshed in place rather than duplicated: the row is still unique on its key.
        var row = Assert.Single(await _database.TmdbRecommendationCache.ToListAsync());
        Assert.Equal(TmdbRecommendationSource.PayloadVersion, row.PayloadVersion);
    }

    [Fact]
    public async Task AFreshCacheHitCostsNoRequest()
    {
        _handler.Responses.Enqueue((HttpStatusCode.OK, OneResult));
        var seed = new RecommendationIdentity(RecommendationKind.Movie, "1");

        var first = await Source().ForSeedAsync(seed, TmdbRecommendationGenerator.Seeds, CancellationToken.None);
        var second = await Source().ForSeedAsync(seed, TmdbRecommendationGenerator.Seeds, CancellationToken.None);

        Assert.Equal("27205", Assert.Single(first).TmdbId);
        Assert.Equal("27205", Assert.Single(second).TmdbId);
        // The whole point of the cache: a page refresh must not spend the rate limit.
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task AStaleRowIsRefetchedRatherThanServedForever()
    {
        _handler.Responses.Enqueue((HttpStatusCode.OK, OneResult));
        _handler.Responses.Enqueue((HttpStatusCode.OK,
            """{ "results": [ { "id": 999, "title": "Newer", "release_date": "2026-01-01" } ] }"""));
        var seed = new RecommendationIdentity(RecommendationKind.Movie, "1");

        await Source().ForSeedAsync(seed, TmdbRecommendationGenerator.Seeds, CancellationToken.None);
        _time.Advance(TmdbRecommendationSource.CacheLifetime + TimeSpan.FromHours(1));
        var refreshed = await Source().ForSeedAsync(seed, TmdbRecommendationGenerator.Seeds, CancellationToken.None);

        Assert.Equal("999", Assert.Single(refreshed).TmdbId);
        Assert.Equal(2, _handler.Requests.Count);
        // Refreshed in place: a TTL that grew a new row per expiry would leak rows forever.
        Assert.Single(_database.TmdbRecommendationCache);
    }

    [Fact]
    public async Task AnUnreachableTmdbFallsBackToTheStalePayload()
    {
        // A week-old list beats an empty feed; recommendations are not time-critical.
        _handler.Responses.Enqueue((HttpStatusCode.OK, OneResult));
        var seed = new RecommendationIdentity(RecommendationKind.Movie, "1");
        await Source().ForSeedAsync(seed, TmdbRecommendationGenerator.Seeds, CancellationToken.None);

        _time.Advance(TmdbRecommendationSource.CacheLifetime + TimeSpan.FromHours(1));
        _handler.Responses.Enqueue((HttpStatusCode.ServiceUnavailable, ""));
        var result = await Source().ForSeedAsync(seed, TmdbRecommendationGenerator.Seeds, CancellationToken.None);

        Assert.Equal("27205", Assert.Single(result).TmdbId);
    }

    [Fact]
    public async Task AColdMissAgainstAnUnreachableTmdbIsEmptyNotAnError()
    {
        _handler.Responses.Enqueue((HttpStatusCode.ServiceUnavailable, ""));

        var result = await Source().ForSeedAsync(
            new RecommendationIdentity(RecommendationKind.Movie, "1"), TmdbRecommendationGenerator.Seeds, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task MoviesAndSeriesUseTheirOwnTmdbEndpointsAndCacheRows()
    {
        // Asking the movie endpoint about a show id would quietly return an unrelated film.
        _handler.Responses.Enqueue((HttpStatusCode.OK, OneResult));
        _handler.Responses.Enqueue((HttpStatusCode.OK,
            """{ "results": [ { "id": 1399, "name": "A Show", "first_air_date": "2011-04-17" } ] }"""));

        await Source().ForSeedAsync(new RecommendationIdentity(RecommendationKind.Movie, "1"), TmdbRecommendationGenerator.Seeds, CancellationToken.None);
        var show = await Source().ForSeedAsync(
            new RecommendationIdentity(RecommendationKind.Series, "1"), TmdbRecommendationGenerator.Seeds, CancellationToken.None);

        Assert.Contains("movie/1/recommendations", _handler.Requests[0], StringComparison.Ordinal);
        Assert.Contains("tv/1/recommendations", _handler.Requests[1], StringComparison.Ordinal);
        // Same id, different kinds: two rows, not one shadowing the other.
        Assert.Equal(2, _database.TmdbRecommendationCache.Count());
        Assert.Equal("A Show", Assert.Single(show).Title);
    }

    [Fact]
    public async Task SeriesTitlesAndAirDatesAreReadFromTheirOwnFields()
    {
        _handler.Responses.Enqueue((HttpStatusCode.OK,
            """{ "results": [ { "id": 1399, "name": "Game of Thrones", "first_air_date": "2011-04-17" } ] }"""));

        var result = await Source().ForSeedAsync(
            new RecommendationIdentity(RecommendationKind.Series, "1"), TmdbRecommendationGenerator.Seeds, CancellationToken.None);

        var title = Assert.Single(result);
        Assert.Equal("Game of Thrones", title.Title);
        Assert.Equal(2011, title.Year);
    }

    [Fact]
    public async Task EntriesWithoutAUsableIdOrTitleAreDropped()
    {
        _handler.Responses.Enqueue((HttpStatusCode.OK,
            """{ "results": [ { "title": "No id" }, { "id": 5 }, { "id": 7, "title": "Good" } ] }"""));

        var result = await Source().ForSeedAsync(
            new RecommendationIdentity(RecommendationKind.Movie, "1"), TmdbRecommendationGenerator.Seeds, CancellationToken.None);

        Assert.Equal("7", Assert.Single(result).TmdbId);
    }

    private TmdbRecommendationSource Source() => new(
        _database,
        new StubFactory(_handler),
        new MediaServerSettings { TmdbApiKey = "key", SupportedLanguages = ["en-US"] },
        _time,
        NullLogger<TmdbRecommendationSource>.Instance);

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        _handler.Dispose();
    }
}
