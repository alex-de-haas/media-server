using Microsoft.Extensions.Caching.Memory;

namespace MediaServer.Api.Hosty;

/// <summary>
/// Caches positive identity validations for a short TTL keyed by the opaque token, so a burst
/// of requests carrying the same session does not hammer Core. The cache window never outlives
/// the token's own expiry. Negative results are not cached (a token may become valid, or the
/// failure may be transient).
/// </summary>
public sealed class CachingIdentityValidator(
    IHostyIdentityValidator inner,
    IMemoryCache cache,
    TimeSpan timeToLive)
    : IHostyIdentityValidator
{
    public async Task<HostySession?> ValidateAsync(string accessToken, CancellationToken cancellationToken)
    {
        var key = CacheKey(accessToken);
        if (cache.TryGetValue<HostySession>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var session = await inner.ValidateAsync(accessToken, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var remaining = session.ExpiresAt - DateTimeOffset.UtcNow;
        var window = remaining < timeToLive ? remaining : timeToLive;
        if (window > TimeSpan.Zero)
        {
            cache.Set(key, session, window);
        }

        return session;
    }

    private static string CacheKey(string token) => $"hosty-identity:{token}";
}
