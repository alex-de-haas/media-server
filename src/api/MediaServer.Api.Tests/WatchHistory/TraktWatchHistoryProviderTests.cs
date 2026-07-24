using System.Net;
using System.Text;
using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using MediaServer.Api.WatchHistory.Trakt;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.WatchHistory;

public sealed class TraktWatchHistoryProviderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-23T12:00:00Z"));
    private readonly StubStore _credentials = new();
    private readonly RecordingHandler _handler = new();
    private readonly int _userId;

    public TraktWatchHistoryProviderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        var user = new AppUser
        {
            HostUserId = "host-1",
            Email = "alex@example.com",
            DisplayName = "Alex",
            Role = AppUserRole.User,
            CreatedAt = _time.GetUtcNow(),
            LastSeenAt = _time.GetUtcNow(),
        };
        _database.AppUsers.Add(user);
        _database.SaveChanges();
        _userId = user.Id;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, object? Body)> _responses = new();

        public List<(string Path, string? Body)> Requests { get; } = [];

        /// <summary>Makes the next id-resolution call fail, for tests about that path.</summary>
        public bool FailNextSearch { get; set; }

        public void Enqueue(HttpStatusCode status, object? body = null) => _responses.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((
                request.RequestUri!.PathAndQuery,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            // Resolving a TMDb id to a Trakt id precedes every per-work read; answering it here keeps
            // each test expressing only the behaviour it is about.
            if (request.RequestUri!.AbsolutePath.StartsWith("/search/tmdb/", StringComparison.Ordinal))
            {
                // A test that is about the lookup failing takes the queued response instead.
                if (FailNextSearch)
                {
                    FailNextSearch = false;
                    return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        Content = new StringContent("", Encoding.UTF8, "application/json"),
                    };
                }

                var type = request.RequestUri.Query.Contains("type=movie", StringComparison.Ordinal) ? "movie" : "show";
                var payload = new StringContent(
                    $$"""[ { "{{type}}": { "ids": { "trakt": 999 } } } ]""", Encoding.UTF8, "application/json");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = payload };
            }

            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, (object?)Array.Empty<object>());
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            return response;
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("https://api.trakt.tv/") };
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

    private TraktWatchHistoryProvider Provider(bool connected = true)
    {
        if (connected)
        {
            var connection = new WatchHistoryProviderConnection
            {
                Id = Guid.NewGuid(),
                AppUserId = _userId,
                ProviderKey = "trakt",
                Status = WatchHistoryConnectionStatus.Connected,
                ConnectedAt = _time.GetUtcNow(),
            };
            connection.SecretKey = TraktAuthorizationService.ConnectionSecretKey(connection.Id);
            if (!_database.WatchHistoryConnections.Any())
            {
                _database.WatchHistoryConnections.Add(connection);
                _database.SaveChanges();
                _credentials.Values[connection.SecretKey] = JsonSerializer.Serialize(
                    new TraktCredentials("access-1", "refresh-1", _time.GetUtcNow().AddHours(5)));
            }
        }

        var settings = new MediaServerSettings { TraktClientId = "cid", TraktClientSecret = "secret" };
        var oauth = new TraktOAuthClient(new StubFactory(_handler), settings, _time, NullLogger<TraktOAuthClient>.Instance);
        var authorization = new TraktAuthorizationService(
            _database, oauth, _credentials, settings, _time, NullLogger<TraktAuthorizationService>.Instance);
        var workIds = new TraktWorkIdResolver(
            oauth, new TraktWorkIdCache(), NullLogger<TraktWorkIdResolver>.Instance);
        return new TraktWatchHistoryProvider(
            _database, oauth, authorization, NullLogger<TraktWatchHistoryProvider>.Instance, workIds);
    }

    [Fact]
    public async Task AFailedIdLookupIsReportedRatherThanReadAsAnEmptyHistory()
    {
        // The whole point of the id fix: an unaskable lookup must not become an authoritative "no
        // history", or a delivery retry would re-post a play that already exists.
        _handler.FailNextSearch = true;

        var result = await Provider().GetHistoryAsync(_userId, [Movie()], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(WatchHistoryFailure.RateLimited, result.Failure);
    }

    private static WatchHistoryIdentity Movie(int tmdb = 27205) =>
        new() { Kind = WatchHistoryMediaKind.Movie, TmdbId = tmdb };

    private static WatchHistoryIdentity Episode(int season = 1, int episode = 2, int? end = null, int tmdb = 1396) =>
        new()
        {
            Kind = WatchHistoryMediaKind.Episode,
            TmdbId = tmdb,
            SeasonNumber = season,
            EpisodeNumber = episode,
            EpisodeNumberEnd = end,
        };

    // ---- Capabilities ----

    [Fact]
    public void TheAdapterDoesNotClaimAnAggregateReadItCannotServe()
    {
        // Trakt has /sync/watched, but this adapter does not implement it. Declaring the capability
        // would make the core call something that is not there.
        var capabilities = Provider(connected: false).Capabilities;

        Assert.False(capabilities.AggregateWatchedReads);
        Assert.True(capabilities.FullHistoryReads);
        Assert.True(capabilities.IndividualEntryRemoval);
        Assert.True(capabilities.TimelessWrites);
    }

    // ---- Removal safety ----

    [Fact]
    public async Task RemovalSendsOnlyIdsSoOtherClientsPlaysSurvive()
    {
        _handler.Enqueue(HttpStatusCode.OK, new { deleted = new { episodes = 2 } });

        var result = await Provider().RemoveEntriesAsync(_userId, ["101", "102"], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Value);
        var request = _handler.Requests.Single();
        Assert.Equal("/sync/history/remove", request.Path);
        Assert.Contains("\"ids\":[101,102]", request.Body);
        // The media-object form would delete every play of the item, including exact ones and other
        // clients'. It must never appear.
        Assert.DoesNotContain("movies", request.Body);
        Assert.DoesNotContain("shows", request.Body);
    }

    [Fact]
    public async Task AnUnusableStoredIdRefusesTheWholeRemovalRatherThanBroadening()
    {
        // The dangerous failure mode is "the id looked wrong, so delete everything for that item".
        var result = await Provider().RemoveEntriesAsync(_userId, ["101", "not-an-id"], CancellationToken.None);

        Assert.Equal(WatchHistoryFailure.ContractViolation, result.Failure);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task RemovingNothingCallsNothing()
    {
        var result = await Provider().RemoveEntriesAsync(_userId, [], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Value);
        Assert.Empty(_handler.Requests);
    }

    // ---- Writes ----

    [Fact]
    public async Task ATimelessPlayUsesTraktsUnknownSentinelRatherThanAFabricatedTime()
    {
        // Inventing a timestamp would put a viewing on the user's profile at a moment nothing happened.
        _handler.Enqueue(HttpStatusCode.OK, new { added = new { movies = 1 } });

        await Provider().AddPlaysAsync(_userId, [new WatchHistoryPlay(Movie(), null)], CancellationToken.None);

        Assert.Contains("\"watched_at\":\"unknown\"", _handler.Requests.Single().Body);
    }

    [Fact]
    public async Task AnExactPlaySendsAnIsoUtcTimestamp()
    {
        _handler.Enqueue(HttpStatusCode.OK, new { added = new { movies = 1 } });
        var watchedAt = DateTimeOffset.Parse("2026-07-23T10:30:00+02:00");

        await Provider().AddPlaysAsync(_userId, [new WatchHistoryPlay(Movie(), watchedAt)], CancellationToken.None);

        var body = _handler.Requests.Single().Body;
        var sent = JsonDocument.Parse(body!).RootElement
            .GetProperty("movies")[0].GetProperty("watched_at").GetString();

        // The exact instant matters more than its spelling: 10:30+02:00 is 08:30 UTC, and Trakt must
        // receive the moment the user actually finished watching.
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-23T08:30:00Z"),
            DateTimeOffset.Parse(sent!).ToUniversalTime());
        Assert.DoesNotContain("unknown", body);
    }

    [Fact]
    public async Task AMultiEpisodeFileBecomesOneEntryPerEpisode()
    {
        // Trakt has no notion of a double episode; one local play is two remote entries.
        _handler.Enqueue(HttpStatusCode.OK, new { added = new { episodes = 2 } });

        var result = await Provider().AddPlaysAsync(
            _userId, [new WatchHistoryPlay(Episode(episode: 1, end: 2), null)], CancellationToken.None);

        Assert.Equal(2, result.Value!.Count);
        var body = _handler.Requests.Single().Body;
        Assert.Contains("\"number\":1", body);
        Assert.Contains("\"number\":2", body);
    }

    [Fact]
    public async Task EpisodesOfOneShowAreSentAsASingleGroupedPayload()
    {
        // A season mark must not become one request per episode.
        _handler.Enqueue(HttpStatusCode.OK, new { added = new { episodes = 3 } });

        await Provider().AddPlaysAsync(
            _userId,
            [
                new WatchHistoryPlay(Episode(episode: 1), null),
                new WatchHistoryPlay(Episode(episode: 2), null),
                new WatchHistoryPlay(Episode(episode: 3), null),
            ],
            CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("/sync/history", request.Path);
        // One show, one season, three episodes.
        Assert.Single(JsonDocument.Parse(request.Body!).RootElement.GetProperty("shows").EnumerateArray());
    }

    [Fact]
    public async Task UnresolvableIdentitiesAreDroppedRatherThanSentAsNonsense()
    {
        var result = await Provider().AddPlaysAsync(
            _userId,
            [new WatchHistoryPlay(new WatchHistoryIdentity { Kind = WatchHistoryMediaKind.Movie }, null)],
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!);
        Assert.Empty(_handler.Requests);
    }

    // ---- Reads ----

    [Fact]
    public async Task HistoryReadsReturnEachPlayWithItsRemoteId()
    {
        // Without the id a play can never be safely deleted later, so it is the point of the read.
        _handler.Enqueue(HttpStatusCode.OK, new[]
        {
            new { id = 9001L, watched_at = "2026-07-20T10:00:00.000Z" },
            new { id = 9002L, watched_at = "2026-07-21T10:00:00.000Z" },
        });

        var result = await Provider().GetHistoryAsync(_userId, [Movie()], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(["9001", "9002"], result.Value!.Select(play => play.RemoteId));
        Assert.All(result.Value!, play => Assert.NotNull(play.WatchedAt));
    }

    [Fact]
    public async Task AnEntryWithoutAnIdIsSkipped()
    {
        // It could never be addressed for removal, so keeping it would only invite a broad delete.
        _handler.Enqueue(HttpStatusCode.OK, new object[]
        {
            new { watched_at = "2026-07-20T10:00:00.000Z" },
            new { id = 9002L, watched_at = "2026-07-21T10:00:00.000Z" },
        });

        var result = await Provider().GetHistoryAsync(_userId, [Movie()], CancellationToken.None);

        Assert.Equal("9002", Assert.Single(result.Value!).RemoteId);
    }

    [Fact]
    public async Task AnUnknownWatchedAtComesBackAsATimelessPlay()
    {
        _handler.Enqueue(HttpStatusCode.OK, new[] { new { id = 9003L, watched_at = "unknown" } });

        var result = await Provider().GetHistoryAsync(_userId, [Movie()], CancellationToken.None);

        Assert.Null(Assert.Single(result.Value!).WatchedAt);
    }

    [Fact]
    public async Task OnlyTheEpisodesAskedAboutAreReturned()
    {
        // One show request returns the whole series' history.
        _handler.Enqueue(HttpStatusCode.OK, new[]
        {
            new { id = 1L, watched_at = "2026-07-20T10:00:00.000Z", episode = new { season = 1, number = 2 } },
            new { id = 2L, watched_at = "2026-07-20T11:00:00.000Z", episode = new { season = 1, number = 9 } },
        });

        var result = await Provider().GetHistoryAsync(_userId, [Episode(season: 1, episode: 2)], CancellationToken.None);

        var play = Assert.Single(result.Value!);
        Assert.Equal("1", play.RemoteId);
        Assert.Equal(2, play.Identity.EpisodeNumber);
    }

    [Fact]
    public async Task AFullPageIsFollowedByAnother()
    {
        _handler.Enqueue(HttpStatusCode.OK, Enumerable.Range(1, 100)
            .Select(i => new { id = (long)i, watched_at = "2026-07-20T10:00:00.000Z" }).ToArray());
        _handler.Enqueue(HttpStatusCode.OK, new[] { new { id = 101L, watched_at = "2026-07-20T10:00:00.000Z" } });

        var result = await Provider().GetHistoryAsync(_userId, [Movie()], CancellationToken.None);

        Assert.Equal(101, result.Value!.Count);
        // History pages specifically: the id-resolution call in front of them is not what this pins.
        var pages = _handler.Requests.Where(request => request.Path.Contains("sync/history")).ToList();
        Assert.Equal(2, pages.Count);
        Assert.Contains("page=2", pages[1].Path);
    }

    [Fact]
    public async Task ImdbOnlyShowsAreNotCollapsedIntoOneSeries()
    {
        // IsResolvable accepts an imdb-only identity, so grouping on TmdbId alone would post one
        // series' episodes under another series' ids.
        _handler.Enqueue(HttpStatusCode.OK, new { added = new { episodes = 2 } });

        WatchHistoryIdentity ByImdb(string imdb) => new()
        {
            Kind = WatchHistoryMediaKind.Episode,
            ImdbId = imdb,
            SeasonNumber = 1,
            EpisodeNumber = 1,
        };

        await Provider().AddPlaysAsync(
            _userId,
            [new WatchHistoryPlay(ByImdb("tt0001"), null), new WatchHistoryPlay(ByImdb("tt0002"), null)],
            CancellationToken.None);

        var shows = JsonDocument.Parse(_handler.Requests.Single().Body!).RootElement.GetProperty("shows");
        Assert.Equal(2, shows.GetArrayLength());
    }

    [Fact]
    public async Task RepeatingAnIdIsNotMistakenForAnUnusableOne()
    {
        // Distinct() used to shrink the count and trip the unparseable-id guard, refusing a perfectly
        // valid removal.
        _handler.Enqueue(HttpStatusCode.OK, new { deleted = new { movies = 1 } });

        var result = await Provider().RemoveEntriesAsync(_userId, ["101", "101"], CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains("\"ids\":[101]", _handler.Requests.Single().Body);
    }

    [Fact]
    public async Task AHistoryLongerThanThePageCeilingFailsInsteadOfTruncating()
    {
        // Reporting a partial list as success would let reconciliation conclude that the plays it
        // never saw do not exist — and act on that.
        for (var page = 0; page <= 200; page++)
        {
            _handler.Enqueue(HttpStatusCode.OK, Enumerable.Range(1, 100)
                .Select(i => new { id = (long)(page * 100 + i), watched_at = "2026-07-20T10:00:00.000Z" }).ToArray());
        }

        var result = await Provider().GetHistoryAsync(_userId, [Movie()], CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(WatchHistoryFailure.ContractViolation, result.Failure);
    }

    // ---- Credentials ----

    [Fact]
    public async Task WithoutAConnectionEveryOperationAsksForAuthenticationRatherThanCallingTrakt()
    {
        var provider = Provider(connected: false);

        Assert.Equal(
            WatchHistoryFailure.AuthenticationRequired,
            (await provider.GetHistoryAsync(_userId, [Movie()], CancellationToken.None)).Failure);
        Assert.Equal(
            WatchHistoryFailure.AuthenticationRequired,
            (await provider.AddPlaysAsync(_userId, [new WatchHistoryPlay(Movie(), null)], CancellationToken.None)).Failure);
        Assert.Equal(
            WatchHistoryFailure.AuthenticationRequired,
            (await provider.RemoveEntriesAsync(_userId, ["1"], CancellationToken.None)).Failure);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ATransientRefreshFailureStaysRetryableInsteadOfDemandingReconnect()
    {
        // A Trakt outage during refresh must not tell a worker to give up and send the user to
        // reconnect an account that was never disconnected.
        var provider = Provider();
        var connection = await _database.WatchHistoryConnections.SingleAsync();
        _credentials.Values[connection.SecretKey] = JsonSerializer.Serialize(
            new TraktCredentials("access-1", "refresh-1", _time.GetUtcNow().AddMinutes(1)));
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable); // the refresh

        var result = await provider.GetHistoryAsync(_userId, [Movie()], CancellationToken.None);

        Assert.Equal(WatchHistoryFailure.Transient, result.Failure);
        Assert.True(result.IsRetryable);
        Assert.Equal(
            WatchHistoryConnectionStatus.Connected,
            (await _database.WatchHistoryConnections.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ARateLimitedCallSurfacesAsRetryable()
    {
        _handler.Enqueue(HttpStatusCode.TooManyRequests);

        var result = await Provider().GetHistoryAsync(_userId, [Movie()], CancellationToken.None);

        Assert.Equal(WatchHistoryFailure.RateLimited, result.Failure);
        Assert.True(result.IsRetryable);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
