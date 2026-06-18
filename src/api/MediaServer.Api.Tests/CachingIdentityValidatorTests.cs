using Imposter.Abstractions;
using MediaServer.Api.Hosty;
using Microsoft.Extensions.Caching.Memory;

[assembly: GenerateImposter(typeof(IHostyIdentityValidator))]

namespace MediaServer.Api.Tests;

public sealed class CachingIdentityValidatorTests
{
    private static HostySession Session(string userId) =>
        new("com.haas.media-server", userId, $"{userId}@example.com", userId.ToUpperInvariant(), "host.user", DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task Serves_repeated_calls_for_the_same_token_from_cache()
    {
        // The inner validator would return a different session on a second call; if the caching
        // layer hit it twice we'd observe "second". Observing "first" both times proves the cache.
        var imposter = IHostyIdentityValidator.Imposter();
        imposter.ValidateAsync(Arg<string>.Any(), Arg<CancellationToken>.Any())
            .Returns(Task.FromResult(Session("first")))
            .Then()
            .Returns(Task.FromResult(Session("second")));

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new CachingIdentityValidator(imposter.Instance(), cache, TimeSpan.FromMinutes(5));

        var first = await sut.ValidateAsync("token-a", CancellationToken.None);
        var second = await sut.ValidateAsync("token-a", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("first", first!.UserId);
        Assert.Equal("first", second!.UserId);
    }

    [Fact]
    public async Task Validates_distinct_tokens_independently()
    {
        var imposter = IHostyIdentityValidator.Imposter();
        imposter.ValidateAsync(Arg<string>.Any(), Arg<CancellationToken>.Any())
            .Returns(Task.FromResult(Session("first")))
            .Then()
            .Returns(Task.FromResult(Session("second")));

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sut = new CachingIdentityValidator(imposter.Instance(), cache, TimeSpan.FromMinutes(5));

        var first = await sut.ValidateAsync("token-a", CancellationToken.None);
        var second = await sut.ValidateAsync("token-b", CancellationToken.None);

        Assert.Equal("first", first!.UserId);
        Assert.Equal("second", second!.UserId);
    }
}
