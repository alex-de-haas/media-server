using MediaServer.Api.Data;
using MediaServer.Api.Native;
using MediaServer.Api.Native.Playback;
using MediaServer.Api.Tests.Jellyfin;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// What a given client is told it can play. The Apple TV spike is the reason this exists at all: the
/// same file has to be offered differently to a client that engages Dolby Vision and one that does
/// not, and a source nothing can play must say why rather than fail silently.
/// </summary>
public sealed class NativePlaybackResolverTests : IDisposable
{
    private const int UserId = 4;

    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly Guid _itemId = Guid.NewGuid();
    private readonly Guid _sourceId = Guid.NewGuid();

    public NativePlaybackResolverTests()
    {
        _context = _db.Create();

        var catalogId = Guid.NewGuid();
        _context.Catalogs.Add(new Catalog
        {
            Id = catalogId, Name = "Movies", Type = CatalogType.Movie, Root = "/tmp/none",
        });
        _context.MediaItems.Add(new MediaItem
        {
            Id = _itemId,
            CatalogId = catalogId,
            Kind = MediaKind.Movie,
            Title = "Film",
            PublicId = Guid.NewGuid().ToString("N"),
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    }

    private NativePlaybackResolver Resolver(bool packaging = false) =>
        new(_context,
            new NativeUrlTokenService(new NativeUrlSigningKey(new byte[32]), new FixedTime()),
            new NativePackagingAvailability { IsAvailable = packaging });

    private void AddSource(string container, string videoCodec, string? hdr, params (string Codec, int Channels)[] audio)
    {
        _context.MediaSources.Add(new MediaSource
        {
            Id = _sourceId,
            MediaItemId = _itemId,
            Path = $"Film.{container}",
            Container = container,
            SizeBytes = 1,
            DurationTicks = 1,
        });
        _context.MediaStreams.Add(new MediaStream
        {
            Id = Guid.NewGuid(), MediaSourceId = _sourceId, StreamType = StreamType.Video,
            Index = 0, Codec = videoCodec, HdrFormat = hdr,
        });
        var index = 1;
        foreach (var track in audio)
        {
            _context.MediaStreams.Add(new MediaStream
            {
                Id = Guid.NewGuid(), MediaSourceId = _sourceId, StreamType = StreamType.Audio,
                Index = index++, Codec = track.Codec, Channels = track.Channels,
            });
        }

        _context.SaveChanges();
    }

    private static NativeCapabilityProfile AppleTv(bool dolbyVision = true) => new(
        Containers: ["mp4", "m4v", "mov"],
        VideoCodecs: ["hevc", "h264"],
        AudioCodecs: ["ac3", "eac3", "aac"],
        HdrFormats: dolbyVision ? ["SDR", "HDR10", "Dolby Vision"] : ["SDR", "HDR10"]);

    private async Task<NativePlaybackResolution> ResolveOneAsync(
        NativeCapabilityProfile profile, bool packaging = false)
    {
        var response = await Resolver(packaging).ResolveAsync(_itemId, UserId, profile, CancellationToken.None);
        Assert.NotNull(response);
        return Assert.Single(response!.Sources);
    }

    [Fact]
    public async Task An_mp4_a_client_can_open_is_direct_play_with_a_usable_url()
    {
        AddSource("mp4", "hevc", "HDR10", ("eac3", 6));

        var resolution = await ResolveOneAsync(AppleTv());

        Assert.Equal(NativePlaybackDecision.DirectPlay, resolution.Decision);
        Assert.Contains($"/native/v1/media/{_sourceId:D}?token=", resolution.Url);
        Assert.Null(resolution.Reason);
    }

    [Fact]
    public async Task A_dolby_vision_source_is_offered_as_dolby_vision_only_to_a_client_that_has_it()
    {
        // The ordering constraint the spike produced: dvh1 engages DV on a device that supports it and
        // breaks one that does not, so the answer depends on what the client said.
        AddSource("mp4", "hevc", "Dolby Vision", ("eac3", 6));

        var withDv = await ResolveOneAsync(AppleTv(dolbyVision: true));
        Assert.Equal(NativeSignalling.DolbyVision, withDv.Signalling);

        var withoutDv = await ResolveOneAsync(AppleTv(dolbyVision: false));
        Assert.Equal(NativeSignalling.CrossCompatible, withoutDv.Signalling);
        Assert.Equal(NativePlaybackDecision.DirectPlay, withoutDv.Decision);
    }

    [Fact]
    public async Task A_client_with_no_hdr_at_all_is_not_offered_an_hdr_source()
    {
        AddSource("mp4", "hevc", "Dolby Vision", ("eac3", 6));

        var resolution = await ResolveOneAsync(AppleTv() with { HdrFormats = ["SDR"] });

        Assert.Equal(NativePlaybackDecision.Unsupported, resolution.Decision);
        Assert.Equal(NativePlaybackReasons.UnsupportedDynamicRange, resolution.Reason);
    }

    [Fact]
    public async Task A_dts_only_source_says_so_rather_than_failing_silently()
    {
        AddSource("mkv", "hevc", "HDR10", ("dts", 6));

        var resolution = await ResolveOneAsync(AppleTv());

        Assert.Equal(NativePlaybackDecision.Unsupported, resolution.Decision);
        Assert.Equal(NativePlaybackReasons.UnsupportedAudioCodec, resolution.Reason);
    }

    [Fact]
    public async Task One_playable_audio_track_among_several_is_enough()
    {
        AddSource("mp4", "hevc", "HDR10", ("dts", 8), ("ac3", 6));

        Assert.Equal(NativePlaybackDecision.DirectPlay, (await ResolveOneAsync(AppleTv())).Decision);
    }

    [Fact]
    public async Task An_mkv_is_a_packaging_problem_and_says_which_kind()
    {
        AddSource("mkv", "hevc", "HDR10", ("eac3", 6));

        // Codecs are fine; only the container is not. Until packaging exists the honest answer is that
        // it is unavailable, not a URL that would fail to open.
        var without = await ResolveOneAsync(AppleTv());
        Assert.Equal(NativePlaybackDecision.Unsupported, without.Decision);
        Assert.Equal(NativePlaybackReasons.PackagingUnavailable, without.Reason);

        var with = await ResolveOneAsync(AppleTv(), packaging: true);
        Assert.Equal(NativePlaybackDecision.Remux, with.Decision);
        Assert.Null(with.Reason);
    }

    [Fact]
    public async Task An_undecodable_picture_is_the_end_of_it()
    {
        AddSource("mp4", "av1", "SDR", ("aac", 2));

        var resolution = await ResolveOneAsync(AppleTv());

        Assert.Equal(NativePlaybackReasons.UnsupportedVideoCodec, resolution.Reason);
    }

    [Fact]
    public async Task A_channel_ceiling_is_honoured()
    {
        AddSource("mp4", "hevc", "SDR", ("eac3", 8));

        var resolution = await ResolveOneAsync(AppleTv() with { MaxAudioChannels = 2 });

        Assert.Equal(NativePlaybackReasons.UnsupportedAudioCodec, resolution.Reason);
    }

    [Fact]
    public async Task A_profile_carrying_blank_entries_is_answered_rather_than_thrown_at()
    {
        // The profile is request input: a client can send [null] or [""], and a malformed body must
        // not become a 500.
        AddSource("mp4", "hevc", "SDR", ("aac", 2));

        var ragged = new NativeCapabilityProfile(
            Containers: ["", "mp4"],
            VideoCodecs: [" ", "hevc"],
            AudioCodecs: ["aac", ""],
            HdrFormats: ["SDR", "  "]);

        Assert.Equal(NativePlaybackDecision.DirectPlay, (await ResolveOneAsync(ragged)).Decision);
    }

    [Fact]
    public async Task An_unpublished_item_resolves_to_nothing()
    {
        AddSource("mp4", "hevc", "SDR", ("aac", 2));
        var item = _context.MediaItems.Single(candidate => candidate.Id == _itemId);
        item.PublicId = null;
        item.RemovedAt = DateTimeOffset.UtcNow;
        _context.SaveChanges();

        Assert.Null(await Resolver().ResolveAsync(_itemId, UserId, AppleTv(), CancellationToken.None));
    }
}
