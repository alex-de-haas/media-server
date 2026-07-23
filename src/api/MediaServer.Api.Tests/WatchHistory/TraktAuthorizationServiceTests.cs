using System.Net;
using System.Text;
using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.WatchHistory;
using MediaServer.Api.WatchHistory.Trakt;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MediaServer.Api.Tests.Jellyfin;

namespace MediaServer.Api.Tests.WatchHistory;

public sealed class TraktAuthorizationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-23T12:00:00Z"));
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly QueuedHandler _handler = new();
    private int _userId;

    public TraktAuthorizationServiceTests()
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

    /// <summary>Replays queued responses in order, recording the paths that were called.</summary>
    private sealed class QueuedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, object? Body)> _responses = new();

        public List<string> Paths { get; } = [];

        public void Enqueue(HttpStatusCode status, object? body = null) => _responses.Enqueue((status, body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            var (status, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.InternalServerError, null);
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            return Task.FromResult(response);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("https://api.trakt.tv/") };
    }

    private sealed class InMemoryCredentialStore : IWatchHistoryCredentialStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public bool Unavailable { get; set; }

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
        {
            Fail();
            return Task.FromResult(Values.GetValueOrDefault(key));
        }

        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            Fail();
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Fail();
            Values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken)
        {
            Fail();
            return Task.FromResult<IReadOnlyList<string>>([.. Values.Keys]);
        }

        private void Fail()
        {
            if (Unavailable)
            {
                throw new WatchHistoryCredentialStoreException("store offline");
            }
        }
    }

    private TraktAuthorizationService Service(bool configured = true)
    {
        var settings = new MediaServerSettings
        {
            TraktClientId = configured ? "client-id" : null,
            TraktClientSecret = configured ? "client-secret" : null,
        };
        var oauth = new TraktOAuthClient(new StubFactory(_handler), settings, _time, NullLogger<TraktOAuthClient>.Instance);
        return new TraktAuthorizationService(
            _database, oauth, _credentials, settings, _time, NullLogger<TraktAuthorizationService>.Instance);
    }

    private void EnqueueDeviceCode() => _handler.Enqueue(HttpStatusCode.OK, new
    {
        device_code = "device-abc",
        user_code = "USER1234",
        verification_url = "https://trakt.tv/activate",
        expires_in = 600,
        interval = 5,
    });

    private void EnqueueTokens(string access = "access-1", string refresh = "refresh-1", int expiresIn = 7200) =>
        _handler.Enqueue(HttpStatusCode.OK, new { access_token = access, refresh_token = refresh, expires_in = expiresIn });

    private void EnqueueAccount() => _handler.Enqueue(HttpStatusCode.OK, new
    {
        user = new { username = "alex", ids = new { slug = "alex-slug" } },
    });

    [Fact]
    public async Task WithoutOperatorConfiguration_TheProviderReportsUnconfiguredAndStartsNothing()
    {
        // Either half alone cannot complete the exchange, so a user must not be sent through a device
        // flow that can only fail at the last step.
        var service = Service(configured: false);

        Assert.False(service.IsConfigured);
        var result = await service.StartAsync(_userId, CancellationToken.None);

        Assert.Equal(WatchHistoryFailure.Unsupported, result.Failure);
        Assert.Empty(_handler.Paths);
    }

    [Fact]
    public async Task StartingStoresTheDeviceCodeAsASecretAndReturnsTheUserFacingPrompt()
    {
        EnqueueDeviceCode();

        var result = await Service().StartAsync(_userId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("USER1234", result.Value!.UserCode);
        Assert.Equal("https://trakt.tv/activate", result.Value.VerificationUrl);

        var attempt = await _database.WatchHistoryAuthorizations.AsNoTracking().SingleAsync();
        var secretKey = WatchHistoryProviderAuthorization.SecretKeyFor("trakt", attempt.Id);
        // The device code is a credential and belongs in the store, not in the row.
        Assert.Equal("device-abc", _credentials.Values[secretKey]);
        Assert.DoesNotContain("device-abc", JsonSerializer.Serialize(attempt));
    }

    [Fact]
    public async Task StartingAgainReplacesTheAttemptAndItsSecret()
    {
        EnqueueDeviceCode();
        await Service().StartAsync(_userId, CancellationToken.None);
        var first = await _database.WatchHistoryAuthorizations.AsNoTracking().SingleAsync();

        _handler.Enqueue(HttpStatusCode.OK, new
        {
            device_code = "device-xyz",
            user_code = "USER5678",
            verification_url = "https://trakt.tv/activate",
            expires_in = 600,
            interval = 5,
        });
        await Service().StartAsync(_userId, CancellationToken.None);

        var second = await _database.WatchHistoryAuthorizations.AsNoTracking().SingleAsync();
        Assert.NotEqual(first.Id, second.Id);
        // The superseded device code must not linger in the store.
        Assert.DoesNotContain(WatchHistoryProviderAuthorization.SecretKeyFor("trakt", first.Id), _credentials.Values.Keys);
        Assert.Single(_credentials.Values);
    }

    [Fact]
    public async Task ApprovalStoresCredentialsInTheStoreAndOnlyAKeyInTheDatabase()
    {
        EnqueueDeviceCode();
        await Service().StartAsync(_userId, CancellationToken.None);

        EnqueueTokens();
        EnqueueAccount();
        _time.Advance(TimeSpan.FromSeconds(10));

        var result = await Service().PollAsync(_userId, CancellationToken.None);

        Assert.Equal(WatchHistoryAuthorizationState.Approved, result.Value!.State);
        Assert.Equal("alex", result.Value.AccountName);

        var connection = await _database.WatchHistoryConnections.AsNoTracking().SingleAsync();
        Assert.Equal(WatchHistoryConnectionStatus.Connected, connection.Status);
        Assert.Equal("alex", connection.ProviderAccountName);

        // The whole point: tokens live in the store, the database holds only the key.
        var serialized = JsonSerializer.Serialize(connection);
        Assert.DoesNotContain("access-1", serialized);
        Assert.DoesNotContain("refresh-1", serialized);
        Assert.Contains("access-1", _credentials.Values[connection.SecretKey]);

        // The completed attempt and its device code are gone.
        Assert.Empty(_database.WatchHistoryAuthorizations);
        Assert.Single(_credentials.Values);
    }

    [Fact]
    public async Task PollingBeforeTheIntervalDoesNotCallTrakt()
    {
        // Polling faster than Trakt asks earns a 429; the interval is respected locally instead.
        EnqueueDeviceCode();
        await Service().StartAsync(_userId, CancellationToken.None);
        var callsAfterStart = _handler.Paths.Count;

        var result = await Service().PollAsync(_userId, CancellationToken.None);

        Assert.Equal(WatchHistoryAuthorizationState.Pending, result.Value!.State);
        Assert.Equal(callsAfterStart, _handler.Paths.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, WatchHistoryAuthorizationState.Denied)]
    [InlineData(HttpStatusCode.Gone, WatchHistoryAuthorizationState.Expired)]
    [InlineData((HttpStatusCode)418, WatchHistoryAuthorizationState.Denied)]
    public async Task TerminalDeviceFlowStatusesEndTheAttemptAndDropItsSecret(HttpStatusCode status, WatchHistoryAuthorizationState expected)
    {
        EnqueueDeviceCode();
        await Service().StartAsync(_userId, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(10));
        _handler.Enqueue(status);

        var result = await Service().PollAsync(_userId, CancellationToken.None);

        Assert.Equal(expected, result.Value!.State);
        Assert.Empty(_database.WatchHistoryAuthorizations);
        // Nothing may outlive the row that referenced it.
        Assert.Empty(_credentials.Values);
    }

    [Fact]
    public async Task APendingPollKeepsWaitingWithoutTouchingTheSecret()
    {
        EnqueueDeviceCode();
        await Service().StartAsync(_userId, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(10));
        _handler.Enqueue(HttpStatusCode.BadRequest);

        var result = await Service().PollAsync(_userId, CancellationToken.None);

        Assert.Equal(WatchHistoryAuthorizationState.Pending, result.Value!.State);
        Assert.Single(_database.WatchHistoryAuthorizations);
        Assert.Single(_credentials.Values);
    }

    [Fact]
    public async Task ASlowDownBacksOffTheNextPoll()
    {
        EnqueueDeviceCode();
        await Service().StartAsync(_userId, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(10));
        _handler.Enqueue(HttpStatusCode.TooManyRequests);

        var result = await Service().PollAsync(_userId, CancellationToken.None);

        Assert.Equal(WatchHistoryAuthorizationState.SlowDown, result.Value!.State);
        var attempt = await _database.WatchHistoryAuthorizations.AsNoTracking().SingleAsync();
        Assert.True(attempt.NextPollAt > _time.GetUtcNow());
    }

    [Fact]
    public async Task AnExpiredAttemptEndsWithoutCallingTrakt()
    {
        EnqueueDeviceCode();
        await Service().StartAsync(_userId, CancellationToken.None);
        var callsAfterStart = _handler.Paths.Count;
        _time.Advance(TimeSpan.FromMinutes(20));

        var result = await Service().PollAsync(_userId, CancellationToken.None);

        Assert.Equal(WatchHistoryAuthorizationState.Expired, result.Value!.State);
        Assert.Equal(callsAfterStart, _handler.Paths.Count);
        Assert.Empty(_credentials.Values);
    }

    [Fact]
    public async Task PollingWithNoAttemptInFlightIsRejected()
    {
        var result = await Service().PollAsync(_userId, CancellationToken.None);

        Assert.Equal(WatchHistoryFailure.IdentityRejected, result.Failure);
    }

    [Fact]
    public async Task AMissingCredentialMarksTheConnectionForReconnect()
    {
        var connection = await ConnectAsync();
        _credentials.Values.Remove(connection.SecretKey);

        var result = await Service().ReadCredentialsAsync(
            await _database.WatchHistoryConnections.SingleAsync(), CancellationToken.None);

        Assert.Null(result);
        var reloaded = await _database.WatchHistoryConnections.AsNoTracking().SingleAsync();
        Assert.Equal(WatchHistoryConnectionStatus.RequiresReconnect, reloaded.Status);
    }

    [Fact]
    public async Task ExpiringCredentialsAreRefreshedAndTheRotatedTokenIsPersisted()
    {
        // Trakt rotates the refresh token on every exchange; not storing what comes back would strand
        // the connection at the following refresh.
        var connection = await ConnectAsync();
        _time.Advance(TimeSpan.FromHours(2));
        EnqueueTokens(access: "access-2", refresh: "refresh-2");

        var fresh = await Service().ReadCredentialsAsync(
            await _database.WatchHistoryConnections.SingleAsync(), CancellationToken.None);

        Assert.Equal("access-2", fresh!.AccessToken);
        Assert.Contains("refresh-2", _credentials.Values[connection.SecretKey]);
    }

    [Fact]
    public async Task ARejectedRefreshTokenRequiresReconnect()
    {
        await ConnectAsync();
        _time.Advance(TimeSpan.FromHours(2));
        _handler.Enqueue(HttpStatusCode.Unauthorized);

        var result = await Service().ReadCredentialsAsync(
            await _database.WatchHistoryConnections.SingleAsync(), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(
            WatchHistoryConnectionStatus.RequiresReconnect,
            (await _database.WatchHistoryConnections.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task ATransientRefreshFailureLeavesTheConnectionConnected()
    {
        // A Trakt outage is not a revoked account; forcing a reconnect here would be user-hostile.
        await ConnectAsync();
        _time.Advance(TimeSpan.FromHours(2));
        _handler.Enqueue(HttpStatusCode.ServiceUnavailable);

        var result = await Service().ReadCredentialsAsync(
            await _database.WatchHistoryConnections.SingleAsync(), CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(
            WatchHistoryConnectionStatus.Connected,
            (await _database.WatchHistoryConnections.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task DisconnectRevokesRemovesTheSecretAndKeepsLocalPlaybackState()
    {
        await ConnectAsync();
        _handler.Enqueue(HttpStatusCode.OK); // revoke

        await Service().DisconnectAsync(_userId, CancellationToken.None);

        Assert.Empty(_database.WatchHistoryConnections);
        Assert.Empty(_credentials.Values);
        Assert.Contains("/oauth/revoke", _handler.Paths);
    }

    [Fact]
    public async Task DisconnectStillRemovesTheCredentialWhenRevocationFails()
    {
        // The user asked to disconnect; leaving a working token behind is the worse failure.
        await ConnectAsync();
        _handler.Enqueue(HttpStatusCode.InternalServerError);

        await Service().DisconnectAsync(_userId, CancellationToken.None);

        Assert.Empty(_database.WatchHistoryConnections);
        Assert.Empty(_credentials.Values);
    }

    [Fact]
    public async Task DisconnectDropsAnAttemptInFlightToo()
    {
        EnqueueDeviceCode();
        await Service().StartAsync(_userId, CancellationToken.None);

        await Service().DisconnectAsync(_userId, CancellationToken.None);

        Assert.Empty(_database.WatchHistoryAuthorizations);
        Assert.Empty(_credentials.Values);
    }

    private async Task<WatchHistoryProviderConnection> ConnectAsync()
    {
        EnqueueDeviceCode();
        await Service().StartAsync(_userId, CancellationToken.None);
        EnqueueTokens();
        EnqueueAccount();
        _time.Advance(TimeSpan.FromSeconds(10));
        await Service().PollAsync(_userId, CancellationToken.None);
        return await _database.WatchHistoryConnections.AsNoTracking().SingleAsync();
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
