using MediaServer.Api.Remux;
using MediaServer.Api.Tests.Probe;
using static MediaServer.Api.Tests.Remux.RemuxContainerBuilders;

namespace MediaServer.Api.Tests.Remux;

/// <summary>
/// What the walk now keeps so that playback never opens the film.
///
/// The synthesiser used to read every subtitle cue and probe each E-AC-3 track in sixty-four places, on
/// every byte-range request. All of it is fixed when the file is written, and the walk is already
/// passing over those bytes.
/// </summary>
public sealed class IndexCarriesCuesTests
{
    private static MatroskaIndex Index(byte[] file) => MatroskaIndexer.Build(new MemoryStream(file));

    private static byte[] Tracks(params byte[][] entries) => ContainerBuilders.Ebml(0x1654AE6B, entries);

    private static byte[] Text(string value) => System.Text.Encoding.UTF8.GetBytes(value);

    /// <summary>A syntactically valid AC-3 sync frame, which is all the descriptor needs to read.</summary>
    private static byte[] Ac3Frame(int size) =>
        [0x0B, 0x77, 0x00, 0x00, 0x14, 0x40, 0xEB, .. new byte[Math.Max(0, size - 7)]];

    [Fact]
    public void The_walk_keeps_the_converted_text_of_every_cue()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(400),
            Tracks(TrackEntry(3, 17, "S_TEXT/UTF8")),
            Cluster(0,
                BlockGroup(3, 40, false, 60, Text("<i>Hello</i>")),
                BlockGroup(3, 200, false, 50, Text("Later"))));

        var track = Assert.Single(Index(file).Tracks);

        // Converted, not raw: the markup comes off at walk time so playback does no work at all.
        Assert.Equal(["Hello", "Later"], track.CueText);
    }

    [Fact]
    public void A_subtitle_kind_nothing_can_rewrite_keeps_no_text()
    {
        // A bitmap subtitle is never carried, so storing its bytes would be dead weight in every index.
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(400),
            Tracks(TrackEntry(3, 17, "S_HDMV/PGS")),
            Cluster(0, BlockGroup(3, 40, false, 60, Text("nonsense"))));

        Assert.Null(Assert.Single(Index(file).Tracks).CueText);
    }

    [Fact]
    public void The_walk_keeps_the_first_audio_unit()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(400),
            Tracks(TrackEntry(2, 2, "A_AC3", channels: 6)),
            Cluster(0, SimpleBlock(2, 0, true, Ac3Frame(200)), SimpleBlock(2, 32, true, Ac3Frame(200))));

        var track = Assert.Single(Index(file).Tracks);

        Assert.NotNull(track.FirstUnit);
        Assert.Equal(0x0B, track.FirstUnit![0]);
        Assert.Equal(0x77, track.FirstUnit[1]);
    }

    [Fact]
    public void Text_and_offsets_stay_aligned_when_a_cue_is_empty()
    {
        // The lists are index-aligned with the samples. A cue that converts to nothing — markup and no
        // words — still occupies its place, or every cue after it would show at the wrong time.
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(400),
            Tracks(TrackEntry(3, 17, "S_TEXT/UTF8")),
            Cluster(0,
                BlockGroup(3, 40, false, 60, Text("First")),
                BlockGroup(3, 100, false, 60, Text("<i></i>")),
                BlockGroup(3, 200, false, 60, Text("Third"))));

        var track = Assert.Single(Index(file).Tracks);

        Assert.Equal(3, track.Samples.Count);
        Assert.Equal(3, track.CueText!.Count);
        Assert.Equal(string.Empty, track.CueText[1]);
        Assert.Equal("Third", track.CueText[2]);
    }

    [Fact]
    public void What_the_walk_kept_survives_being_written_and_read_back()
    {
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(400),
            Tracks(
                TrackEntry(2, 2, "A_AC3", channels: 6),
                TrackEntry(3, 17, "S_TEXT/UTF8")),
            Cluster(0,
                SimpleBlock(2, 0, true, Ac3Frame(200)),
                BlockGroup(3, 40, false, 60, Text("Hello"))));

        var built = Index(file);
        using var stored = new MemoryStream();
        RemuxIndexFormat.Write(stored, built, new RemuxIndexFormat.Stamp(1, DateTimeOffset.UnixEpoch));

        var loaded = RemuxIndexFormat.Read(new MemoryStream(stored.ToArray()))!.Value.Index;

        Assert.Equal(["Hello"], loaded.Track(3)!.CueText);
        Assert.Equal(built.Track(2)!.FirstUnit, loaded.Track(2)!.FirstUnit);
    }

    [Fact]
    public void A_subtitle_track_is_laid_out_without_the_source_being_readable()
    {
        // The point of the whole change: hand the synthesiser a source it cannot read, and the header
        // still comes out. Before this it read every cue from exactly that stream.
        var file = ContainerBuilders.Matroska(
            ContainerBuilders.Info(400),
            Tracks(
                TrackEntry(1, 1, "V_MPEGH/ISO/HEVC", codecPrivate: [0x01, 0x22, 0x20, 0x00],
                    width: 8, height: 8, defaultDuration: 40_000_000),
                TrackEntry(3, 17, "S_TEXT/UTF8")),
            Cluster(0,
                SimpleBlock(1, 0, true, Frame(10, 0x01)),
                BlockGroup(3, 40, false, 60, Text("Hello"))));

        var index = Index(file);
        var built = Mp4Synthesizer.Build(
            [new Mp4Synthesizer.Input(index, new UnreadableStream(index.SourceLength))],
            [new Mp4Synthesizer.TrackRef(0, 1), new Mp4Synthesizer.TrackRef(0, 3)],
            VideoSignalling.CrossCompatible,
            null,
            SubtitleDefault.Embedded);

        Assert.NotNull(built);
        Assert.Equal(["hvc1", "tx3g"], built.SampleEntries);
    }

    /// <summary>A source that throws if anything asks it for bytes.</summary>
    private sealed class UnreadableStream(long length) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("the synthesiser must not open the film");

        public override long Seek(long offset, SeekOrigin origin) => Position = offset;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
