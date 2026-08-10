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

    /// <summary>
    /// An E-AC-3 sync frame: stream type 0, six blocks, 3/2 with LFE at 48 kHz, bitstream id 16. The frame
    /// size is stated in the header as words less one, so it has to match what is handed over.
    /// </summary>
    private static byte[] Eac3Frame(int size, int streamType = 0)
    {
        var words = (size / 2) - 1;
        return
        [
            0x0B, 0x77,
            (byte)((streamType << 6) | ((words >> 8) & 0x07)),
            (byte)(words & 0xFF),
            0x3F,                                   // fscod 0, numblkscod 3, acmod 7, lfeon 1
            0x80,                                   // bsid 16
            .. new byte[Math.Max(0, size - 6)],
        ];
    }

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

    /// <summary>
    /// An AudioSpecificConfig packed from its fields, so a test reads as the thing it is describing rather
    /// than as two hex bytes whose meaning has to be taken on trust.
    /// </summary>
    private static byte[] Asc(
        int objectType, int frequencyIndex, int channelConfiguration,
        int frameLengthFlag = 0, int? explicitRate = null)
    {
        var bits = new List<int>();
        void Write(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                bits.Add((value >> i) & 1);
            }
        }

        Write(objectType, 5);
        Write(frequencyIndex, 4);
        if (frequencyIndex == 15)
        {
            Write(explicitRate ?? 0, 24);
        }

        Write(channelConfiguration, 4);
        Write(frameLengthFlag, 1);
        Write(0, 2);                                // dependsOnCoreCoder, extensionFlag

        var bytes = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++)
        {
            bytes[i / 8] |= (byte)(bits[i] << (7 - (i % 8)));
        }

        return bytes;
    }

    [Fact]
    public void Aac_priming_is_trimmed_by_an_edit_list_rather_than_left_to_be_heard()
    {
        // A whole frame of encoder priming, as every AAC encoder produces and Matroska states in
        // nanoseconds. Without an edit list the soundtrack starts 1024 samples — 21 ms — after the
        // picture, which a round trip through ffmpeg showed and no unit test would have.
        var built = Build(AacFile(
            Asc(objectType: 2, frequencyIndex: 3, channelConfiguration: 2),
            codecDelay: 21_333_333));

        var elst = Assert.Single(built.Reader.Find("moov/trak/edts/elst"));
        Assert.Equal(1u, built.Reader.U32At(elst.Start + 4));

        // Rounded, not truncated: 21333333 ns × 48000 ÷ 1e9 truncates to 1023 and leaves the track one
        // sample late, which is exactly what the round trip caught.
        Assert.Equal(1024, (int)built.Reader.U32At(elst.Start + 12));
    }

    [Fact]
    public void A_track_with_no_priming_gets_no_edit_list_at_all()
    {
        // AC-3 has no encoder delay and states none, and an edit list starting at zero would say the same
        // as no edit list while giving a player one more thing to disagree about.
        Assert.Empty(Build(VideoAndAudio()).Reader.Find("moov/trak/edts/elst"));
        Assert.Empty(
            Build(AacFile(Asc(objectType: 2, frequencyIndex: 3, channelConfiguration: 2)))
                .Reader.Find("moov/trak/edts/elst"));
    }

    private static byte[] AacFile(byte[] config, int frames = 3, ulong codecDelay = 0) =>
        ContainerBuilders.Matroska(
            ContainerBuilders.Info(80),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_AAC", codecPrivate: config, codecDelay: codecDelay, channels: 2)),
            Cluster(0, [
                SimpleBlock(1, 0, true, Frame(10, 0x01)),
                .. Enumerable.Range(0, frames)
                    .Select(i => SimpleBlock(2, (short)(i * 21), true, Frame(300, (byte)(0x40 + i))))]));

    [Fact]
    public void Aac_is_described_from_its_codec_private_rather_than_from_a_frame()
    {
        // AAC-LC, 48 kHz, stereo — the canonical 0x11 0x90. Matroska stores this verbatim and it is
        // exactly the payload the esds wants, so nothing is read out of the bitstream.
        var config = Asc(objectType: 2, frequencyIndex: 3, channelConfiguration: 2);
        Assert.Equal([0x11, 0x90], config);

        var built = Build(AacFile(config));

        Assert.Equal(["hvc1", "mp4a"], built.Result.SampleEntries);

        var stsd = built.Reader.Find("moov/trak/mdia/minf/stbl/stsd").Skip(1).First();
        var entry = built.Reader.SampleEntry(stsd);
        Assert.Equal("mp4a", entry.Type);
        Assert.Equal(2, built.Result.Header[entry.Start + 17]);
        Assert.Equal(48000u, built.Reader.U32At(entry.Start + 24) >> 16);

        var esds = Assert.Single(
            built.Reader.Children(entry.Start + 28, entry.End), box => box.Type == "esds");

        // The config comes back out byte for byte, which is the whole point of carrying it rather than
        // deriving it.
        Assert.Contains(
            config,
            Enumerable.Range(esds.Start, esds.Length - config.Length)
                .Select(at => built.Result.Header[at..(at + config.Length)]));
    }

    [Fact]
    public void The_esds_carries_the_descriptor_chain_mp4_requires()
    {
        var built = Build(AacFile(Asc(objectType: 2, frequencyIndex: 3, channelConfiguration: 2)));
        var stsd = built.Reader.Find("moov/trak/mdia/minf/stbl/stsd").Skip(1).First();
        var entry = built.Reader.SampleEntry(stsd);
        var esds = Assert.Single(
            built.Reader.Children(entry.Start + 28, entry.End), box => box.Type == "esds");

        // version and flags, then ES_Descriptor (0x03) with its length, ES_ID and flags byte.
        var body = built.Result.Header[esds.Start..esds.End];
        Assert.Equal(0x03, body[4]);

        // Every tag in the chain, in order: ES, DecoderConfig, DecoderSpecificInfo, SLConfig.
        Assert.Equal([0x03, 0x04, 0x05, 0x06], Tags(body));
    }

    /// <summary>Walks the descriptor chain the way a demuxer would, following the expandable lengths.</summary>
    private static List<byte> Tags(byte[] esdsBody)
    {
        var tags = new List<byte>();
        var at = 4;                                 // past version and flags
        while (at < esdsBody.Length)
        {
            tags.Add(esdsBody[at++]);
            var length = 0;
            while (at < esdsBody.Length)
            {
                var next = esdsBody[at++];
                length = (length << 7) | (next & 0x7F);
                if ((next & 0x80) == 0)
                {
                    break;
                }
            }

            // ES and DecoderConfig hold their successors inside them, so their fixed fields are stepped
            // over rather than their whole payload.
            at += tags[^1] switch { 0x03 => 3, 0x04 => 13, _ => length };
        }

        return tags;
    }

    [Theory]
    [InlineData(2, 3, 2, 0, 1024)]                  // LC, the ordinary case
    [InlineData(2, 3, 2, 1, 960)]                   // frameLengthFlag: 960 rather than 1024
    [InlineData(1, 4, 6, 0, 1024)]                  // Main at 44.1 kHz, 5.1
    public void The_frame_length_is_read_from_the_config_rather_than_assumed(
        int objectType, int frequencyIndex, int channels, int frameLengthFlag, int expected)
    {
        var built = Build(AacFile(Asc(objectType, frequencyIndex, channels, frameLengthFlag)));

        // One run of three frames, each the stated length. A wrong constant here plays the track at the
        // wrong speed rather than failing, which is why it is read.
        var stts = built.Reader.Find("moov/trak/mdia/minf/stbl/stts").Skip(1).First();
        Assert.Equal(1u, built.Reader.U32At(stts.Start + 4));
        Assert.Equal(3u, built.Reader.U32At(stts.Start + 8));
        Assert.Equal((uint)expected, built.Reader.U32At(stts.Start + 12));
    }

    [Fact]
    public void An_escaped_sampling_frequency_is_read_rather_than_refused()
    {
        var built = Build(AacFile(
            Asc(objectType: 2, frequencyIndex: 15, channelConfiguration: 2, explicitRate: 44100)));

        var stsd = built.Reader.Find("moov/trak/mdia/minf/stbl/stsd").Skip(1).First();
        var entry = built.Reader.SampleEntry(stsd);
        Assert.Equal(44100u, built.Reader.U32At(entry.Start + 24) >> 16);
    }

    [Theory]
    // Explicitly signalled SBR and PS. Both declare a second sampling frequency, and the conventions for
    // which one belongs in the sample entry disagree — a wrong choice plays at half or double speed.
    [InlineData(5, 3, 2)]
    [InlineData(29, 3, 2)]
    // A channel configuration of zero defers to a program config element inside the first frame, which is
    // a bitstream read this deliberately does not do.
    [InlineData(2, 3, 0)]
    // Reserved sampling frequency indexes.
    [InlineData(2, 13, 2)]
    [InlineData(2, 14, 2)]
    // Object types that are not plain AAC: SSR-era scalable, ER AAC LD, and an escaped type.
    [InlineData(6, 3, 2)]
    [InlineData(23, 3, 2)]
    [InlineData(31, 3, 2)]
    public void A_config_this_cannot_be_sure_of_is_refused_rather_than_guessed_at(
        int objectType, int frequencyIndex, int channelConfiguration)
    {
        var built = Build(AacFile(Asc(objectType, frequencyIndex, channelConfiguration)));

        Assert.Equal(["hvc1"], built.Result.SampleEntries);
    }

    [Fact]
    public void An_aac_track_with_no_config_at_all_is_left_out()
    {
        // Matroska requires CodecPrivate for A_AAC — it *is* the config — so this is a malformed file
        // rather than an old one. It must still not become a track with no descriptor.
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(80),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_AAC", channels: 2)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(10, 0x01)),
                SimpleBlock(2, 0, true, Frame(300, 0x40))));

        Assert.Equal(["hvc1"], Build(file).Result.SampleEntries);
    }

    [Fact]
    public void Only_the_default_of_each_kind_is_marked_enabled()
    {
        // Two audio tracks. The first is the viewer's choice and the player's default; a second marked
        // enabled would leave the default ambiguous.
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_AC3", channels: 6),
                TrackEntry(3, 2, "A_AC3", channels: 2)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(100, 0x11)),
                SimpleBlock(2, 0, true, Ac3Frame(200)),
                SimpleBlock(3, 0, true, Ac3Frame(200))));

        var built = Build(file, tracks: [1, 2, 3]);
        var flags = built.Reader.Find("moov/trak/tkhd")
            .Select(box => built.Reader.U32At(box.Start) & 0x00FFFFFF)
            .ToList();

        // Video enabled, first audio enabled, second in the movie but not enabled.
        Assert.Equal([3u, 3u, 2u], flags);
    }

    [Fact]
    public void Tracks_of_one_kind_are_declared_alternatives_to_each_other()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_AC3", channels: 6),
                TrackEntry(3, 2, "A_AC3", channels: 2)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(100, 0x11)),
                SimpleBlock(2, 0, true, Ac3Frame(200)),
                SimpleBlock(3, 0, true, Ac3Frame(200))));

        var built = Build(file, tracks: [1, 2, 3]);

        // alternate_group is a big-endian 16-bit field 46 bytes into the box body — past version and
        // flags, the two timestamps, the id, four reserved bytes, the duration, eight more reserved and
        // the layer — so its value is in the second of those two bytes.
        var groups = built.Reader.Find("moov/trak/tkhd")
            .Select(box => built.Result.Header[box.Start + 47])
            .ToList();

        Assert.Equal([0, 1, 1], groups);
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
        // described wrongly. End to end the walk now also declines to record its frames, which is why the
        // next test reaches this guard the only way left — with an index built by hand.
        Assert.Equal(["hvc1"], built.Result.SampleEntries);
    }

    [Fact]
    public void An_undescribable_track_that_reached_the_synthesiser_anyway_is_still_refused()
    {
        // The walk no longer records frames for a codec no sample entry covers, so this index could not
        // come out of it. The guard is kept for the paths that do not go through the walk — a hand-built
        // index, or a codec added to one vocabulary and forgotten in the other, which is exactly the drift
        // that once produced a container with a picture and no sound.
        var index = new MatroskaIndex { SourceLength = 4096, TimestampScale = 1_000_000 };
        index.Tracks.Add(new IndexedTrack
        {
            Number = 1, Ordinal = 0, Kind = IndexedTrackKind.Video,
            CodecId = "V_MPEGH/ISO/HEVC", CodecPrivate = Hvcc, Width = 8, Height = 8,
            DefaultDuration = 40_000_000,
        });
        index.Track(1)!.Samples.Add(new IndexedSample(0, 0, 10, true));

        index.Tracks.Add(new IndexedTrack
        {
            Number = 2, Ordinal = 1, Kind = IndexedTrackKind.Audio, CodecId = "A_DTS", Channels = 6,
        });
        index.Track(2)!.Samples.Add(new IndexedSample(0, 16, 40, true));

        var result = Mp4Synthesizer.Build(
            [new Mp4Synthesizer.Input(index, new MemoryStream(new byte[4096]))],
            [new Mp4Synthesizer.TrackRef(0, 1), new Mp4Synthesizer.TrackRef(0, 2)],
            VideoSignalling.CrossCompatible);

        Assert.NotNull(result);
        Assert.Equal(["hvc1"], result.SampleEntries);
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

    private static Mp4Synthesizer.Result BuildWithSubtitleDefault(
        byte[] file,
        SubtitleDefault subtitleDefault,
        IReadOnlyList<(IReadOnlyList<TextCue> Cues, string? Language)>? externalText = null)
    {
        var stream = new MemoryStream(file);
        var index = MatroskaIndexer.Build(stream);
        var result = Mp4Synthesizer.Build(
            [new Mp4Synthesizer.Input(index, stream)],
            [.. RemuxTrackChoice.Resolve(index, null, null)
                .Select(number => new Mp4Synthesizer.TrackRef(0, number))],
            VideoSignalling.CrossCompatible,
            externalText,
            subtitleDefault);

        Assert.NotNull(result);
        return result;
    }

    /// <summary>The `enabled` bit of every track header, in order.</summary>
    private static List<bool> Enabled(Mp4Synthesizer.Result result)
    {
        var reader = new Mp4BoxReader(result.Header);
        return [.. reader.Find("moov/trak/tkhd").Select(box => (reader.U32At(box.Start) & 1) == 1)];
    }

    [Fact]
    public void Carrying_a_subtitle_for_the_menu_is_not_turning_it_on()
    {
        // The regression this guards: once every subtitle is carried, marking "the first of its kind"
        // as default would put words on screen for a viewer who asked for none.
        var result = BuildWithSubtitleDefault(WithSubtitles(), SubtitleDefault.None);

        Assert.Equal(["hvc1", "tx3g"], result.SampleEntries);
        Assert.Equal([true, false], Enabled(result));
    }

    [Fact]
    public void A_chosen_embedded_subtitle_is_the_one_turned_on()
    {
        var result = BuildWithSubtitleDefault(WithSubtitles(), SubtitleDefault.Embedded);

        Assert.Equal([true, true], Enabled(result));
    }

    [Fact]
    public void A_chosen_external_subtitle_overtakes_the_embedded_ones()
    {
        // An external file is prepared after the referenced tracks, so being chosen is not enough to
        // make it first — it has to overtake them, or the wrong subtitle plays.
        var result = BuildWithSubtitleDefault(
            WithSubtitles(),
            SubtitleDefault.External,
            [([new TextCue(0, 1000, "From a file")], "fra")]);

        Assert.Equal(["hvc1", "tx3g", "tx3g"], result.SampleEntries);
        Assert.Equal([true, true, false], Enabled(result));

        // And it is the external one that leads, which its language proves.
        var reader = new Mp4BoxReader(result.Header);
        // mdhd version 1: version and flags, two 64-bit timestamps, the timescale and a 64-bit
        // duration, and then the packed language.
        var languages = reader.Find("moov/trak/mdia/mdhd")
            .Select(box => reader.U32At(box.Start + 32) >> 16)
            .ToList();
        Assert.Equal(0x1A41u, languages[1]);                 // 'fra'
    }

    [Fact]
    public void Each_track_carries_its_own_language_rather_than_undetermined()
    {
        // Six dubs that all read "Undetermined" are barely better than carrying one.
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_AC3", channels: 6, language: "eng"),
                TrackEntry(3, 2, "A_AC3", channels: 2, language: "rus")),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(100, 0x11)),
                SimpleBlock(2, 0, true, Ac3Frame(200)),
                SimpleBlock(3, 0, true, Ac3Frame(200))));

        var built = Build(file, tracks: [1, 2, 3]);
        var languages = built.Reader.Find("moov/trak/mdia/mdhd")
            .Select(box => built.Reader.U32At(box.Start + 32) >> 16)
            .ToList();

        // Packed as three five-bit letters offset from 0x60: und for the video, then eng and rus.
        Assert.Equal([0x55C4u, 0x15C7u, 0x4AB3u], languages);
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

    [Fact]
    public void A_subtitle_from_a_file_beside_the_video_becomes_a_track_of_its_own()
    {
        var file = VideoAndAudio();
        var stream = new MemoryStream(file);
        var index = MatroskaIndexer.Build(stream);

        var result = Mp4Synthesizer.Build(
            [new Mp4Synthesizer.Input(index, stream)],
            [new Mp4Synthesizer.TrackRef(0, 1), new Mp4Synthesizer.TrackRef(0, 2)],
            VideoSignalling.CrossCompatible,
            [([new TextCue(40, 60, "From a sidecar")], "rus")]);

        Assert.NotNull(result);
        Assert.Equal(["hvc1", "ac-3", "tx3g"], result.SampleEntries);

        var reader = new Mp4BoxReader(result.Header);

        // Its samples ride in the header, like every other rewritten cue.
        var offsets = reader.ChunkOffsets(reader.Find("moov/trak/mdia/minf/stbl/co64").Skip(2).First());
        Assert.All(offsets, offset => Assert.InRange(offset, 0ul, (ulong)result.HeaderLength));

        // Cues from a file count in milliseconds.
        var mdhd = reader.Find("moov/trak/mdia/mdhd").Skip(2).First();
        Assert.Equal(1000u, reader.U32At(mdhd.Start + 20));

        Assert.Contains("From a sidecar", System.Text.Encoding.UTF8.GetString(result.Header),
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_sidecar_adds_no_track()
    {
        var file = VideoAndAudio();
        var stream = new MemoryStream(file);
        var index = MatroskaIndexer.Build(stream);

        var result = Mp4Synthesizer.Build(
            [new Mp4Synthesizer.Input(index, stream)],
            [new Mp4Synthesizer.TrackRef(0, 1)],
            VideoSignalling.CrossCompatible,
            [([], "rus")]);

        Assert.NotNull(result);
        Assert.Equal(["hvc1"], result.SampleEntries);
    }

    [Fact]
    public void Eac3_gets_its_own_entry_and_descriptor()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_EAC3", channels: 6)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(10, 0x01)),
                SimpleBlock(2, 0, true, Eac3Frame(640))));

        var built = Build(file);

        Assert.Equal(["hvc1", "ec-3"], built.Result.SampleEntries);

        var stsd = built.Reader.Find("moov/trak/mdia/minf/stbl/stsd").Skip(1).First();
        var entry = built.Reader.SampleEntry(stsd);
        Assert.Equal("ec-3", entry.Type);
        Assert.Equal(6, built.Result.Header[entry.Start + 17]);
        Assert.Equal(48000u, built.Reader.U32At(entry.Start + 24) >> 16);

        var dec3 = built.Reader.Children(entry.Start + 28, entry.End).Single(box => box.Type == "dec3");
        // One independent substream, 48 kHz, bitstream id 16, 3/2 with LFE, nothing dependent.
        Assert.Equal(0, built.Result.Header[dec3.Start + 1] & 0x07);
        Assert.Equal(0x20, built.Result.Header[dec3.Start + 2]);
        Assert.Equal(0x0F, built.Result.Header[dec3.Start + 3]);
    }

    [Fact]
    public void An_eac3_frame_is_timed_by_the_blocks_it_actually_carries()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B, TrackEntry(2, 2, "A_EAC3", channels: 6)),
            Cluster(0, SimpleBlock(2, 0, true, Eac3Frame(640))));

        var built = Build(file, tracks: [2]);

        // Six blocks of 256 samples. AC-3 is always 1536; E-AC-3 is not, and assuming so would drift.
        var stts = built.Reader.Find("moov/trak/mdia/minf/stbl/stts").First();
        Assert.Equal(1536u, built.Reader.Runs(stts)[0].Value);
    }

    [Fact]
    public void A_unit_that_opens_with_a_dependent_substream_is_left_out()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(160),
            ContainerBuilders.Ebml(0x1654AE6B,
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: Hvcc, width: 8, height: 8,
                    defaultDuration: 40_000_000),
                TrackEntry(2, 2, "A_EAC3", channels: 6)),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(10, 0x01)),
                // Stream type 1 is a dependent substream; without its independent one there is nothing
                // to describe the stream against.
                SimpleBlock(2, 0, true, Eac3Frame(640, streamType: 1))));

        Assert.Equal(["hvc1"], Build(file).Result.SampleEntries);
    }
}
