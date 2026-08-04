using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Native;
using MediaServer.Api.Tests.Jellyfin;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// What a signed URL is actually allowed to reach. The token proves who is asking; these rules decide
/// what exists to be asked for.
/// </summary>
public sealed class NativeMediaResolverTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly string _root;

    private Guid _catalogId;
    private Guid _itemId;
    private Guid _sourceId;

    public NativeMediaResolverTests()
    {
        _context = _db.Create();
        _root = Path.Combine(Path.GetTempPath(), "ms-native-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Seed();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private NativeMediaResolver Resolver() => new(_context, new CatalogPathSandbox());

    private void Seed()
    {
        _catalogId = Guid.NewGuid();
        _itemId = Guid.NewGuid();
        _sourceId = Guid.NewGuid();

        File.WriteAllBytes(Path.Combine(_root, "Film.mkv"), new byte[64]);
        File.WriteAllText(Path.Combine(_root, "Film.rus.srt"), "1\n");

        _context.Catalogs.Add(new Catalog
        {
            Id = _catalogId,
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = _root,
        });
        _context.MediaItems.Add(new MediaItem
        {
            Id = _itemId,
            CatalogId = _catalogId,
            Kind = MediaKind.Movie,
            Title = "Film",
            PublicId = Guid.NewGuid().ToString("N"),
        });
        _context.MediaSources.Add(new MediaSource
        {
            Id = _sourceId,
            MediaItemId = _itemId,
            Path = "Film.mkv",
            Container = "mkv",
            SizeBytes = 64,
            DurationTicks = 1,
        });
        _context.SaveChanges();
    }

    private Guid AddStream(bool external, string? path)
    {
        var id = Guid.NewGuid();
        _context.MediaStreams.Add(new MediaStream
        {
            Id = id,
            MediaSourceId = _sourceId,
            StreamType = external ? StreamType.Subtitle : StreamType.Audio,
            Index = 1,
            IsExternal = external,
            ExternalPath = path,
        });
        _context.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Serves_a_published_items_source()
    {
        var resolved = await Resolver().ResolveSourceAsync(_sourceId, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(Path.Combine(_root, "Film.mkv"), resolved!.AbsolutePath);
    }

    [Fact]
    public async Task Refuses_a_source_whose_item_was_tombstoned()
    {
        // A signed URL minted while the title was visible must stop working when it stops being
        // published — otherwise the token outlives the decision to remove it.
        var item = _context.MediaItems.Single(media => media.Id == _itemId);
        item.PublicId = null;
        item.RemovedAt = DateTimeOffset.UtcNow;
        _context.SaveChanges();

        Assert.Null(await Resolver().ResolveSourceAsync(_sourceId, CancellationToken.None));
    }

    [Fact]
    public async Task Serves_a_sidecar_track()
    {
        var streamId = AddStream(external: true, "Film.rus.srt");

        var resolved = await Resolver().ResolveSidecarAsync(_sourceId, streamId, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("application/x-subrip", resolved!.ContentType);
    }

    [Fact]
    public async Task Refuses_an_embedded_track_which_has_no_file_of_its_own()
    {
        var streamId = AddStream(external: false, path: null);

        Assert.Null(await Resolver().ResolveSidecarAsync(_sourceId, streamId, CancellationToken.None));
    }

    [Fact]
    public async Task Refuses_a_track_belonging_to_a_different_source()
    {
        var streamId = AddStream(external: true, "Film.rus.srt");

        Assert.Null(await Resolver().ResolveSidecarAsync(Guid.NewGuid(), streamId, CancellationToken.None));
    }

    [Fact]
    public async Task Refuses_a_path_that_climbs_out_of_the_catalog_root()
    {
        // The row is the attacker here: whatever put it there, the sandbox is what stops it resolving.
        var outside = Path.Combine(Path.GetTempPath(), "ms-native-outside-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "secret");
        try
        {
            var streamId = AddStream(external: true, "../" + Path.GetFileName(outside));

            Assert.Null(await Resolver().ResolveSidecarAsync(_sourceId, streamId, CancellationToken.None));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
