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

        var result = Mp4Synthesizer.Build(
            [new Mp4Synthesizer.Input(index, stream)],
            [.. numbers.Select(number => new Mp4Synthesizer.TrackRef(0, number))],
            signalling);
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
        Assert.Empty(built.Result.Wrappers);   // one input needs no wrapper between files

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
        // Counted in the source's own ticks, which are milliseconds here. Nanoseconds would be exact
        // too, but a 32-bit delta would then top out at 4.29 s.
        Assert.Equal(40u, run.Value);
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

        Assert.Null(Mp4Synthesizer.Build(
            [new Mp4Synthesizer.Input(index, stream)],
            [new Mp4Synthesizer.TrackRef(0, 9)],
            VideoSignalling.DolbyVision));
    }

    [Fact]
    public void Track_order_is_the_callers_and_the_video_track_comes_first()
    {
        var built = Build(VideoAndAudio(), tracks: [2, 1]);

        Assert.Equal(["ac-3", "hvc1"], built.Result.SampleEntries);
    }

    private static byte[] WithSubtitles(bool durations = true, string codec = "S_TEXT/UTF8")
    {
        var tracks = ContainerBuilders.Ebml(0x1654AE6B,
            TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                defaultDuration: 40_000_000),
            TrackEntry(3, 17, codec));

        var cue = System.Text.Encoding.UTF8.GetBytes("<i>Hello</i>");
        var later = System.Text.Encoding.UTF8.GetBytes("Later");

        return ContainerBuilders.Matroska(
            ContainerBuilders.Info(400),
            tracks,
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(10, 0x01)),
                SimpleBlock(1, 40, false, Frame(10, 0x02)),
                // A cue at 40 ms for 60 ms, then a gap, then one at 200 ms for 50 ms.
                durations ? BlockGroup(3, 40, false, 60, cue) : BlockGroup(3, 40, false, cue),
                durations ? BlockGroup(3, 200, false, 50, later) : BlockGroup(3, 200, false, later)));
    }

    [Fact]
    public void A_text_subtitle_track_becomes_timed_text()
    {
        var built = Build(WithSubtitles(), tracks: [1, 3]);

        Assert.Equal(["hvc1", "tx3g"], built.Result.SampleEntries);
    }

    [Fact]
    public void Timed_text_samples_are_carried_in_the_header_rather_than_pointed_at()
    {
        var file = WithSubtitles();
        var built = Build(file, tracks: [1, 3]);

        var co64 = built.Reader.Find("moov/trak/mdia/minf/stbl/co64").Skip(1).First();
        var offsets = built.Reader.ChunkOffsets(co64);

        // Every subtitle sample lives inside the header — nothing in the source could serve one, because a
        // length prefix and the empty samples between cues exist nowhere in Matroska.
        Assert.All(offsets, offset => Assert.InRange(offset, 0ul, (ulong)built.Result.HeaderLength));
    }

    [Fact]
    public void The_header_carries_a_second_mdat_for_the_rewritten_text()
    {
        var withText = Build(WithSubtitles(), tracks: [1, 3]);
        var withoutText = Build(WithSubtitles(), tracks: [1]);

        Assert.Equal(2, withText.Reader.Top.Count(box => box.Type == "mdat"));
        Assert.Equal(1, withoutText.Reader.Top.Count(box => box.Type == "mdat"));
    }

    [Fact]
    public void A_gap_between_cues_becomes_an_empty_sample()
    {
        var built = Build(WithSubtitles(), tracks: [1, 3]);

        var stsz = built.Reader.Find("moov/trak/mdia/minf/stbl/stsz").Skip(1).First();

        // Four samples: nothing on screen until 40 ms, the first cue, nothing from 100 ms to 200 ms, then
        // the second. A timed-text track has no way to express a gap other than by saying nothing in it.
        Assert.Equal(4u, built.Reader.U32At(stsz.Start + 8));
        Assert.Equal(2u, built.Reader.U32At(stsz.Start + 12));       // leading filler: a bare length of zero
        Assert.Equal(2u, built.Reader.U32At(stsz.Start + 12 + 8));   // and the one between the cues
    }

    [Fact]
    public void The_markup_is_gone_from_what_is_written()
    {
        var built = Build(WithSubtitles(), tracks: [1, 3]);

        var text = System.Text.Encoding.UTF8.GetString(built.Result.Header);
        Assert.Contains("Hello", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<i>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_subtitle_track_that_states_no_durations_is_left_out()
    {
        var built = Build(WithSubtitles(durations: false), tracks: [1, 3]);

        // A cue with no end cannot be expressed: MP4 has no "until the next one".
        Assert.Equal(["hvc1"], built.Result.SampleEntries);
    }

    [Fact]
    public void A_bitmap_subtitle_track_is_left_out()
    {
        var built = Build(WithSubtitles(codec: "S_HDMV/PGS"), tracks: [1, 3]);

        Assert.Equal(["hvc1"], built.Result.SampleEntries);
    }

    [Fact]
    public void A_long_gap_between_cues_does_not_overflow_the_timing_table()
    {
        var cue = System.Text.Encoding.UTF8.GetBytes("Much later");
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(700_000),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(3, 17, "S_TEXT/UTF8")),
            Cluster(0, SimpleBlock(1, 0, true, Frame(10, 0x01))),
            // Ten minutes of nothing, then a cue. In nanoseconds that gap is 600,000,000,000 — a
            // hundred and forty times what a 32-bit field holds. A block's own timecode is a signed
            // 16-bit offset from its cluster, so a gap this size is expressed by the cluster.
            Cluster(600_000, BlockGroup(3, 0, false, 3000, cue)));

        var built = Build(file, tracks: [1, 3]);

        var stts = built.Reader.Find("moov/trak/mdia/minf/stbl/stts").Skip(1).First();
        var runs = built.Reader.Runs(stts);
        Assert.Equal(600_000u, runs[0].Value);      // the gap, in milliseconds, intact
        Assert.Equal(3000u, runs[1].Value);         // and the cue's own three seconds
    }

    [Fact]
    public void An_eac3_track_is_left_out_rather_than_described_as_ac3()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_EAC3", channels: 6)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(10, 0x01)),
                SimpleBlock(2, 0, true, Ac3Frame(200))));

        // E-AC-3 is an `ec-3` entry with a `dec3` descriptor in MP4; calling it `ac-3` would misstate
        // the stream, and a missing track is easier to notice than a mislabelled one.
        Assert.Equal(["hvc1"], Build(file).Result.SampleEntries);
    }

    [Fact]
    public void An_audio_track_counts_in_its_own_sample_rate()
    {
        var built = Build(VideoAndAudio());

        var mdhd = built.Reader.Find("moov/trak/mdia/mdhd").Skip(1).First();
        Assert.Equal(48000u, built.Reader.U32At(mdhd.Start + 20));

        var stts = built.Reader.Find("moov/trak/mdia/minf/stbl/stts").Skip(1).First();
        // 1536 samples a frame, which is exact at any rate — unlike 32 ms, which is not at 44.1 kHz.
        Assert.Equal(1536u, built.Reader.Runs(stts)[0].Value);
    }

    [Fact]
    public void A_second_input_gets_its_own_wrapper_and_its_own_base()
    {
        var main = VideoAndAudio();
        var dub = ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B, TrackEntry(1, 2, "A_AC3", channels: 6)),
            Cluster(0, SimpleBlock(1, 0, true, Ac3Frame(300))));

        var mainStream = new MemoryStream(main);
        var dubStream = new MemoryStream(dub);
        var mainIndex = MatroskaIndexer.Build(mainStream);
        var dubIndex = MatroskaIndexer.Build(dubStream);

        var result = Mp4Synthesizer.Build(
            [new Mp4Synthesizer.Input(mainIndex, mainStream), new Mp4Synthesizer.Input(dubIndex, dubStream)],
            [new Mp4Synthesizer.TrackRef(0, 1), new Mp4Synthesizer.TrackRef(1, 1)],
            VideoSignalling.CrossCompatible);

        Assert.NotNull(result);
        Assert.Equal(["hvc1", "ac-3"], result.SampleEntries);

        // One wrapper for the second file, which sits between the two of them.
        var wrapper = Assert.Single(result.Wrappers);
        Assert.Equal(16, wrapper.Length);
        Assert.Equal("mdat", System.Text.Encoding.ASCII.GetString(wrapper, 4, 4));

        Assert.Equal(
            result.HeaderLength + main.Length + wrapper.Length + dub.Length,
            result.TotalLength);

        // The dub's samples are addressed past the video's file, not from the same base.
        var reader = new Mp4BoxReader(result.Header);
        var dubOffsets = reader.ChunkOffsets(reader.Find("moov/trak/mdia/minf/stbl/co64").Skip(1).First());
        Assert.All(dubOffsets, offset =>
            Assert.InRange(offset, (ulong)(result.HeaderLength + main.Length), (ulong)result.TotalLength));
    }
}
