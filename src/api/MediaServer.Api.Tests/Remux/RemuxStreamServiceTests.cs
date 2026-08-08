using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Remux;
using MediaServer.Api.Tests.Probe;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static MediaServer.Api.Tests.Remux.RemuxContainerBuilders;

namespace MediaServer.Api.Tests.Remux;

/// <summary>
/// The rules the served bytes are subject to. A remux URL is anonymous — the token in the query string is
/// the credential — so everything else that keeps a file from being served has to hold here as firmly as
/// it does on the direct path.
/// </summary>
public sealed class RemuxStreamServiceTests : IDisposable
{
    private static readonly byte[] Hvcc = [0x01, 0x22, 0x20, 0x00];

    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _root = Directory.CreateTempSubdirectory("remux-stream-tests").FullName;
    private readonly RemuxIndexStore _store;
    private readonly Guid _catalogId = Guid.NewGuid();

    public RemuxStreamServiceTests()
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

    private RemuxStreamService Service() => new(_database, new CatalogPathSandbox(), _store);

    private static byte[] Ac3Frame(int size) =>
        [0x0B, 0x77, 0x00, 0x00, 0x14, 0x40, 0xEB, .. new byte[Math.Max(0, size - 7)]];

    private static byte[] Film() =>
        ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_AC3", channels: 6),
                TrackEntry(3, 2, "A_AC3", channels: 2)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(20, 0x11)),
                SimpleBlock(2, 0, true, Ac3Frame(200)),
                SimpleBlock(3, 0, true, Ac3Frame(120))));

    private string Path_(string relative) => System.IO.Path.Combine(_root, "library", relative);

    private Guid Seed(bool published = true, bool removed = false, bool onDisk = true, bool indexed = true)
    {
        const string relative = "film.mkv";
        if (onDisk)
        {
            File.WriteAllBytes(Path_(relative), Film());
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
            Container = "mkv",
            Path = relative,
            SizeBytes = 1,
            DurationTicks = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _database.SaveChanges();

        if (indexed && onDisk)
        {
            using var stream = File.OpenRead(Path_(relative));
            _store.Save(sourceId, Path_(relative), MatroskaIndexer.Build(stream));
        }

        return sourceId;
    }

    private Task<(RemuxStream? Stream, RemuxRefusal Refusal)> OpenAsync(
        Guid sourceId, Guid? audio = null, Guid? subtitle = null) =>
        Service().OpenAsync(sourceId, audio, subtitle, VideoSignalling.DolbyVision, CancellationToken.None);

    [Fact]
    public async Task An_indexed_source_is_served_as_the_header_plus_the_file()
    {
        var id = Seed();

        var (stream, _) = await OpenAsync(id);

        Assert.NotNull(stream);
        using var content = stream.Content;
        Assert.Equal("video/mp4", stream.ContentType);
        Assert.Equal(new FileInfo(Path_("film.mkv")).Length + 1000, content.Length, tolerance: 1000);
        Assert.True(content.CanSeek);
    }

    [Fact]
    public async Task An_unpublished_item_is_unreachable()
    {
        var (stream, refusal) = await OpenAsync(Seed(published: false));

        Assert.Null(stream);
        Assert.Equal(RemuxRefusal.Unknown, refusal);
    }

    [Fact]
    public async Task A_tombstoned_item_is_unreachable()
    {
        var (stream, refusal) = await OpenAsync(Seed(removed: true));

        Assert.Null(stream);
        Assert.Equal(RemuxRefusal.Unknown, refusal);
    }

    [Fact]
    public async Task A_source_that_does_not_exist_is_unreachable()
    {
        var (stream, refusal) = await OpenAsync(Guid.NewGuid());

        Assert.Null(stream);
        Assert.Equal(RemuxRefusal.Unknown, refusal);
    }

    [Fact]
    public async Task A_source_whose_file_is_gone_is_unreachable()
    {
        var (stream, refusal) = await OpenAsync(Seed(onDisk: false));

        Assert.Null(stream);
        Assert.Equal(RemuxRefusal.Unknown, refusal);
    }

    [Fact]
    public async Task A_source_the_walk_has_not_reached_says_so_rather_than_saying_no()
    {
        var (stream, refusal) = await OpenAsync(Seed(indexed: false));

        Assert.Null(stream);
        // Distinct from unreachable: retrying later works, and the endpoint answers 503 rather than 404.
        Assert.Equal(RemuxRefusal.NotIndexed, refusal);
    }

    [Fact]
    public async Task An_index_built_against_an_older_file_is_not_used()
    {
        var id = Seed();
        // Same rows, different bytes: the index describes a file that no longer exists.
        File.WriteAllBytes(Path_("film.mkv"), [.. Film(), 0x00]);
        File.SetLastWriteTimeUtc(Path_("film.mkv"), DateTime.UtcNow.AddHours(1));

        var (stream, refusal) = await OpenAsync(id);

        Assert.Null(stream);
        Assert.Equal(RemuxRefusal.NotIndexed, refusal);
    }

    [Fact]
    public async Task The_tag_changes_when_the_chosen_tracks_change()
    {
        var id = Seed();
        // The second dub, which is not the one a player would take by default.
        var audio = Guid.NewGuid();
        _database.MediaStreams.Add(new MediaStream
        {
            Id = audio,
            MediaSourceId = id,
            StreamType = StreamType.Audio,
            Index = 2,
            Codec = "ac3",
        });
        _database.SaveChanges();

        var (first, _) = await OpenAsync(id);
        var (second, _) = await OpenAsync(id, audio: audio);

        Assert.NotNull(first);
        Assert.NotNull(second);
        using var a = first.Content;
        using var b = second.Content;

        // A viewer switching dub gets a different body, so a cache must be told it is a different thing.
        Assert.NotEqual(first.ETag.Tag.Value, second.ETag.Tag.Value);
    }

    [Fact]
    public async Task A_chosen_sidecar_with_no_index_of_its_own_says_not_yet()
    {
        var id = Seed();
        var dub = Guid.NewGuid();
        File.WriteAllBytes(Path_("film.rus.mka"), Film());
        _database.MediaStreams.Add(new MediaStream
        {
            Id = dub,
            MediaSourceId = id,
            StreamType = StreamType.Audio,
            Index = 99,
            Codec = "ac3",
            IsExternal = true,
            ExternalPath = "film.rus.mka",
        });
        _database.SaveChanges();

        var (stream, refusal) = await OpenAsync(id, audio: dub);

        Assert.Null(stream);
        Assert.Equal(RemuxRefusal.NotIndexed, refusal);
    }

    [Fact]
    public async Task A_chosen_sidecar_that_has_been_walked_is_carried()
    {
        var id = Seed();
        var dub = Guid.NewGuid();
        var dubPath = Path_("film.rus.mka");
        File.WriteAllBytes(dubPath, ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B, TrackEntry(1, 2, "A_AC3", channels: 6)),
            Cluster(0, SimpleBlock(1, 0, true, Ac3Frame(300)))));

        using (var stream = File.OpenRead(dubPath))
        {
            _store.Save(dub, dubPath, MatroskaIndexer.Build(stream));
        }

        _database.MediaStreams.Add(new MediaStream
        {
            Id = dub,
            MediaSourceId = id,
            StreamType = StreamType.Audio,
            Index = 99,
            Codec = "ac3",
            IsExternal = true,
            ExternalPath = "film.rus.mka",
        });
        _database.SaveChanges();

        var (served, _) = await OpenAsync(id, audio: dub);

        Assert.NotNull(served);
        using var content = served.Content;

        // Both files are in the output, plus the header and the wrapper between them.
        var expected = new FileInfo(Path_("film.mkv")).Length + new FileInfo(dubPath).Length;
        Assert.True(content.Length > expected, "the sidecar's bytes are part of what is served");
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
