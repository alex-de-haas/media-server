using static MediaServer.Api.Remux.Mp4Writer;

namespace MediaServer.Api.Remux;

/// <summary>Which sample entry a video track is given, which decides whether a client engages Dolby Vision.</summary>
internal enum VideoSignalling
{
    /// <summary>The cross-compatible form: a player reads it as HDR10 even when the RPU is present.</summary>
    CrossCompatible,

    /// <summary>Dolby Vision proper. Only for a client that reported support for it.</summary>
    DolbyVision,
}

/// <summary>
/// Computes an MP4 header whose samples live in an untouched Matroska file.
///
/// <code>
/// [ftyp][moov][mdat header][ ...the whole .mkv, byte for byte... ]
/// </code>
///
/// An <c>mdat</c> is an opaque blob, so it can wrap the entire source and the sample table can point at
/// payload positions inside it. An output offset is then the header's length plus the source offset, and
/// answering a byte range becomes reading the same range from the source. Nothing is repackaged, nothing is
/// stored, and the Matroska framing bytes inside <c>mdat</c> are never referenced by any sample.
///
/// The header is built twice: once to learn its own length, once with offsets that account for it. The
/// second pass is the same length as the first because every offset field is fixed width.
///
/// See <c>docs/features/remux-streaming/plan.md</c>, and <c>scripts/remux-prototype/</c> for the
/// measurements this was built from.
/// </summary>
internal static class Mp4Synthesizer
{
    /// <summary>A 64-bit <c>mdat</c>: <c>size=1</c>, the type, then the real length.</summary>
    private const int MdatHeaderLength = 16;

    internal sealed record Result(byte[] Header, long TotalLength, IReadOnlyList<string> SampleEntries)
    {
        public long HeaderLength => Header.Length;
    }

    /// <summary>
    /// Builds the header for the given tracks, in the order given — the first video track and the first
    /// audio track are what a player picks by default, so the caller's order is the viewer's choice.
    /// <paramref name="source"/> is read only for the few bytes an AC-3 descriptor needs.
    /// </summary>
    internal static Result? Build(
        MatroskaIndex index,
        IReadOnlyList<ulong> trackNumbers,
        VideoSignalling signalling,
        Stream source)
    {
        var prepared = new List<Prepared>();
        foreach (var number in trackNumbers)
        {
            if (index.Track(number) is not { } track || track.Samples.Count == 0)
            {
                continue;
            }

            var one = track.Kind switch
            {
                IndexedTrackKind.Video => PrepareVideo(track, index.TimestampScale, signalling),
                IndexedTrackKind.Audio => PrepareAudio(track, source),
                _ => null,
            };

            if (one is not null)
            {
                prepared.Add(one);
            }
        }

        if (prepared.Count == 0)
        {
            return null;
        }

        var movieDuration = prepared.Max(track => track.Duration);
        var ftyp = Box("ftyp", "isom"u8.ToArray(), U32(0x200),
            "isomiso2mp41hvc1dby1"u8.ToArray());

        var provisional = Assemble(prepared, movieDuration, 0);
        var headerLength = ftyp.Length + provisional.Length + MdatHeaderLength;
        var moov = Assemble(prepared, movieDuration, headerLength);
        if (moov.Length != provisional.Length)
        {
            // Fixed-width offsets are what makes the two passes agree; if they ever do not, a header that
            // lies about where the samples are is worse than no header.
            return null;
        }

        var mdat = new byte[MdatHeaderLength];
        U32(1).CopyTo(mdat, 0);
        "mdat"u8.CopyTo(mdat.AsSpan(4));
        U64((ulong)(MdatHeaderLength + index.SourceLength)).CopyTo(mdat, 8);

        byte[] header = [.. ftyp, .. moov, .. mdat];
        return new Result(
            header,
            header.Length + index.SourceLength,
            prepared.Select(track => track.SampleEntry).ToList());
    }

    private sealed record Prepared(
        IndexedTrack Track,
        string SampleEntry,
        byte[] Entry,
        IReadOnlyList<long> Deltas,
        IReadOnlyList<long>? CompositionOffsets,
        IReadOnlyList<int>? SyncSamples,
        long Duration);

    private static Prepared? PrepareVideo(IndexedTrack track, long timestampScale, VideoSignalling signalling)
    {
        if (VideoCodec(track.CodecId) is not { } codec || track.CodecPrivate is null)
        {
            return null;
        }

        var entryName = codec.SampleEntry;
        if (signalling == VideoSignalling.DolbyVision
            && codec.ConfigurationBox == "hvcC"
            && track.DolbyVisionConfiguration is not null)
        {
            // Only HEVC carries Dolby Vision, and only a track that came with a configuration can claim it.
            entryName = "dvh1";
        }

        var count = track.Samples.Count;
        var presentation = track.Samples.Select(sample => sample.Timestamp * timestampScale).ToArray();

        // The decode timeline is the presentation timestamps in sorted order. Taking DefaultDuration as a
        // constant instead drifts — on a two-hour film it parted company with the real timestamps by half a
        // minute — so the durations are read from the file rather than assumed.
        var decode = presentation.Order().ToArray();
        var deltas = new long[count];
        for (var i = 0; i < count - 1; i++)
        {
            deltas[i] = decode[i + 1] - decode[i];
        }

        deltas[count - 1] = count > 1 ? deltas[count - 2] : track.DefaultDuration;

        var composition = new long[count];
        var reordered = false;
        for (var i = 0; i < count; i++)
        {
            composition[i] = presentation[i] - decode[i];
            reordered |= composition[i] != 0;
        }

        var sync = new List<int>();
        for (var i = 0; i < count; i++)
        {
            if (track.Samples[i].IsKeyframe)
            {
                sync.Add(i + 1);                    // sample numbers are one-based
            }
        }

        return new Prepared(
            track,
            entryName,
            VideoEntry(track, entryName, codec.ConfigurationBox),
            deltas,
            reordered ? composition : null,
            // A sync table listing every sample says nothing; its absence already means "all of them".
            sync.Count == count ? null : sync,
            deltas.Sum());
    }

    private static Prepared? PrepareAudio(IndexedTrack track, Stream source)
    {
        if (track.CodecId is not ("A_AC3" or "A_EAC3"))
        {
            return null;
        }

        var first = track.Samples[0];
        var probe = new byte[Math.Min(16, first.Size)];
        source.Position = first.Offset;
        source.ReadExactly(probe);
        if (DescribeAc3(probe) is not { } ac3)
        {
            return null;
        }

        // AC-3 is 1536 samples a frame, always, so the timing is exact — and it does not depend on the
        // per-frame timestamps a laced block cannot give.
        var duration = 1536L * Timescale / ac3.SampleRate;
        var count = track.Samples.Count;
        var deltas = Enumerable.Repeat(duration, count).ToArray();

        return new Prepared(
            track, "ac-3", AudioEntry(ac3), deltas, null, null, duration * count);
    }

    private static byte[] VideoEntry(IndexedTrack track, string entryName, string configurationBox)
    {
        var extras = new List<byte[]> { Box(configurationBox, track.CodecPrivate!) };

        if (track.TransferCharacteristics > 0 || track.ColourPrimaries > 0)
        {
            // Without colr the format description reports no transfer function at all. Often the container
            // does not state one — this library's own files keep it in the HEVC SPS — and then it is left
            // out rather than guessed.
            extras.Add(Box("colr",
                "nclx"u8.ToArray(),
                U16((ushort)track.ColourPrimaries),
                U16((ushort)track.TransferCharacteristics),
                U16((ushort)track.MatrixCoefficients),
                [(byte)(track.FullRange ? 0x80 : 0x00)]));
        }

        if (track.DolbyVisionConfiguration is not null)
        {
            extras.Add(Box("dvvC", track.DolbyVisionConfiguration));
        }

        byte[] body =
        [
            .. new byte[6], .. U16(1),                          // reserved, data reference index
            .. new byte[16],
            .. U16((ushort)track.Width), .. U16((ushort)track.Height),
            .. U32(0x00480000), .. U32(0x00480000),             // 72 dpi, as everything writes
            .. new byte[4],
            .. U16(1),                                          // frame count
            .. new byte[32],                                    // compressor name
            .. U16(0x0018),                                     // depth
            .. new byte[] { 0xFF, 0xFF },
        ];

        return Box(entryName, [body, .. extras]);
    }

    private static byte[] AudioEntry(Ac3Description ac3)
    {
        byte[] body =
        [
            .. new byte[6], .. U16(1),
            .. new byte[8],
            .. U16((ushort)ac3.Channels), .. U16(16),
            .. new byte[4],
            .. U32((uint)ac3.SampleRate << 16),
        ];

        return Box("ac-3", body, Box("dac3", ac3.Dac3));
    }

    private static byte[] Assemble(IReadOnlyList<Prepared> tracks, long movieDuration, long mediaBase)
    {
        var traks = new List<byte[]>();
        for (var i = 0; i < tracks.Count; i++)
        {
            traks.Add(Trak(tracks[i], i + 1, movieDuration, mediaBase));
        }

        var mvhd = Full("mvhd", 1, 0,
            U64(0), U64(0), U32(1000), U64((ulong)(movieDuration / 1_000_000)),
            U32(0x00010000), U16(0x0100), new byte[10],
            UnityMatrix(), new byte[24], U32((uint)tracks.Count + 1));

        return Box("moov", [mvhd, .. traks]);
    }

    private static byte[] Trak(Prepared prepared, int id, long movieDuration, long mediaBase)
    {
        var track = prepared.Track;
        var isVideo = track.Kind == IndexedTrackKind.Video;

        var tkhd = Full("tkhd", 1, 3,
            U64(0), U64(0), U32((uint)id), new byte[4],
            U64((ulong)(movieDuration / 1_000_000)), new byte[8],
            U16(0), U16(0), U16(0), new byte[2],
            UnityMatrix(),
            U32(isVideo ? (uint)(track.DisplayWidth > 0 ? track.DisplayWidth : track.Width) << 16 : 0),
            U32(isVideo ? (uint)(track.DisplayHeight > 0 ? track.DisplayHeight : track.Height) << 16 : 0));

        var mdhd = Full("mdhd", 1, 0,
            U64(0), U64(0), U32(Timescale), U64((ulong)prepared.Duration),
            U16(0x55C4), U16(0));                               // 'und'

        var hdlr = Full("hdlr", 0, 0,
            new byte[4],
            isVideo ? "vide"u8.ToArray() : "soun"u8.ToArray(),
            new byte[12],
            [.. "MediaServer"u8, 0]);

        var mediaHeader = isVideo
            ? Box("vmhd", [0x00, 0x00, 0x00, 0x01], new byte[8])
            : Box("smhd", new byte[8]);

        var dinf = Box("dinf", Full("dref", 0, 0, U32(1), Full("url ", 0, 1)));
        var minf = Box("minf", mediaHeader, dinf, Stbl(prepared, mediaBase));

        return Box("trak", tkhd, Box("mdia", mdhd, hdlr, minf));
    }

    private static byte[] Stbl(Prepared prepared, long mediaBase)
    {
        var samples = prepared.Track.Samples;
        var parts = new List<byte[]>
        {
            Full("stsd", 0, 0, U32(1), prepared.Entry),
            RunLength("stts", prepared.Deltas),
        };

        if (prepared.CompositionOffsets is { } composition)
        {
            // Version 1 so a negative offset is legal: a frame stored before the one it follows on screen
            // has a composition time earlier than its decode time.
            parts.Add(RunLength("ctts", composition, version: 1));
        }

        if (prepared.SyncSamples is { } sync)
        {
            parts.Add(Full("stss", 0, 0, U32((uint)sync.Count),
                [.. sync.SelectMany(number => U32((uint)number))]));
        }

        // One sample per chunk keeps the mapping trivial: co64 is then simply the sample offsets, and no
        // interleaving decision has to be made about a file that is already interleaved.
        parts.Add(Full("stsc", 0, 0, U32(1), U32(1), U32(1), U32(1)));
        parts.Add(Full("stsz", 0, 0, U32(0), U32((uint)samples.Count),
            [.. samples.SelectMany(sample => U32((uint)sample.Size))]));
        parts.Add(Full("co64", 0, 0, U32((uint)samples.Count),
            [.. samples.SelectMany(sample => U64((ulong)(sample.Offset + mediaBase)))]));

        return Box("stbl", [.. parts]);
    }

    /// <summary>
    /// A run-length table, which is what makes these boxes small: constant frame rates and constant audio
    /// frame durations collapse to a single entry.
    /// </summary>
    private static byte[] RunLength(string type, IReadOnlyList<long> values, byte version = 0)
    {
        var runs = new List<(uint Count, long Value)>();
        foreach (var value in values)
        {
            if (runs.Count > 0 && runs[^1].Value == value)
            {
                runs[^1] = (runs[^1].Count + 1, value);
            }
            else
            {
                runs.Add((1, value));
            }
        }

        var body = new List<byte[]> { U32((uint)runs.Count) };
        foreach (var (count, value) in runs)
        {
            body.Add(U32(count));
            body.Add(version == 0 ? U32((uint)value) : I32((int)value));
        }

        return Full(type, version, 0, [.. body]);
    }
}
