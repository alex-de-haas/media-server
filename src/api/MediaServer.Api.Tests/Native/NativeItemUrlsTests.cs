using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Native;
using MediaServer.Api.Tests.Jellyfin;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// The URLs a native client is handed for one title: one per edition, plus the sidecar files that
/// belong to it. Built from the real detail projection rather than a hand-assembled DTO, so the test
/// breaks if the projection stops carrying what the URLs are built from.
/// </summary>
public sealed class NativeItemUrlsTests : IDisposable
{
    private const int UserId = 3;

    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly Guid _itemId = Guid.NewGuid();
    private readonly Guid _sourceId = Guid.NewGuid();

    public NativeItemUrlsTests()
    {
        _context = _db.Create();
        Seed();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);
    }

    private static NativeUrlTokenService Tokens() => new(new NativeUrlSigningKey(new byte[32]), new FixedTime());

    private LibraryReadService Library() =>
        new(_context,
            new UserDataService(_context, TimeProvider.System),
            new MediaServerSettings { SupportedLanguages = ["en-US"] });

    private void Seed()
    {
        var catalogId = Guid.NewGuid();
        _context.Catalogs.Add(new Catalog
        {
            Id = catalogId,
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = "/srv/movies",
        });
        _context.AppUsers.Add(new AppUser { Id = UserId, HostUserId = "host-3", DisplayName = "Alex" });
        _context.MediaItems.Add(new MediaItem
        {
            Id = _itemId,
            CatalogId = catalogId,
            Kind = MediaKind.Movie,
            Title = "Film",
            PublicId = Guid.NewGuid().ToString("N"),
        });
        _context.MediaSources.Add(new MediaSource
        {
            Id = _sourceId,
            MediaItemId = _itemId,
            VersionName = "Remux",
            Path = "Film.mkv",
            Container = "mkv",
            SizeBytes = 1,
            DurationTicks = 1,
        });
        _context.SaveChanges();
    }

    private Guid AddStream(bool external, StreamType type, string? externalPath)
    {
        var id = Guid.NewGuid();
        _context.MediaStreams.Add(new MediaStream
        {
            Id = id,
            MediaSourceId = _sourceId,
            StreamType = type,
            Index = 0,
            Language = "rus",
            IsExternal = external,
            ExternalPath = externalPath,
        });
        _context.SaveChanges();
        return id;
    }

    private async Task<List<NativeSourceUrlsDto>> BuildAsync()
    {
        var detail = await Library().GetDetailAsync(_itemId, UserId, CancellationToken.None);
        Assert.NotNull(detail);
        return NativeItemUrls.Build(detail!, UserId, Tokens());
    }

    [Fact]
    public async Task Gives_each_edition_a_stream_url_carrying_a_token_for_that_source()
    {
        var source = Assert.Single(await BuildAsync());

        Assert.Equal(_sourceId, source.MediaSourceId);
        Assert.Equal("Remux", source.VersionName);
        Assert.StartsWith($"/native/v1/media/{_sourceId:D}?token=", source.StreamUrl);

        // The URL has to actually work, and only for that source.
        var token = source.StreamUrl.Split("token=")[1];
        Assert.True(Tokens().Validate(token, _sourceId, "GET").IsValid);
        Assert.False(Tokens().Validate(token, Guid.NewGuid(), "GET").IsValid);
    }

    [Fact]
    public async Task Offers_sidecar_tracks_and_skips_embedded_ones()
    {
        AddStream(external: false, StreamType.Audio, externalPath: null);
        var external = AddStream(external: true, StreamType.Subtitle, "Film.rus.srt");

        var track = Assert.Single(Assert.Single(await BuildAsync()).Tracks);

        Assert.Equal(external, track.StreamId);
        Assert.Contains($"/tracks/{external:D}?token=", track.Url);
    }

    [Fact]
    public async Task Covers_a_source_and_its_sidecars_with_one_token()
    {
        // One playback reads two files when a viewer picks an external dub; two credentials would only
        // create two things that can expire separately.
        AddStream(external: true, StreamType.Audio, "Film.rus.mka");

        var source = Assert.Single(await BuildAsync());
        var fromStream = source.StreamUrl.Split("token=")[1];
        var fromTrack = Assert.Single(source.Tracks).Url.Split("token=")[1];

        Assert.Equal(fromStream, fromTrack);
    }
}
