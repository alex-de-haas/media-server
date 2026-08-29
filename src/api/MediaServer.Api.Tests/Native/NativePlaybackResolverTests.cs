using MediaServer.Api.Data;
using MediaServer.Api.Native;
using MediaServer.Api.Native.Playback;
using MediaServer.Api.Remux;
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

    /// <summary>
    /// Stands in for the background walk: whether a source has an index yet is the only thing that
    /// decides between "remux" and "not yet", and these tests set it rather than build one.
    /// </summary>
    private sealed class Readiness(RemuxReadinessState state) : IRemuxReadiness
    {
        public Task<IReadOnlyDictionary<Guid, RemuxReadinessState>> ReadyAsync(
            IReadOnlyList<Guid> mediaSourceIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, RemuxReadinessState>>(
                mediaSourceIds.ToDictionary(id => id, _ => state));
    }

    private NativePlaybackResolver Resolver(bool packaging = false) =>
        Resolver(packaging ? RemuxReadinessState.Ready : RemuxReadinessState.Unsupported);

    private NativePlaybackResolver Resolver(RemuxReadinessState readiness) =>
        new(_context,
            new NativeUrlTokenService(new NativeUrlSigningKey(new byte[32]), new FixedTime()),
            new Readiness(readiness),
            new NativePreferenceService(_context));

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
        var response = await Resolver(packaging).ResolveAsync(_itemId, UserId, profile, null, null, false, CancellationToken.None);
        Assert.NotNull(response);
        return Assert.Single(response!.Sources);
    }

    [Fact]
    public async Task A_remux_url_carries_the_very_tracks_the_answer_reports()
    {
        // The picker ticks what the answer names, so if the URL and the answer could disagree a viewer
        // would be told they are hearing a track the packager was never asked for.
        AddSource("mkv", "hevc", "HDR10", ("ac3", 6), ("eac3", 6));
        var second = _context.MediaStreams
            .Single(stream => stream.StreamType == StreamType.Audio && stream.Codec == "eac3").Id;

        var response = await Resolver(packaging: true).ResolveAsync(
            _itemId, UserId, AppleTv(), second, null, false, CancellationToken.None);

        var resolution = Assert.Single(response!.Sources);
        Assert.Equal(NativePlaybackDecision.Remux, resolution.Decision);
        Assert.Equal(second, resolution.AudioStreamId);
        Assert.Contains($"audioStreamId={second:D}", resolution.Url);
        Assert.DoesNotContain("subtitleStreamId", resolution.Url);
    }

    [Fact]
    public async Task Direct_play_reports_no_tracks_because_the_choice_was_never_ours()
    {
        AddSource("mp4", "hevc", "HDR10", ("eac3", 6));

        var resolution = await ResolveOneAsync(AppleTv());

        Assert.Equal(NativePlaybackDecision.DirectPlay, resolution.Decision);
        Assert.Null(resolution.AudioStreamId);
        Assert.Null(resolution.SubtitleStreamId);
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
        // breaks one that does not, so the answer depends on what the client said. An mkv, because the
        // choice only exists where we build the container.
        AddSource("mkv", "hevc", "Dolby Vision", ("ac3", 6));

        // Remux is where the choice exists, because that is where we write the container.
        var withDv = await ResolveOneAsync(AppleTv(dolbyVision: true), packaging: true);
        Assert.Equal(NativeSignalling.DolbyVision, withDv.Signalling);

        var withoutDv = await ResolveOneAsync(AppleTv(dolbyVision: false), packaging: true);
        Assert.Equal(NativeSignalling.CrossCompatible, withoutDv.Signalling);
    }

    [Fact]
    public async Task Direct_play_promises_no_signalling_because_it_serves_the_file_as_written()
    {
        // The file goes out byte for byte, so its sample entry is whatever is on disk. Advertising a
        // choice here would be a promise nothing keeps: a client without Dolby Vision could still be
        // handed a dvh1 file while the response claimed otherwise.
        AddSource("mp4", "hevc", "Dolby Vision", ("eac3", 6));

        var resolution = await ResolveOneAsync(AppleTv(dolbyVision: false));

        Assert.Equal(NativePlaybackDecision.DirectPlay, resolution.Decision);
        Assert.Null(resolution.Signalling);
        Assert.Equal("Dolby Vision", resolution.SourceDynamicRange);
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
    public async Task A_source_whose_audio_cannot_be_packaged_is_refused_rather_than_played_silently()
    {
        // FLAC is the live case now that AAC is described: the anime extras in this library carry nothing
        // else. A client that decodes it gets past its own check, and packaging still cannot write the
        // sample entry — so offering a remux would hand over a playable-looking file with no sound.
        AddSource("mkv", "hevc", "HDR10", ("flac", 2));

        var resolution = await ResolveOneAsync(
            AppleTv() with { AudioCodecs = ["ac3", "eac3", "aac", "flac"] }, packaging: true);

        Assert.Equal(NativePlaybackDecision.Unsupported, resolution.Decision);
        Assert.Equal(NativePlaybackReasons.PackagingUnsupportedAudio, resolution.Reason);
    }

    [Fact]
    public async Task An_aac_only_source_is_offered_now_that_it_can_be_described()
    {
        // 64 episodes of this library carry AAC and nothing else a client could take. Before this they
        // were indexed and still refused.
        AddSource("mkv", "hevc", "SDR", ("aac", 2));

        Assert.Equal(
            NativePlaybackDecision.Remux,
            (await ResolveOneAsync(AppleTv(), packaging: true)).Decision);
    }

    [Fact]
    public async Task A_source_whose_picture_cannot_be_packaged_is_refused_rather_than_promised()
    {
        // A recent Apple TV decodes AV1, so the client-side check passes and nothing else here would stop
        // it. Packaging has no sample entry for AV1, so the URL would have been handed over and then
        // failed on the request that used it.
        AddSource("mkv", "av1", "HDR10", ("ac3", 6));

        var resolution = await ResolveOneAsync(
            AppleTv() with { VideoCodecs = ["hevc", "h264", "av1"] }, packaging: true);

        Assert.Equal(NativePlaybackDecision.Unsupported, resolution.Decision);
        Assert.Equal(NativePlaybackReasons.PackagingUnsupportedVideo, resolution.Reason);
    }

    [Fact]
    public async Task A_permanent_packaging_refusal_beats_saying_the_walk_has_not_got_there_yet()
    {
        // The index for this source has not been built. Reporting "not yet" would be true and useless:
        // when the walk does reach it, the picture will be exactly as undescribable as it is now.
        AddSource("mkv", "av1", "HDR10", ("ac3", 6));

        var response = await Resolver(RemuxReadinessState.Pending).ResolveAsync(
            _itemId, UserId, AppleTv() with { VideoCodecs = ["hevc", "h264", "av1"] },
            null, null, false, CancellationToken.None);

        Assert.Equal(
            NativePlaybackReasons.PackagingUnsupportedVideo, Assert.Single(response!.Sources).Reason);
    }

    [Fact]
    public async Task An_eac3_only_source_is_offered_now_that_it_can_be_described()
    {
        // Atmos rides on E-AC-3, so a source carrying only it is exactly the case worth having.
        AddSource("mkv", "hevc", "HDR10", ("eac3", 6));

        Assert.Equal(
            NativePlaybackDecision.Remux,
            (await ResolveOneAsync(AppleTv(), packaging: true)).Decision);
    }

    [Fact]
    public async Task A_dts_only_source_is_still_refused_rather_than_played_silently()
    {
        AddSource("mkv", "hevc", "HDR10", ("dts", 8));

        var resolution = await ResolveOneAsync(AppleTv(), packaging: true);

        Assert.Equal(NativePlaybackDecision.Unsupported, resolution.Decision);
        // DTS is out of scope for this client, and the client says so first.
        Assert.Equal(NativePlaybackReasons.UnsupportedAudioCodec, resolution.Reason);
    }

    [Fact]
    public async Task One_packageable_audio_track_among_several_is_enough_to_remux()
    {
        AddSource("mkv", "hevc", "HDR10", ("dts", 8), ("ac3", 6));

        Assert.Equal(
            NativePlaybackDecision.Remux,
            (await ResolveOneAsync(AppleTv(), packaging: true)).Decision);
    }

    [Fact]
    public async Task A_source_the_walk_has_not_reached_says_so_rather_than_saying_no()
    {
        AddSource("mkv", "hevc", "HDR10", ("ac3", 6));

        var response = await Resolver(RemuxReadinessState.Pending)
            .ResolveAsync(_itemId, UserId, AppleTv(), null, null, false, CancellationToken.None);
        var resolution = Assert.Single(response!.Sources);

        // Waiting helps here, and a client that knows the difference shows "preparing" instead of
        // "unavailable" — and retries.
        Assert.Equal(NativePlaybackDecision.Unsupported, resolution.Decision);
        Assert.Equal(NativePlaybackReasons.PackagingPending, resolution.Reason);
    }

    [Fact]
    public async Task A_remux_url_carries_the_transport_and_the_signalling_it_promises()
    {
        AddSource("mkv", "hevc", "Dolby Vision", ("ac3", 6));

        var resolution = await ResolveOneAsync(AppleTv(dolbyVision: true), packaging: true);

        Assert.Equal(NativePlaybackDecision.Remux, resolution.Decision);
        Assert.Equal(NativePlaybackTransport.ByteRange, resolution.Transport);
        Assert.NotNull(resolution.Url);
        Assert.Contains("/remux?token=", resolution.Url);
        // What the URL asks for and what the response promises must be the same thing.
        Assert.Contains($"signalling={resolution.Signalling}", resolution.Url);
    }

    [Fact]
    public async Task An_mkv_is_a_packaging_problem_and_says_which_kind()
    {
        AddSource("mkv", "hevc", "HDR10", ("ac3", 6));

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

        Assert.Null(await Resolver().ResolveAsync(_itemId, UserId, AppleTv(), null, null, false, CancellationToken.None));
    }
}
