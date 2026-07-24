using System.Net;
using System.Text;
using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Recommendations.Trakt;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using MediaServer.Api.WatchHistory.Trakt;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The Trakt adapter: an optional upgrade that must never take the feed down, and must never emit a
/// candidate nothing else can recognize.
/// </summary>
public sealed class TraktRecommendationProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-25T12:00:00Z"));
    private readonly StubStore _credentials = new();
    private readonly RecordingHandler _handler = new();
    private readonly int _userId;

    public TraktRecommendationProviderTests()
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
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public List<string> Requests { get; } = [];

        public void Enqueue(HttpStatusCode status, string body) => _responses.Enqueue((status, body));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, "[]");
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.trakt.tv/") };
    }

    private sealed class StubStore : IWatchHistoryCredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([.. Values.Keys]);
    }

    [Fact]
    public async Task WithoutAConnectionTheSourceIsUnavailableAndSilent()
    {
        Assert.False(await Provider(connected: false).IsAvailableAsync(_userId, CancellationToken.None));
        Assert.Empty(await Provider(connected: false).GetAsync(_userId, 10, CancellationToken.None));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task AConnectionAwaitingReconnectIsUnavailableRatherThanAlwaysEmpty()
    {
        // "Not offered" is a cleaner story for the UI than a source that is present and never works.
        Connect(WatchHistoryConnectionStatus.RequiresReconnect);

        Assert.False(await Provider().IsAvailableAsync(_userId, CancellationToken.None));
    }

    [Fact]
    public async Task AHealthyConnectionYieldsCandidatesFromBothKinds()
    {
        Connect();
        _handler.Enqueue(HttpStatusCode.OK, """
            [ { "title": "Inception", "year": 2010, "ids": { "trakt": 1, "tmdb": 27205 } } ]
            """);
        _handler.Enqueue(HttpStatusCode.OK, """
            [ { "title": "Severance", "year": 2022, "ids": { "trakt": 2, "tmdb": 95396 } } ]
            """);

        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, entry => entry.Identity == new RecommendationIdentity(RecommendationKind.Movie, "27205"));
        Assert.Contains(result, entry => entry.Identity == new RecommendationIdentity(RecommendationKind.Series, "95396"));
        Assert.Contains("recommendations/movies", _handler.Requests[0], StringComparison.Ordinal);
        Assert.Contains("recommendations/shows", _handler.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoviesAndShowsAreInterleavedRatherThanConcatenated()
    {
        // Trakt ranks each kind on its own, so appending would bury every show below every movie for
        // a reason neither list implies.
        Connect();
        _handler.Enqueue(HttpStatusCode.OK, """
            [ { "title": "M1", "ids": { "tmdb": 1 } }, { "title": "M2", "ids": { "tmdb": 2 } } ]
            """);
        _handler.Enqueue(HttpStatusCode.OK, """
            [ { "title": "S1", "ids": { "tmdb": 3 } }, { "title": "S2", "ids": { "tmdb": 4 } } ]
            """);

        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Equal(
            [RecommendationKind.Movie, RecommendationKind.Series, RecommendationKind.Movie, RecommendationKind.Series],
            result.Select(entry => entry.Identity.Kind));
        Assert.Equal([0, 1, 2, 3], result.Select(entry => entry.Rank));
    }

    [Fact]
    public async Task WrappedAndBareTitleShapesAreBothAccepted()
    {
        // Trakt has returned both over time; only accepting one would silently empty the feed.
        Connect();
        _handler.Enqueue(HttpStatusCode.OK, """
            [ { "movie": { "title": "Wrapped", "year": 1999, "ids": { "tmdb": 603 } } } ]
            """);
        _handler.Enqueue(HttpStatusCode.OK, "[]");

        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Equal("Wrapped", Assert.Single(result).Title);
    }

    [Fact]
    public async Task TitlesWithoutATmdbIdAreDropped()
    {
        // Nothing downstream could merge or match them: they would render as a card no other part of
        // the app recognizes.
        Connect();
        _handler.Enqueue(HttpStatusCode.OK, """
            [ { "title": "Trakt only", "ids": { "trakt": 7, "slug": "trakt-only" } },
              { "title": "Usable", "ids": { "tmdb": 8 } } ]
            """);
        _handler.Enqueue(HttpStatusCode.OK, "[]");

        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Equal("Usable", Assert.Single(result).Title);
    }

    [Fact]
    public async Task AFailedTraktCallYieldsAnEmptyListRatherThanAnError()
    {
        // This source is an upgrade over the built-in engine, never a dependency of it.
        Connect();
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable, "");
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable, "");

        Assert.Empty(await Provider().GetAsync(_userId, 10, CancellationToken.None));
    }

    [Fact]
    public async Task OneKindFailingStillReturnsTheOther()
    {
        Connect();
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable, "");
        _handler.Enqueue(HttpStatusCode.OK, """[ { "title": "S1", "ids": { "tmdb": 3 } } ]""");

        var result = await Provider().GetAsync(_userId, 10, CancellationToken.None);

        Assert.Equal(RecommendationKind.Series, Assert.Single(result).Identity.Kind);
    }

    [Fact]
    public async Task TheLimitIsHonouredAcrossBothKinds()
    {
        Connect();
        _handler.Enqueue(HttpStatusCode.OK,
            JsonSerializer.Serialize(Enumerable.Range(1, 5).Select(id => new { title = $"M{id}", ids = new { tmdb = id } })));
        _handler.Enqueue(HttpStatusCode.OK,
            JsonSerializer.Serialize(Enumerable.Range(10, 5).Select(id => new { title = $"S{id}", ids = new { tmdb = id } })));

        Assert.Equal(3, (await Provider().GetAsync(_userId, 3, CancellationToken.None)).Count);
    }

    private void Connect(WatchHistoryConnectionStatus status = WatchHistoryConnectionStatus.Connected)
    {
        var connection = new WatchHistoryProviderConnection
        {
            Id = Guid.NewGuid(), AppUserId = _userId, ProviderKey = "trakt",
            Status = status, ConnectedAt = _time.GetUtcNow(),
        };
        connection.SecretKey = TraktAuthorizationService.ConnectionSecretKey(connection.Id);
        _database.WatchHistoryConnections.Add(connection);
        _database.SaveChanges();
        _credentials.Values[connection.SecretKey] = JsonSerializer.Serialize(
            new TraktCredentials("access-1", "refresh-1", _time.GetUtcNow().AddHours(5)));
    }

    private TraktRecommendationProvider Provider(bool connected = true)
    {
        if (connected && !_database.WatchHistoryConnections.Any())
        {
            Connect();
        }

        var settings = new MediaServerSettings { TraktClientId = "cid", TraktClientSecret = "secret" };
        var oauth = new TraktOAuthClient(
            new StubFactory(_handler), settings, _time, NullLogger<TraktOAuthClient>.Instance);
        var authorization = new TraktAuthorizationService(
            _database, oauth, _credentials, settings, _time, NullLogger<TraktAuthorizationService>.Instance);

        return new TraktRecommendationProvider(
            _database, authorization, oauth, NullLogger<TraktRecommendationProvider>.Instance);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        _handler.Dispose();
    }
}
