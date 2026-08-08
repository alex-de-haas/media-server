using System.Text;
using MediaServer.Api.Remux;
using MediaServer.Api.Tests.Probe;
using static MediaServer.Api.Tests.Remux.RemuxContainerBuilders;

namespace MediaServer.Api.Tests.Remux;

public sealed class Mp4SynthesizerTests
{
    private static readonly byte[] Hvcc = [0x01, 0x22, 0x20, 0x00, 0x00, 0x00, 0xB0];
    private static readonly byte[] Avcc = [0x01, 0x64, 0x00, 0x28, 0xFF];
    private static readonly byte[] DolbyVision = [0x01, 0x00, 0x10, 0x35, 0x10, 0x00, 0x00, 0x00];

    /// <summary>
    /// A real AC-3 sync frame header: 48 kHz, 3/2 with LFE. Six channels is what the descriptor should be
    /// read out of the bitstream as, not out of the container's own claim.
    /// </summary>
    private static byte[] Ac3Frame(int size) =>
        [0x0B, 0x77, 0x00, 0x00, 0x14, 0x40, 0xEB, .. new byte[Math.Max(0, size - 7)]];

    private sealed record Built(Mp4Synthesizer.Result Result, Mp4BoxReader Reader, MatroskaIndex Index);

    private static Built Build(
        byte[] file,
        VideoSignalling signalling = VideoSignalling.DolbyVision,
        IReadOnlyList<ulong>? tracks = null)
    {
        var stream = new MemoryStream(file);
        var index = MatroskaIndexer.Build(stream);
        var numbers = tracks ?? index.Tracks
            .Where(track => track.Kind is IndexedTrackKind.Video or IndexedTrackKind.Audio)
            .Select(track => track.Number)
            .ToList();

        var result = Mp4Synthesizer.Build(index, numbers, signalling, stream);
        Assert.NotNull(result);
        return new Built(result, new Mp4BoxReader(result.Header), index);
    }

    private static byte[] VideoAndAudio(
        byte[]? codecPrivate = null,
        byte[]? dv = null,
        string codec = "V_MPEGH/ISO/HEVC",
        int primaries = 9,
        int transfer = 16,
        int matrix = 9)
    {
        var tracks = ContainerBuilders.Ebml(0x1654AE6B,
            TrackEntry(1, 1, codec, codecPrivate: codecPrivate ?? Hvcc, dolbyVision: dv,
                width: 3840, height: 2160, defaultDuration: 40_000_000,
                primaries: primaries, transfer: transfer, matrix: matrix),
            TrackEntry(2, 2, "A_AC3", channels: 6));

        // Four video frames at a constant 40 ms, and two audio frames.
        return ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            tracks,
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(100, 0x11)),
                SimpleBlock(1, 40, false, Frame(50, 0x22)),
                SimpleBlock(1, 80, false, Frame(60, 0x33)),
                SimpleBlock(1, 120, false, Frame(70, 0x44)),
                SimpleBlock(2, 0, true, Ac3Frame(200)),
                SimpleBlock(2, 32, true, Ac3Frame(200))));
    }

    [Fact]
    public void The_header_is_ftyp_then_moov_then_an_mdat_wrapping_the_source()
    {
        var file = VideoAndAudio();
        var built = Build(file);

        Assert.Equal(["ftyp", "moov", "mdat"], built.Reader.Top.Select(box => box.Type));
        Assert.Equal(built.Result.HeaderLength + file.Length, built.Result.TotalLength);

        // The mdat declares itself as covering its own header plus the whole source.
        var mdat = built.Reader.Top.Single(box => box.Type == "mdat");
        Assert.Equal(built.Result.HeaderLength, mdat.Start);
    }

    [Fact]
    public void Sample_offsets_land_inside_the_wrapped_source()
    {
        var file = VideoAndAudio();
        var built = Build(file);

        var video = built.Index.Track(1)!;
        var offsets = built.Reader.ChunkOffsets(built.Reader.Find("moov/trak/mdia/minf/stbl/co64").First());

        Assert.Equal(video.Samples.Count, offsets.Count);
        for (var i = 0; i < offsets.Count; i++)
        {
            // An output offset is the header's length plus the offset in the source, which is the whole
            // trick: no byte of media is moved.
            Assert.Equal((ulong)(video.Samples[i].Offset + built.Result.HeaderLength), offsets[i]);
            Assert.InRange(offsets[i], (ulong)built.Result.HeaderLength, (ulong)built.Result.TotalLength);
        }
    }

    [Fact]
    public void Dolby_vision_is_offered_only_when_the_source_carries_its_configuration()
    {
        Assert.Equal("dvh1", Build(VideoAndAudio(dv: DolbyVision)).Result.SampleEntries[0]);
        Assert.Equal("hvc1", Build(VideoAndAudio(dv: null)).Result.SampleEntries[0]);
    }

    [Fact]
    public void The_cross_compatible_form_is_written_when_it_is_asked_for()
    {
        var built = Build(VideoAndAudio(dv: DolbyVision), VideoSignalling.CrossCompatible);

        Assert.Equal("hvc1", built.Result.SampleEntries[0]);

        // The configuration is still carried: a player that reads it sees HDR10, which is the point of the
        // cross-compatible form.
        var stsd = built.Reader.Find("moov/trak/mdia/minf/stbl/stsd").First();
        var entry = built.Reader.SampleEntry(stsd);
        Assert.Contains("dvvC", built.Reader.Children(entry.Start + 78, entry.End).Select(box => box.Type));
    }

    [Fact]
    public void H264_gets_its_own_configuration_box_and_entry_even_when_dolby_vision_is_asked_for()
    {
        var built = Build(
            VideoAndAudio(codecPrivate: Avcc, dv: DolbyVision, codec: "V_MPEG4/ISO/AVC"),
            VideoSignalling.DolbyVision);

        Assert.Equal("avc1", built.Result.SampleEntries[0]);

        var stsd = built.Reader.Find("moov/trak/mdia/minf/stbl/stsd").First();
        var entry = built.Reader.SampleEntry(stsd);
        var children = built.Reader.Children(entry.Start + 78, entry.End).Select(box => box.Type).ToList();
        Assert.Contains("avcC", children);
        Assert.DoesNotContain("hvcC", children);
    }

    [Fact]
    public void Colour_is_written_when_the_container_states_it()
    {
        var built = Build(VideoAndAudio());
        var stsd = built.Reader.Find("moov/trak/mdia/minf/stbl/stsd").First();
        var entry = built.Reader.SampleEntry(stsd);

        var colr = built.Reader.Children(entry.Start + 78, entry.End).Single(box => box.Type == "colr");
        Assert.Equal("nclx", Encoding.ASCII.GetString(built.Result.Header, colr.Start, 4));
        Assert.Equal(9, built.Result.Header[colr.Start + 5]);       // primaries
        Assert.Equal(16, built.Result.Header[colr.Start + 7]);      // transfer
        Assert.Equal(9, built.Result.Header[colr.Start + 9]);       // matrix
    }

    [Fact]
    public void Colour_is_left_out_rather_than_guessed_when_the_container_is_silent()
    {
        var built = Build(VideoAndAudio(primaries: 0, transfer: 0, matrix: 0));
        var stsd = built.Reader.Find("moov/trak/mdia/minf/stbl/stsd").First();
        var entry = built.Reader.SampleEntry(stsd);

        Assert.DoesNotContain("colr", built.Reader.Children(entry.Start + 78, entry.End).Select(box => box.Type));
    }

    [Fact]
    public void A_constant_frame_rate_collapses_to_one_timing_run()
    {
        var built = Build(VideoAndAudio());
        var stts = built.Reader.Find("moov/trak/mdia/minf/stbl/stts").First();

        var runs = built.Reader.Runs(stts);
        var run = Assert.Single(runs);
        Assert.Equal(4u, run.Count);
        Assert.Equal(40_000_000u, run.Value);       // 40 ms, in nanoseconds
    }

    [Fact]
    public void Frames_in_display_order_need_no_composition_table()
    {
        var built = Build(VideoAndAudio());

        Assert.Empty(built.Reader.Find("moov/trak/mdia/minf/stbl/ctts"));
    }

    [Fact]
    public void Frames_out_of_display_order_get_one()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000)),
            // Stored 0, 80, 40: the second frame is shown last, which is what a composition offset is for.
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(10, 0x01)),
                SimpleBlock(1, 80, false, Frame(10, 0x02)),
                SimpleBlock(1, 40, false, Frame(10, 0x03))));

        var built = Build(file, tracks: [1]);

        Assert.NotEmpty(built.Reader.Find("moov/trak/mdia/minf/stbl/ctts"));
    }

    [Fact]
    public void A_sync_table_is_omitted_when_every_sample_is_one()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(80),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(10, 0x01)),
                SimpleBlock(1, 40, true, Frame(10, 0x02))));

        var built = Build(file, tracks: [1]);

        Assert.Empty(built.Reader.Find("moov/trak/mdia/minf/stbl/stss"));
    }

    [Fact]
    public void A_sync_table_is_written_when_only_some_samples_are_keyframes()
    {
        var built = Build(VideoAndAudio());

        var stss = Assert.Single(built.Reader.Find("moov/trak/mdia/minf/stbl/stss"));
        Assert.Equal(1u, built.Reader.U32At(stss.Start + 4));
        Assert.Equal(1u, built.Reader.U32At(stss.Start + 8));    // sample numbers are one-based
    }

    [Fact]
    public void The_audio_descriptor_is_read_out_of_the_bitstream()
    {
        var built = Build(VideoAndAudio());

        Assert.Equal("ac-3", built.Result.SampleEntries[1]);

        var stsd = built.Reader.Find("moov/trak/mdia/minf/stbl/stsd").Skip(1).First();
        var entry = built.Reader.SampleEntry(stsd);
        Assert.Equal("ac-3", entry.Type);
        // Six channels and 48 kHz come from the sync frame, not from the container's Channels element.
        Assert.Equal(6, built.Result.Header[entry.Start + 17]);
        Assert.Equal(48000u, built.Reader.U32At(entry.Start + 24) >> 16);
        Assert.Contains("dac3", built.Reader.Children(entry.Start + 28, entry.End).Select(box => box.Type));
    }

    [Fact]
    public void A_track_the_synthesiser_cannot_describe_is_left_out()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(80),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_DTS", channels: 6)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(10, 0x01)),
                SimpleBlock(2, 0, true, Frame(40, 0x02))));

        var built = Build(file);

        // DTS is not something an MP4 track can be written for here, so it is dropped rather than
        // described wrongly.
        Assert.Equal(["hvc1"], built.Result.SampleEntries);
    }

    [Fact]
    public void Nothing_usable_produces_nothing_rather_than_an_empty_container()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(80),
            ContainerBuilders.Ebml(0x1654AE6B, TrackEntry(9, 17, "S_TEXT/UTF8")));

        var stream = new MemoryStream(file);
        var index = MatroskaIndexer.Build(stream);

        Assert.Null(Mp4Synthesizer.Build(index, [9], VideoSignalling.DolbyVision, stream));
    }

    [Fact]
    public void Track_order_is_the_callers_and_the_video_track_comes_first()
    {
        var built = Build(VideoAndAudio(), tracks: [2, 1]);

        Assert.Equal(["ac-3", "hvc1"], built.Result.SampleEntries);
    }
}
