using MediaServer.Api.Recommendations;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The registry's job is to make provider resolution boring: unambiguous keys, and an optional source
/// that cannot take the feed down with it.
/// </summary>
public sealed class RecommendationProviderRegistryTests
{
    private sealed class StubProvider(string key, bool available = true, Exception? throws = null)
        : IRecommendationProvider
    {
        public string Key => key;

        public string DisplayName => key;

        public Task<bool> IsAvailableAsync(int appUserId, CancellationToken cancellationToken) =>
            throws is not null ? Task.FromException<bool>(throws) : Task.FromResult(available);

        public Task<IReadOnlyList<RecommendationCandidate>> GetAsync(
            int appUserId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecommendationCandidate>>([]);
    }

    private static RecommendationProviderRegistry Registry(params IRecommendationProvider[] providers) =>
        new(providers, NullLogger<RecommendationProviderRegistry>.Instance);

    [Fact]
    public void KeysResolveRegardlessOfCasing()
    {
        // Keys arrive from routes and stored preferences; casing must not decide whether a source exists.
        var registry = Registry(new StubProvider("library"));

        Assert.NotNull(registry.Find("Library"));
        Assert.NotNull(registry.Find("LIBRARY"));
        Assert.Null(registry.Find("trakt"));
    }

    [Fact]
    public void AnUnknownOrEmptyKeyResolvesToNothingRatherThanThrowing()
    {
        var registry = Registry(new StubProvider("library"));

        Assert.Null(registry.Find("nope"));
        Assert.Null(registry.Find(""));
        Assert.Null(registry.Find("  "));
    }

    [Fact]
    public void DuplicateKeysFailAtStartupRatherThanShadowingSilently()
    {
        // Otherwise resolution would depend on registration order — a bug that only ever shows up as
        // one provider mysteriously disappearing.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Registry(new StubProvider("library"), new StubProvider("LIBRARY")));

        Assert.Contains("library", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnEmptyOrPaddedKeyIsRejectedWhereItIsStillCheapToFix()
    {
        Assert.Throws<InvalidOperationException>(() => Registry(new StubProvider("")));
        Assert.Throws<InvalidOperationException>(() => Registry(new StubProvider(" library ")));
    }

    [Fact]
    public async Task OnlyAvailableProvidersAreOffered()
    {
        var registry = Registry(new StubProvider("library"), new StubProvider("trakt", available: false));

        var available = await registry.AvailableForAsync(1, CancellationToken.None);

        Assert.Equal("library", Assert.Single(available).Key);
    }

    [Fact]
    public async Task AProviderThatThrowsIsSkippedRatherThanTakingTheFeedDown()
    {
        // An optional upgrade going wrong must not cost the user their recommendations.
        var registry = Registry(
            new StubProvider("library"),
            new StubProvider("trakt", throws: new InvalidOperationException("boom")));

        var available = await registry.AvailableForAsync(1, CancellationToken.None);

        Assert.Equal("library", Assert.Single(available).Key);
    }

    [Fact]
    public void DescribeIsStablyOrdered()
    {
        // The UI renders these; a set-iteration order would reshuffle the source control between runs.
        var registry = Registry(new StubProvider("trakt"), new StubProvider("library"));

        Assert.Equal(["library", "trakt"], registry.Describe().Select(descriptor => descriptor.Key));
    }
}
