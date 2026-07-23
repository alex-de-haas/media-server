using MediaServer.Api.WatchHistory;

namespace MediaServer.Api.Tests.WatchHistory;

public sealed class WatchHistoryIdentityTests
{
    private static WatchHistoryIdentity Episode(int? season = 1, int? episode = 2, int? end = null, int? tmdb = 42) => new()
    {
        Kind = WatchHistoryMediaKind.Episode,
        TmdbId = tmdb,
        SeasonNumber = season,
        EpisodeNumber = episode,
        EpisodeNumberEnd = end,
    };

    [Fact]
    public void AMovieIsResolvableFromEitherExternalId()
    {
        Assert.True(new WatchHistoryIdentity { Kind = WatchHistoryMediaKind.Movie, TmdbId = 27205 }.IsResolvable);
        Assert.True(new WatchHistoryIdentity { Kind = WatchHistoryMediaKind.Movie, ImdbId = "tt1375666" }.IsResolvable);
        Assert.False(new WatchHistoryIdentity { Kind = WatchHistoryMediaKind.Movie }.IsResolvable);
    }

    [Fact]
    public void AnEpisodeNeedsCoordinatesAsWellAsAnId()
    {
        Assert.True(Episode().IsResolvable);
        Assert.False(Episode(season: null).IsResolvable);
        Assert.False(Episode(episode: null).IsResolvable);
        Assert.False(Episode(tmdb: null).IsResolvable);
    }

    [Fact]
    public void ARangeExpandsToOneIdentityPerEpisode()
    {
        // Providers have no notion of a double episode, so one local play becomes two remote entries.
        var expanded = Episode(episode: 1, end: 2).Expand().ToList();

        Assert.Equal([1, 2], expanded.Select(identity => identity.EpisodeNumber));
        Assert.All(expanded, identity => Assert.Null(identity.EpisodeNumberEnd));
        Assert.All(expanded, identity => Assert.Equal(1, identity.SeasonNumber));
    }

    [Fact]
    public void AnOrdinaryIdentityExpandsToItself()
    {
        Assert.Single(Episode().Expand());
        Assert.Single(new WatchHistoryIdentity { Kind = WatchHistoryMediaKind.Movie, TmdbId = 1 }.Expand());
    }

    [Theory]
    [InlineData(2, 2)]   // end == start: not a range
    [InlineData(2, 1)]   // end < start: malformed
    public void ADegenerateRangeExpandsToItself(int first, int end)
    {
        var expanded = Assert.Single(Episode(episode: first, end: end).Expand());
        Assert.Equal(first, expanded.EpisodeNumber);
    }
}

public sealed class WatchHistoryResultTests
{
    [Fact]
    public void SuccessCarriesTheValueAndNoFailure()
    {
        var result = WatchHistoryResult<int>.Success(3);

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Value);
        Assert.Null(result.Failure);
        Assert.False(result.IsRetryable);
    }

    [Theory]
    [InlineData(WatchHistoryFailure.Transient, true)]
    [InlineData(WatchHistoryFailure.RateLimited, true)]
    [InlineData(WatchHistoryFailure.AuthenticationRequired, false)]
    [InlineData(WatchHistoryFailure.Unsupported, false)]
    [InlineData(WatchHistoryFailure.IdentityRejected, false)]
    [InlineData(WatchHistoryFailure.ContractViolation, false)]
    public void OnlyTransportLevelFailuresAreWorthRetrying(WatchHistoryFailure failure, bool retryable)
    {
        // A worker schedules on this: retrying an identity the provider rejected only burns rate limit,
        // and retrying an expired credential can never succeed without the user reconnecting.
        Assert.Equal(retryable, WatchHistoryResult<int>.Failed(failure).IsRetryable);
    }

    [Fact]
    public void AFailureCanCarryTheProvidersRequestedDelay()
    {
        var result = WatchHistoryResult<int>.Failed(
            WatchHistoryFailure.RateLimited, "429", TimeSpan.FromSeconds(30));

        Assert.False(result.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
        Assert.Equal("429", result.Detail);
    }
}

public sealed class WatchHistoryProviderRegistryTests
{
    private sealed class StubProvider(string key, string name = "Stub") : IWatchHistoryProvider
    {
        public string Key => key;

        public string DisplayName => name;

        public WatchHistoryCapabilities Capabilities => new()
        {
            ExactTimestampWrites = true,
            TimelessWrites = true,
            AggregateWatchedReads = true,
            FullHistoryReads = true,
            IndividualEntryRemoval = true,
        };

        public Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> GetHistoryAsync(
            int appUserId, IReadOnlyCollection<WatchHistoryIdentity> identities, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> AddPlaysAsync(
            int appUserId, IReadOnlyCollection<WatchHistoryPlay> plays, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WatchHistoryResult<int>> RemoveEntriesAsync(
            int appUserId, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubAuthorization(string providerKey, bool configured = true) : IWatchHistoryProviderAuthorization
    {
        public string ProviderKey => providerKey;

        public bool IsConfigured => configured;

        public Task<WatchHistoryResult<WatchHistoryAuthorizationPrompt>> StartAsync(int appUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WatchHistoryResult<WatchHistoryAuthorizationOutcome>> PollAsync(int appUserId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DisconnectAsync(int appUserId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static WatchHistoryProviderRegistry Registry(
        IEnumerable<IWatchHistoryProvider>? providers = null,
        IEnumerable<IWatchHistoryProviderAuthorization>? authorizations = null) =>
        new(providers ?? [new StubProvider("trakt")], authorizations ?? [new StubAuthorization("trakt")]);

    [Fact]
    public void ResolvesByKeyRegardlessOfCasing()
    {
        // Keys arrive from routes and stored rows; both must reach the same adapter.
        var registry = Registry();

        Assert.NotNull(registry.Find("trakt"));
        Assert.NotNull(registry.Find("TRAKT"));
        Assert.NotNull(registry.Find(" trakt "));
        Assert.NotNull(registry.FindAuthorization("Trakt"));
    }

    [Theory]
    [InlineData("simkl")]
    [InlineData("")]
    [InlineData("   ")]
    public void ReturnsNullForAnUnknownOrEmptyKey(string key)
    {
        Assert.Null(Registry().Find(key));
        Assert.Null(Registry().FindAuthorization(key));
    }

    [Fact]
    public void DescribesRegisteredProvidersForSettings()
    {
        var descriptor = Assert.Single(Registry().Describe());

        Assert.Equal("trakt", descriptor.Key);
        Assert.Equal("Stub", descriptor.DisplayName);
        Assert.True(descriptor.IsConfigured);
        Assert.True(descriptor.Capabilities.FullHistoryReads);
    }

    [Fact]
    public void AProviderWithoutAConfiguredAuthorizationIsDescribedAsUnconfigured()
    {
        // Settings shows "needs operator configuration" from this, so it must not claim readiness.
        var registry = Registry(authorizations: [new StubAuthorization("trakt", configured: false)]);

        Assert.False(Assert.Single(registry.Describe()).IsConfigured);
    }

    [Fact]
    public void AProviderWithNoAuthorizationAtAllIsDescribedAsUnconfigured()
    {
        var registry = Registry(authorizations: []);

        Assert.False(Assert.Single(registry.Describe()).IsConfigured);
        Assert.Null(registry.FindAuthorization("trakt"));
    }

    [Fact]
    public void DescribesProvidersInAStableOrder()
    {
        var registry = Registry(
            providers: [new StubProvider("simkl"), new StubProvider("trakt")],
            authorizations: []);

        Assert.Equal(["simkl", "trakt"], registry.Describe().Select(descriptor => descriptor.Key));
    }

    [Fact]
    public void DuplicateKeysFailAtStartupRatherThanShadowingSilently()
    {
        // Otherwise resolution would depend on DI registration order, and one adapter would quietly win.
        var error = Assert.Throws<InvalidOperationException>(() =>
            Registry(providers: [new StubProvider("trakt"), new StubProvider("TRAKT")], authorizations: []));

        Assert.Contains("trakt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyProviderKeyFailsAtStartup()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Registry(providers: [new StubProvider("  ")], authorizations: []));
    }
}
