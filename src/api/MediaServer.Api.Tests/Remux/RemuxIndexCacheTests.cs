using MediaServer.Api.Remux;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Remux;

/// <summary>
/// Holding a parsed index in memory. A nine-megabyte index was read and decoded on every byte-range
/// request, which is half a second even on a fast disc — and every request for a source wants the same
/// index, so the work had no answer of its own.
/// </summary>
public sealed class RemuxIndexCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"idx-{Guid.NewGuid():N}");
    private readonly RemuxIndexStore _store;

    public RemuxIndexCacheTests()
    {
        Directory.CreateDirectory(_root);
        _store = new RemuxIndexStore(_root, NullLogger<RemuxIndexStore>.Instance);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Source(string content = "a film")
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.mkv");
        File.WriteAllText(path, content);
        return path;
    }

    private static MatroskaIndex Index() => new()
    {
        SourceLength = 6,
        Tracks =
        {
            new IndexedTrack
            {
                Number = 1, Kind = IndexedTrackKind.Video, CodecId = "V_MPEGH/ISO/HEVC",
                Samples = { new IndexedSample(0, 100, 50, true) },
            },
        },
    };

    [Fact]
    public void The_same_index_comes_back_without_being_read_again()
    {
        var source = Source();
        var id = Guid.NewGuid();
        _store.Save(id, source, Index());

        var first = _store.Load(id, source);
        var second = _store.Load(id, source);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void A_source_that_changed_gets_its_new_index_rather_than_the_one_in_memory()
    {
        // The stamp is the guard. Serving a remembered index for a re-encoded file would point every
        // sample at the wrong bytes.
        var source = Source();
        var id = Guid.NewGuid();
        _store.Save(id, source, Index());
        var before = _store.Load(id, source);
        Assert.NotNull(before);

        File.WriteAllText(source, "a different film entirely");

        Assert.Null(_store.Load(id, source));
    }

    [Fact]
    public void Saving_a_rebuilt_index_replaces_what_was_held()
    {
        var source = Source();
        var id = Guid.NewGuid();
        _store.Save(id, source, Index());
        var first = _store.Load(id, source);

        _store.Save(id, source, Index());
        var second = _store.Load(id, source);

        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Deleting_forgets_it()
    {
        var source = Source();
        var id = Guid.NewGuid();
        _store.Save(id, source, Index());
        Assert.NotNull(_store.Load(id, source));

        _store.Delete(id);

        Assert.Null(_store.Load(id, source));
    }

    [Fact]
    public void Two_sources_are_held_apart()
    {
        var first = Source("one");
        var second = Source("two");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _store.Save(a, first, Index());
        _store.Save(b, second, Index());

        Assert.NotSame(_store.Load(a, first), _store.Load(b, second));
    }
}
