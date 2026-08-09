using MediaServer.Api.Remux;
using MediaServer.Api.Tests.Probe;
using static MediaServer.Api.Tests.Remux.RemuxContainerBuilders;

namespace MediaServer.Api.Tests.Remux;

public sealed class MatroskaIndexerTests
{
    private static MatroskaIndex Index(byte[] file) =>
        MatroskaIndexer.Build(new MemoryStream(file));

    private static byte[] File(byte[] tracks, params byte[][] clusters) =>
        ContainerBuilders.Matroska(ContainerBuilders.Info(30_000), [tracks, .. clusters]);

    private static byte[] Tracks(params byte[][] entries) => ContainerBuilders.Ebml(0x1654AE6B, entries);

    [Fact]
    public void Carries_what_a_sample_entry_needs_rather_than_deriving_it()
    {
        var hvcc = new byte[] { 0x01, 0x22, 0x20, 0x00 };
        var dv = new byte[] { 0x01, 0x00, 0x10, 0x35, 0x10 };

        var index = Index(File(Tracks(TrackEntry(
            number: 1, type: 1, codec: "V_MPEGH/ISO/HEVC",
            codecPrivate: hvcc, dolbyVision: dv,
            width: 3840, height: 2160, defaultDuration: 41_666_666,
            primaries: 9, transfer: 16, matrix: 9, range: 1))));

        var track = Assert.Single(index.Tracks);
        Assert.Equal(IndexedTrackKind.Video, track.Kind);
        Assert.Equal("V_MPEGH/ISO/HEVC", track.CodecId);
        Assert.Equal(hvcc, track.CodecPrivate);
        Assert.Equal(dv, track.DolbyVisionConfiguration);
        Assert.Equal(3840, track.Width);
        Assert.Equal(2160, track.Height);
        Assert.Equal(41_666_666, track.DefaultDuration);
        Assert.Equal(9, track.ColourPrimaries);
        Assert.Equal(16, track.TransferCharacteristics);
        Assert.Equal(9, track.MatrixCoefficients);
        Assert.False(track.FullRange);      // Range 1 is broadcast, not full
    }

    [Fact]
    public void A_track_without_a_dolby_vision_mapping_reports_none()
    {
        var index = Index(File(Tracks(TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", width: 1920, height: 1080))));

        Assert.Null(Assert.Single(index.Tracks).DolbyVisionConfiguration);
    }

    [Fact]
    public void Records_where_each_sample_lives_and_when_it_is_shown()
    {
        var file = File(
            Tracks(TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: [0x01], width: 8, height: 8)),
            Cluster(1000,
                SimpleBlock(1, 0, keyframe: true, Frame(40, 0xAA)),
                SimpleBlock(1, 42, keyframe: false, Frame(17, 0xBB))));

        var samples = Assert.Single(Index(file).Tracks).Samples;
        Assert.Equal(2, samples.Count);

        Assert.Equal(1000, samples[0].Timestamp);
        Assert.Equal(40, samples[0].Size);
        Assert.True(samples[0].IsKeyframe);

        // A block's timestamp is relative to its cluster's.
        Assert.Equal(1042, samples[1].Timestamp);
        Assert.Equal(17, samples[1].Size);
        Assert.False(samples[1].IsKeyframe);

        // What matters is that an offset points at that frame's payload and not at a block header, so it
        // is checked against the bytes rather than against an assumed header length.
        Assert.All(file[(int)samples[0].Offset..((int)samples[0].Offset + 40)], b => Assert.Equal(0xAA, b));
        Assert.All(file[(int)samples[1].Offset..((int)samples[1].Offset + 17)], b => Assert.Equal(0xBB, b));
    }

    [Fact]
    public void Sample_offsets_point_at_the_real_bytes()
    {
        var frame = Frame(24, 0x5A);
        var file = File(
            Tracks(TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: [0x01], width: 8, height: 8)),
            Cluster(0, SimpleBlock(1, 0, keyframe: true, frame)));

        var sample = Assert.Single(Assert.Single(Index(file).Tracks).Samples);

        Assert.Equal(frame, file[(int)sample.Offset..((int)sample.Offset + sample.Size)]);
    }

    [Fact]
    public void Fixed_lacing_becomes_one_sample_per_frame()
    {
        var index = Index(File(
            Tracks(TrackEntry(2, 2, "A_AC3", channels: 6)),
            Cluster(0, LacedSimpleBlock(2, 0, Lacing.Fixed,
                Frame(10, 0x01), Frame(10, 0x02), Frame(10, 0x03)))));

        var track = Assert.Single(index.Tracks);
        Assert.Equal(3, track.Samples.Count);
        Assert.Equal(1, track.LacedBlocks);
        Assert.All(track.Samples, sample => Assert.Equal(10, sample.Size));
        Assert.Equal(track.Samples[0].Offset + 10, track.Samples[1].Offset);
        Assert.Equal(track.Samples[1].Offset + 10, track.Samples[2].Offset);
    }

    [Fact]
    public void Xiph_lacing_becomes_one_sample_per_frame()
    {
        var index = Index(File(
            Tracks(TrackEntry(2, 2, "A_AC3", channels: 2)),
            Cluster(0, LacedSimpleBlock(2, 0, Lacing.Xiph,
                Frame(300, 0x01), Frame(7, 0x02), Frame(19, 0x03)))));

        var track = Assert.Single(index.Tracks);
        Assert.Equal([300, 7, 19], track.Samples.Select(sample => sample.Size));
        Assert.Equal(1, track.LacedBlocks);
    }

    [Fact]
    public void Ebml_lacing_becomes_one_sample_per_frame()
    {
        var index = Index(File(
            Tracks(TrackEntry(3, 2, "A_EAC3", channels: 6)),
            Cluster(0, LacedSimpleBlock(3, 0, Lacing.Ebml,
                Frame(120, 0x01), Frame(90, 0x02), Frame(45, 0x03)))));

        var track = Assert.Single(index.Tracks);
        Assert.Equal([120, 90, 45], track.Samples.Select(sample => sample.Size));
        Assert.Equal(1, track.LacedBlocks);
    }

    [Fact]
    public void A_track_nothing_can_describe_is_listed_but_its_frames_are_not()
    {
        var index = Index(File(
            Tracks(
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: [0x01], width: 8, height: 8),
                TrackEntry(2, 2, "A_TRUEHD", channels: 8),
                TrackEntry(3, 2, "A_AC3", channels: 6)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(20, 0x01)),
                SimpleBlock(2, 0, true, Frame(4000, 0x02)),
                SimpleBlock(3, 0, true, Frame(5, 0x03)))));

        // The track stays: its ordinal is what keeps the viewer's stored stream indexes lined up with the
        // file, and the resolver has to see it to explain why it cannot be used.
        Assert.Equal(3, index.Tracks.Count);
        Assert.Equal("A_TRUEHD", index.Track(2)!.CodecId);
        Assert.Equal(1, index.Track(2)!.Ordinal);
        Assert.Equal(8, index.Track(2)!.Channels);

        // Its frames do not. On production one such track was 96 % of its film's index.
        Assert.Empty(index.Track(2)!.Samples);
        Assert.Single(index.Track(1)!.Samples);
        Assert.Single(index.Track(3)!.Samples);
    }

    [Fact]
    public void An_aac_track_is_walked_only_when_its_configuration_can_be_described()
    {
        // A_AAC is described from CodecPrivate — it *is* the AudioSpecificConfig — so the walk asks the
        // same question the synthesiser will: not "is there a config" but "can this config be written".
        // Track 2 is LC at 48 kHz stereo; track 3 has none at all; track 4 declares explicit SBR, which
        // is refused. Walking either of the last two would be bytes nothing could ever point at.
        var index = Index(File(
            Tracks(
                TrackEntry(2, 2, "A_AAC", codecPrivate: [0x11, 0x90], channels: 2),
                TrackEntry(3, 2, "A_AAC", channels: 2),
                TrackEntry(4, 2, "A_AAC", codecPrivate: [0x29, 0x90], channels: 2)),
            Cluster(0,
                SimpleBlock(2, 0, true, Frame(300, 0x01)),
                SimpleBlock(3, 0, true, Frame(300, 0x02)),
                SimpleBlock(4, 0, true, Frame(300, 0x03)))));

        Assert.Single(index.Track(2)!.Samples);
        Assert.Empty(index.Track(3)!.Samples);
        Assert.Empty(index.Track(4)!.Samples);
    }

    [Fact]
    public void A_video_track_without_its_configuration_is_not_walked()
    {
        // The codec is one a sample entry could be written for, but the track came without the record that
        // describes it — so nothing could ever point at these frames.
        var index = Index(File(
            Tracks(TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", width: 8, height: 8)),
            Cluster(0, SimpleBlock(1, 0, true, Frame(20, 0x01)))));

        Assert.Empty(Assert.Single(index.Tracks).Samples);
    }

    [Fact]
    public void A_bitmap_subtitle_is_listed_but_not_walked()
    {
        var index = Index(File(
            Tracks(
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: [0x01], width: 8, height: 8),
                TrackEntry(4, 17, "S_HDMV/PGS")),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(20, 0x01)),
                SimpleBlock(4, 0, true, Frame(600, 0x02)))));

        Assert.Equal(2, index.Tracks.Count);
        Assert.Empty(index.Track(4)!.Samples);
    }

    [Fact]
    public void An_unlaced_block_is_not_counted_as_laced()
    {
        var index = Index(File(
            Tracks(TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: [0x01], width: 8, height: 8)),
            Cluster(0, SimpleBlock(1, 0, keyframe: true, Frame(12, 0xEE)))));

        Assert.Equal(0, Assert.Single(index.Tracks).LacedBlocks);
    }

    [Fact]
    public void A_block_group_is_a_keyframe_exactly_when_it_references_nothing()
    {
        var index = Index(File(
            Tracks(TrackEntry(1, 1, "V_MPEG4/ISO/AVC", codecPrivate: [0x01], width: 8, height: 8)),
            Cluster(0,
                BlockGroup(1, 0, references: false, Frame(30, 0x01)),
                BlockGroup(1, 40, references: true, Frame(11, 0x02)))));

        var samples = Assert.Single(index.Tracks).Samples;
        Assert.True(samples[0].IsKeyframe);
        Assert.False(samples[1].IsKeyframe);
    }

    [Fact]
    public void Samples_from_several_clusters_land_on_the_right_tracks()
    {
        var index = Index(File(
            Tracks(
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: [0x01], width: 8, height: 8),
                TrackEntry(2, 2, "A_AC3", channels: 6)),
            Cluster(0, SimpleBlock(1, 0, true, Frame(20, 0x01)), SimpleBlock(2, 0, true, Frame(5, 0x02))),
            Cluster(1000, SimpleBlock(1, 0, true, Frame(21, 0x03)), SimpleBlock(2, 0, true, Frame(6, 0x04)))));

        Assert.Equal(2, index.Track(1)!.Samples.Count);
        Assert.Equal(2, index.Track(2)!.Samples.Count);
        Assert.Equal([0, 1000], index.Track(1)!.Samples.Select(sample => sample.Timestamp));
        Assert.Equal([20, 21], index.Track(1)!.Samples.Select(sample => sample.Size));
    }

    [Fact]
    public void The_timestamp_scale_is_read_rather_than_assumed()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(1000, timestampScale: 100_000),
            Tracks(TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: [0x01], width: 8, height: 8)));

        Assert.Equal(100_000, Index(file).TimestampScale);
    }

    [Fact]
    public void A_lacing_header_that_does_not_add_up_leaves_the_block_whole()
    {
        // Three frames promised, sizes that overrun the payload. Slicing on those numbers would produce
        // samples pointing outside the block, which is worse than one sample that is merely coarse.
        var index = Index(File(
            Tracks(TrackEntry(2, 2, "A_AC3", channels: 2)),
            Cluster(0, LacedSimpleBlock(2, 0, Lacing.Xiph,
                Frame(250, 0x01), Frame(250, 0x02), Frame(1, 0x03)))));

        var track = Assert.Single(index.Tracks);
        foreach (var sample in track.Samples)
        {
            Assert.InRange(sample.Offset + sample.Size, 0, index.SourceLength);
        }
    }

    [Fact]
    public void Fixed_lacing_that_does_not_divide_evenly_leaves_the_block_whole()
    {
        // Three "equal" frames over a payload that is not a multiple of three. Slicing anyway would
        // truncate every frame and quietly lose the remainder.
        var index = Index(File(
            Tracks(TrackEntry(2, 2, "A_AC3", channels: 2)),
            Cluster(0, LacedSimpleBlock(2, 0, Lacing.Fixed,
                Frame(10, 0x01), Frame(10, 0x02), Frame(11, 0x03)))));

        var track = Assert.Single(index.Tracks);
        var sample = Assert.Single(track.Samples);
        // The whole payload, lacing header byte and all: on corrupt input the sample is unusable either
        // way, and what matters is that the offsets stay inside the block rather than run past it.
        Assert.Equal(32, sample.Size);
        Assert.Equal(0, track.LacedBlocks);
        Assert.InRange(sample.Offset + sample.Size, 0, index.SourceLength);
    }
}
