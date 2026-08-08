using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Remux;
using MediaServer.Api.Tests.Probe;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static MediaServer.Api.Tests.Remux.RemuxContainerBuilders;

namespace MediaServer.Api.Tests.Remux;

public sealed class RemuxIndexServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _root = Directory.CreateTempSubdirectory("remux-service-tests").FullName;
    private readonly RemuxIndexStore _store;
    private readonly Guid _catalogId = Guid.NewGuid();

    public RemuxIndexServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        Directory.CreateDirectory(Path.Combine(_root, "library"));
        _database.Catalogs.Add(new Catalog
        {
            Id = _catalogId,
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = Path.Combine(_root, "library"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _database.SaveChanges();

        _store = new RemuxIndexStore(_root, NullLogger<RemuxIndexStore>.Instance);
    }

    private RemuxIndexService Service() =>
        new(_database, new CatalogPathSandbox(), _store, NullLogger<RemuxIndexService>.Instance);

    /// <summary>A one-track Matroska file that the indexer can actually walk.</summary>
    private static byte[] TinyMatroska() =>
        ContainerBuilders.Matroska(
            ContainerBuilders.Info(1000),
            ContainerBuilders.Ebml(0x1654AE6B, TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", width: 8, height: 8)),
            Cluster(0, SimpleBlock(1, 0, keyframe: true, Frame(16, 0xAB))));

    private Guid SeedSource(
        string relativePath = "film.mkv",
        string container = "mkv",
        bool published = true,
        bool removed = false,
        bool onDisk = true,
        byte[]? content = null)
    {
        if (onDisk)
        {
            var absolute = Path.Combine(_root, "library", relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllBytes(absolute, content ?? TinyMatroska());
        }

        var itemId = Guid.NewGuid();
        _database.MediaItems.Add(new MediaItem
        {
            Id = itemId,
            PublicId = published ? Guid.NewGuid().ToString("N") : null,
            CatalogId = _catalogId,
            RemovedAt = removed ? DateTimeOffset.UtcNow : null,
            Kind = MediaKind.Movie,
            Title = "Film",
        });

        var sourceId = Guid.NewGuid();
        _database.MediaSources.Add(new MediaSource
        {
            Id = sourceId,
            MediaItemId = itemId,
            Container = container,
            Path = relativePath,
            SizeBytes = 1,
            DurationTicks = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _database.SaveChanges();
        return sourceId;
    }

    private string AbsolutePath(string relativePath = "film.mkv") =>
        Path.Combine(_root, "library", relativePath);

    [Fact]
    public async Task A_matroska_source_without_an_index_is_pending()
    {
        var id = SeedSource();

        var pending = await Service().PendingAsync(10, CancellationToken.None);

        Assert.Equal(id, Assert.Single(pending).MediaSourceId);
    }

    [Fact]
    public async Task An_mp4_source_is_not_pending_because_it_has_nothing_to_gain()
    {
        SeedSource("film.mp4", container: "mp4");

        Assert.Empty(await Service().PendingAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task An_unpublished_item_is_not_pending()
    {
        SeedSource(published: false);

        Assert.Empty(await Service().PendingAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_tombstoned_item_is_not_pending()
    {
        SeedSource(removed: true);

        Assert.Empty(await Service().PendingAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_source_whose_file_is_gone_is_not_pending()
    {
        SeedSource(onDisk: false);

        Assert.Empty(await Service().PendingAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_source_that_already_has_a_current_index_is_not_pending()
    {
        var service = Service();
        SeedSource();
        var candidate = Assert.Single(await service.PendingAsync(10, CancellationToken.None));

        Assert.True(await service.BuildAsync(candidate, CancellationToken.None));

        Assert.Empty(await service.PendingAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task A_source_whose_file_changed_becomes_pending_again()
    {
        var service = Service();
        SeedSource();
        var candidate = Assert.Single(await service.PendingAsync(10, CancellationToken.None));
        await service.BuildAsync(candidate, CancellationToken.None);

        await File.WriteAllBytesAsync(
            AbsolutePath(), [.. TinyMatroska(), 0x00], CancellationToken.None);

        Assert.Single(await service.PendingAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task Pending_stops_at_the_limit()
    {
        SeedSource("a.mkv");
        SeedSource("b.mkv");
        SeedSource("c.mkv");

        Assert.Equal(2, (await Service().PendingAsync(2, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Building_stores_something_the_store_will_hand_back()
    {
        var service = Service();
        var id = SeedSource();
        var candidate = Assert.Single(await service.PendingAsync(10, CancellationToken.None));

        Assert.True(await service.BuildAsync(candidate, CancellationToken.None));

        var index = _store.Load(id, AbsolutePath());
        Assert.NotNull(index);
        var track = Assert.Single(index.Tracks);
        Assert.Equal("V_MPEGH/ISO/HEVC", track.CodecId);
        Assert.Single(track.Samples);
    }

    [Fact]
    public async Task A_file_with_no_tracks_is_not_indexed()
    {
        var service = Service();
        var id = SeedSource(content: [0x00, 0x01, 0x02, 0x03]);
        var candidate = Assert.Single(await service.PendingAsync(10, CancellationToken.None));

        Assert.False(await service.BuildAsync(candidate, CancellationToken.None));

        Assert.Null(_store.Load(id, AbsolutePath()));
    }

    [Fact]
    public async Task Building_a_file_that_vanished_is_not_an_error()
    {
        var service = Service();
        SeedSource();
        var candidate = Assert.Single(await service.PendingAsync(10, CancellationToken.None));
        File.Delete(AbsolutePath());

        Assert.False(await service.BuildAsync(candidate, CancellationToken.None));
    }

    [Fact]
    public async Task Pruning_removes_indexes_whose_source_is_gone_and_keeps_the_rest()
    {
        var service = Service();
        var live = SeedSource();
        var candidate = Assert.Single(await service.PendingAsync(10, CancellationToken.None));
        await service.BuildAsync(candidate, CancellationToken.None);

        // An index left behind by a title deleted while the server was down.
        var orphan = Guid.NewGuid();
        _store.Save(orphan, AbsolutePath(), new MatroskaIndex { SourceLength = 1 });

        Assert.Equal(1, await service.PruneAsync(CancellationToken.None));

        Assert.Contains(live, _store.Stored());
        Assert.DoesNotContain(orphan, _store.Stored());
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
