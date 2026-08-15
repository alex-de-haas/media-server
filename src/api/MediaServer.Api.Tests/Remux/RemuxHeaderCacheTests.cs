using MediaServer.Api.Remux;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Remux;

/// <summary>
/// What the cache has to get right, given what it is standing in for: a synthesis that reads thousands
/// of scattered places in a film and was being repeated on every byte-range request.
/// </summary>
public sealed class RemuxHeaderCacheTests
{
    /// <summary>
    /// A budget in kilobytes rather than the shipped 512 MB. Eviction is about arithmetic, not about
    /// size, and asking a parallel test run to hold gigabytes is how a suite becomes flaky.
    /// </summary>
    private static RemuxHeaderCache Cache(long budget = 512 * 1024) =>
        new(NullLogger<RemuxHeaderCache>.Instance, budget);

    private static Mp4Synthesizer.Result Header(int kilobytes) =>
        new(new byte[kilobytes * 1024], [], kilobytes * 1024L, ["hvc1"]);

    [Fact]
    public void A_header_built_once_is_handed_back()
    {
        var cache = Cache();
        var built = Header(1);

        cache.Put("k", built);

        Assert.Same(built, cache.Get("k"));
    }

    [Fact]
    public void A_key_never_seen_is_a_miss_rather_than_a_guess()
    {
        Assert.Null(Cache().Get("nothing"));
    }

    [Fact]
    public void A_different_choice_of_tracks_is_a_different_entry()
    {
        // The key is the ETag's own string, which carries the tracks and the signalling. Sharing an
        // entry across them would serve a viewer the audio somebody else picked.
        var cache = Cache();
        var english = Header(1);
        var russian = Header(1);

        cache.Put("source-0:1.0:2-hvc1", english);
        cache.Put("source-0:1.0:3-hvc1", russian);

        Assert.Same(english, cache.Get("source-0:1.0:2-hvc1"));
        Assert.Same(russian, cache.Get("source-0:1.0:3-hvc1"));
    }

    [Fact]
    public void The_least_recently_watched_is_dropped_first()
    {
        // A 512 KB budget against 200 KB headers: the third forces one out, and it must not be the one
        // just used — that would be the film someone is watching.
        var cache = Cache();
        cache.Put("a", Header(200));
        cache.Put("b", Header(200));

        _ = cache.Get("a");                     // a is now the most recently used
        cache.Put("c", Header(200));

        Assert.NotNull(cache.Get("a"));
        Assert.NotNull(cache.Get("c"));
        Assert.Null(cache.Get("b"));
    }

    [Fact]
    public void Storing_the_same_key_twice_does_not_double_count_it()
    {
        // Two range requests can race to build the same header; both then store it. Counting the bytes
        // twice would evict something for space that was never taken.
        var cache = Cache();
        cache.Put("a", Header(200));
        cache.Put("a", Header(200));
        cache.Put("b", Header(200));

        Assert.NotNull(cache.Get("a"));
        Assert.NotNull(cache.Get("b"));
    }

    [Fact]
    public void One_entry_larger_than_the_budget_is_still_served()
    {
        // Refusing to hold it would mean rebuilding it on every request, which is the thing being fixed.
        var cache = Cache();
        cache.Put("huge", Header(600));

        Assert.NotNull(cache.Get("huge"));
    }
}
