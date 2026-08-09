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

    private Guid Seed(
        bool published = true, bool removed = false, bool onDisk = true, bool indexed = true,
        byte[]? content = null)
    {
        const string relative = "film.mkv";
        if (onDisk)
        {
            File.WriteAllBytes(Path_(relative), content ?? Film());
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

    [Fact]
    public async Task The_tag_changes_when_a_subtitle_beside_the_video_is_edited()
    {
        var id = Seed();
        var subtitle = Guid.NewGuid();
        var path = Path_("film.eng.srt");
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nBefore\n");
        _database.MediaStreams.Add(new MediaStream
        {
            Id = subtitle,
            MediaSourceId = id,
            StreamType = StreamType.Subtitle,
            Index = 98,
            Codec = "subrip",
            IsExternal = true,
            ExternalPath = "film.eng.srt",
        });
        _database.SaveChanges();

        var (first, _) = await OpenAsync(id, subtitle: subtitle);
        Assert.NotNull(first);
        using (first.Content)
        {
        }

        // Same length, different words, and a later timestamp: the body changes and a conditional
        // request must not be told nothing did.
        File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nAfterx\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(1));

        var (second, _) = await OpenAsync(id, subtitle: subtitle);
        Assert.NotNull(second);
        using (second.Content)
        {
        }

        Assert.NotEqual(first.ETag.Tag.Value, second.ETag.Tag.Value);
        // And the answer is as fresh as the freshest thing in it, not merely as the video.
        Assert.True(second.LastModified > first.LastModified);
    }

    [Fact]
    public async Task The_tag_changes_when_a_dub_is_replaced_by_one_of_the_same_length()
    {
        var id = Seed();
        var dub = Guid.NewGuid();
        var dubPath = Path_("film.rus.mka");
        var dubFile = ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B, TrackEntry(1, 2, "A_AC3", channels: 6)),
            Cluster(0, SimpleBlock(1, 0, true, Ac3Frame(300))));

        void WriteDub()
        {
            File.WriteAllBytes(dubPath, dubFile);
            using var stream = File.OpenRead(dubPath);
            _store.Save(dub, dubPath, MatroskaIndexer.Build(stream));
        }

        WriteDub();
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

        var (first, _) = await OpenAsync(id, audio: dub);
        Assert.NotNull(first);
        using (first.Content)
        {
        }

        // A different dub that happens to be the same size — nothing about the source changed, and the
        // length alone would not tell the two apart.
        WriteDub();
        File.SetLastWriteTimeUtc(dubPath, DateTime.UtcNow.AddHours(1));
        using (var stream = File.OpenRead(dubPath))
        {
            _store.Save(dub, dubPath, MatroskaIndexer.Build(stream));
        }

        var (second, _) = await OpenAsync(id, audio: dub);
        Assert.NotNull(second);
        using (second.Content)
        {
        }

        Assert.NotEqual(first.ETag.Tag.Value, second.ETag.Tag.Value);
    }

    [Fact]
    public async Task A_picture_that_cannot_be_described_is_refused_rather_than_served_without_it()
    {
        // AV1 with AC-3 beside it. The resolver asks whether the *client* can decode the picture, and a
        // recent Apple TV can; this asks whether we can *write* its sample entry, and we cannot. Serving
        // what is left would hand back a film that is nothing but its soundtrack.
        var id = Seed(content: ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_AV1", codecPrivate: [0x81, 0x00], width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_AC3", channels: 6)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(20, 0x11)),
                SimpleBlock(2, 0, true, Ac3Frame(200)))));

        var (stream, refusal) = await OpenAsync(id);

        Assert.Null(stream);
        Assert.Equal(RemuxRefusal.NotPackageable, refusal);
    }

    [Fact]
    public async Task A_soundtrack_that_cannot_be_described_is_refused_rather_than_served_silently()
    {
        // The mirror of the picture rule: HEVC we can write, FLAC we cannot, and no dub beside the file to
        // take its place. Serving what is left would be a film with no sound.
        var id = Seed(content: ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_FLAC", channels: 2)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(20, 0x11)),
                SimpleBlock(2, 0, true, Frame(300, 0x22)))));

        var (stream, refusal) = await OpenAsync(id);

        Assert.Null(stream);
        Assert.Equal(RemuxRefusal.NotPackageable, refusal);
    }

    [Fact]
    public async Task An_aac_soundtrack_is_served_now_that_it_can_be_described()
    {
        // The case the whole anime half of the library turns on: video plus AAC and nothing else.
        var id = Seed(content: ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_AAC", codecPrivate: [0x11, 0x90], channels: 2)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(20, 0x11)),
                SimpleBlock(2, 0, true, Frame(300, 0x22)))));

        var (stream, _) = await OpenAsync(id);

        Assert.NotNull(stream);
        await stream.Content.DisposeAsync();
    }

    [Fact]
    public async Task An_aac_config_the_descriptor_would_decline_is_refused_before_anything_is_served()
    {
        // Explicitly signalled SBR: a config is present, so a check for mere presence would pass it, and
        // the track would be walked, chosen, and then dropped by the synthesiser — a picture and no
        // sound. The packageability question and the descriptor ask the same thing, so it is refused
        // here instead.
        var id = Seed(content: ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_AAC", codecPrivate: [0x29, 0x90], channels: 2)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(20, 0x11)),
                SimpleBlock(2, 0, true, Frame(300, 0x22)))));

        var (stream, refusal) = await OpenAsync(id);

        Assert.Null(stream);
        Assert.Equal(RemuxRefusal.NotPackageable, refusal);
    }

    [Fact]
    public async Task A_source_with_no_picture_at_all_is_not_caught_by_that_rule()
    {
        // An audio-only Matroska has nothing to describe wrongly. The refusal above is for a source that
        // has a picture we cannot write, not for one that never had a picture.
        var id = Seed(content: ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B, TrackEntry(2, 2, "A_AC3", channels: 6)),
            Cluster(0, SimpleBlock(2, 0, true, Ac3Frame(200)))));

        var (stream, _) = await OpenAsync(id);

        Assert.NotNull(stream);
        await stream.Content.DisposeAsync();
    }
}
