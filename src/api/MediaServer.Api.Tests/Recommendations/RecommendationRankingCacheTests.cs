using MediaServer.Api.Recommendations;
using MediaServer.Api.Tests.Jellyfin;

namespace MediaServer.Api.Tests.Recommendations;

public sealed class RecommendationRankingCacheTests : IDisposable
{
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-09-27T12:00:00Z"));
    private readonly RecommendationRankingCache _cache;
    private RecommendationRankingStamp _stamp;
    private int _builds;

    public RecommendationRankingCacheTests() => _cache = new RecommendationRankingCache(_time);

    [Fact]
    public async Task GetAsync_UnchangedInputs_ReusesEvenAnEmptyResult()
    {
        var first = await Get();
        Assert.Same(first, await Get());
        Assert.Empty(first.Candidates);
        Assert.Equal(1, _builds);
    }

    [Fact]
    public async Task GetAsync_Expiry_IsAbsoluteAndStartsAfterTheBuild()
    {
        var first = await Get(build: _ =>
        {
            _time.Advance(TimeSpan.FromMinutes(10));
            return Build();
        });
        _time.Advance(RecommendationRankingCache.Lifetime - TimeSpan.FromSeconds(1));
        Assert.Same(first, await Get());
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotSame(first, await Get());
        Assert.Equal(2, _builds);
    }

    [Fact]
    public async Task GetAsync_DifferentUsersAndLimits_DoNotShareResults()
    {
        var first = await Get();
        Assert.NotSame(first, await Get(user: 2));
        Assert.NotSame(first, await Get(limit: 240));
        Assert.Same(first, await Get());
        Assert.Equal(3, _builds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetAsync_ChangedStampOrPreference_Rebuilds(bool preference)
    {
        var first = await Get();
        _stamp = preference
            ? _stamp with { PopularityBias = 1.5 }
            : _stamp with { Profile = _stamp.Profile with { StateRevisions = 1 } };

        var second = await Get();
        Assert.NotSame(first, second);
        Assert.Same(second, await Get());
        Assert.Equal(2, _builds);
    }

    [Fact]
    public async Task GetAsync_ConcurrentReaders_BuildOnce()
    {
        var started = Signal();
        var release = Signal();
        var first = Get(build: async ct =>
        {
            started.SetResult();
            await release.Task.WaitAsync(ct);
            return await Build();
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var others = Enumerable.Range(0, 12).Select(_ => Get()).ToArray();
        Assert.All(others, task => Assert.False(task.IsCompleted));

        release.SetResult();
        var results = await Task.WhenAll(others.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(1, _builds);
    }

    [Fact]
    public async Task GetAsync_CancelledWaiter_DoesNotCancelTheBuilder()
    {
        var release = Signal();
        var first = Get(build: async ct =>
        {
            await release.Task.WaitAsync(ct);
            return await Build();
        });
        using var cancellation = new CancellationTokenSource();
        var waiter = Get(token: cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(first.IsCompleted);

        release.SetResult();
        Assert.Same(await first.WaitAsync(TimeSpan.FromSeconds(10)), await Get());
        Assert.Equal(1, _builds);
    }

    [Fact]
    public async Task GetAsync_CancelledBuilder_AllowsAWaitingRequestToRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var first = Get(build: async ct =>
        {
            await Signal().Task.WaitAsync(ct);
            return await Build();
        }, token: cancellation.Token);
        var waiter = Get();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var result = await waiter.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(result, await Get());
        Assert.Equal(1, _builds);
    }

    [Fact]
    public async Task GetAsync_FailedBuild_IsNotCachedAndReleasesTheGate()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Get(
            build: _ => throw new InvalidOperationException("Failed to rank")));
        var recovered = await Get();
        Assert.Same(recovered, await Get());
        Assert.Equal(1, _builds);
    }

    [Fact]
    public async Task GetAsync_CancellationIgnoredByBuilder_DoesNotPublishTheResult()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Get(build: _ =>
        {
            cancellation.Cancel();
            return Build();
        }, token: cancellation.Token));

        await Get();
        Assert.Equal(2, _builds);
    }

    [Fact]
    public async Task GetAsync_InputsChangeDuringBuild_WaiterRebuildsAgainstNewInputs()
    {
        var release = Signal();
        var first = Get(build: async ct =>
        {
            await release.Task.WaitAsync(ct);
            return await Build();
        });
        var waiter = Get();
        _stamp = _stamp with { PopularityBias = 2 };
        release.SetResult();

        var before = await first.WaitAsync(TimeSpan.FromSeconds(10));
        var after = await waiter.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotSame(before, after);
        Assert.Same(after, await Get());
        Assert.Equal(2, _builds);
    }

    [Fact]
    public async Task GetAsync_FailedStampRead_DoesNotPoisonTheKey()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _cache.GetAsync(1, 50,
            _ => throw new InvalidOperationException("Database unavailable"), _ => Build(), CancellationToken.None));
        await Get();
        Assert.Equal(1, _builds);
    }

    [Fact]
    public async Task GetAsync_CapacityExceeded_EvictsTheLeastRecentlyUsedResult()
    {
        var oldest = await Get(user: 1);
        var second = await Get(user: 2);
        for (var user = 3; user <= RecommendationRankingCache.Capacity; user++)
            await Get(user: user);

        Assert.Same(oldest, await Get(user: 1));
        await Get(user: RecommendationRankingCache.Capacity + 1);

        Assert.Same(oldest, await Get(user: 1));
        Assert.NotSame(second, await Get(user: 2));
        Assert.Equal(RecommendationRankingCache.Capacity + 2, _builds);
    }

    private Task<EngineResult> Get(int user = 1, int limit = 50,
        Func<CancellationToken, Task<EngineResult>>? build = null, CancellationToken token = default) =>
        _cache.GetAsync(user, limit, _ => Task.FromResult(_stamp), build ?? (_ => Build()), token);

    private Task<EngineResult> Build()
    {
        _builds++;
        return Task.FromResult(new EngineResult([], RecommendationRung.History));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose() => _cache.Dispose();
}
