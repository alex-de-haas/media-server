using MediaServer.Api.Recommendations.Profile;

namespace MediaServer.Api.Recommendations;

/// <summary>
/// Short-lived ranked candidates shared by ordinary feed requests. Library state is projected live.
/// </summary>
public sealed class RecommendationRankingCache(TimeProvider time) : IDisposable
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    internal const int Capacity = 128;

    private readonly object _sync = new();
    private readonly Dictionary<Key, Entry> _entries = new();
    // Stripes keep synchronization bounded even as users and requested limits change. Colliding
    // keys may wait for each other, but never share results or a request's scoped DbContext.
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private long _access;

    internal async Task<EngineResult> GetAsync(
        int appUserId,
        int limit,
        Func<CancellationToken, Task<RecommendationRankingStamp>> readStamp,
        Func<CancellationToken, Task<EngineResult>> build,
        CancellationToken cancellationToken)
    {
        var key = new Key(appUserId, limit);
        var gate = _gates[(int)((uint)key.GetHashCode() % (uint)_gates.Length)];
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Read after waiting: another request may have built while this caller was queued,
            // and user input may have changed in the meantime. Each delegate uses this caller's scope.
            var stamp = await readStamp(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var cached))
                {
                    if (cached.Stamp == stamp && time.GetUtcNow() < cached.ExpiresAt)
                    {
                        _entries[key] = cached with { Access = ++_access };
                        return cached.Result;
                    }

                    _entries.Remove(key);
                }
            }

            var result = await build(cancellationToken);
            // A build spanning a write is useful to its caller, but must not become a cached answer
            // labelled with inputs it did not use. Failed and cancelled builds never reach storage.
            var after = await readStamp(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (after == stamp)
            {
                lock (_sync)
                {
                    var now = time.GetUtcNow();
                    foreach (var expired in _entries.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
                        _entries.Remove(expired);

                    if (_entries.Count >= Capacity)
                        _entries.Remove(_entries.MinBy(pair => pair.Value.Access).Key);

                    _entries[key] = new Entry(stamp, result, now + Lifetime, ++_access);
                }
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var gate in _gates)
            gate.Dispose();
    }

    private readonly record struct Key(int AppUserId, int Limit);
    private sealed record Entry(RecommendationRankingStamp Stamp, EngineResult Result, DateTimeOffset ExpiresAt, long Access);
}

internal readonly record struct RecommendationRankingStamp(ProfileStamp Profile, double PopularityBias);
