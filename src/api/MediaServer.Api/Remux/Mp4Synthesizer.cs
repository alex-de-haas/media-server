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

    /// <summary>
    /// One file whose samples may appear in the output. There is more than one when a sidecar is carried:
    /// an external dub is a second file, and its samples join the video's in the same container.
    /// </summary>
    internal sealed record Input(MatroskaIndex Index, Stream Content);

    /// <summary>Which track of which input, in the order the output should carry them.</summary>
    internal readonly record struct TrackRef(int Input, ulong Number);

    /// <param name="Header">Everything before the first wrapped file, including that file's own
    /// <c>mdat</c> header.</param>
    /// <param name="Wrappers">The <c>mdat</c> header of every input after the first. These sit
    /// <em>between</em> the files, so whoever stitches the output has to put them there — the offsets in
    /// the sample tables already count on it.</param>
    internal sealed record Result(
        byte[] Header,
        IReadOnlyList<byte[]> Wrappers,
        long TotalLength,
        IReadOnlyList<string> SampleEntries)
    {
        public long HeaderLength => Header.Length;
    }

    /// <summary>
    /// Builds the header for the given tracks, in the order given — a player takes the first track of each
    /// kind as its default, so the caller's order is the viewer's choice. The inputs are read only for the
    /// few bytes an AC-3 descriptor needs and for subtitle text.
    /// </summary>
    internal static Result? Build(
        IReadOnlyList<Input> inputs,
        IReadOnlyList<TrackRef> tracks,
        VideoSignalling signalling,
        IReadOnlyList<(IReadOnlyList<TextCue> Cues, string? Language)>? externalText = null)
    {
        var prepared = new List<Prepared>();
        // Timed text is rewritten, so it needs somewhere to live; it is small enough to ride in the header.
        var subtitles = new MemoryStream();
        foreach (var reference in tracks)
        {
            if (reference.Input < 0 || reference.Input >= inputs.Count)
            {
                continue;
            }

            var input = inputs[reference.Input];
            if (input.Index.Track(reference.Number) is not { } track || track.Samples.Count == 0)
            {
                continue;
            }

            var one = track.Kind switch
            {
                IndexedTrackKind.Video => PrepareVideo(track, input.Index.TimestampScale, signalling, reference.Input),
                IndexedTrackKind.Audio => PrepareAudio(track, input.Content, reference.Input),
                IndexedTrackKind.Subtitle => PrepareSubtitle(
                    track, input.Index.TimestampScale, input.Content, subtitles),
                _ => null,
            };

            if (one is not null)
            {
                prepared.Add(one);
            }
        }

        foreach (var (cues, language) in externalText ?? [])
        {
            if (PrepareExternalSubtitle(cues, language, subtitles) is { } one)
            {
                prepared.Add(one);
            }
        }

        if (prepared.Count == 0)
        {
            return null;
        }

        // The movie header counts in milliseconds; each track counts in its own units, so the longest is
        // found after converting rather than before.
        var movieDuration = prepared.Max(track =>
            (track.Duration - track.MediaTime) * 1000 / Math.Max(1, track.Timescale));
        var ftyp = Box("ftyp", "isom"u8.ToArray(), U32(0x200),
            "isomiso2mp41hvc1dby1"u8.ToArray());

        // [ftyp][moov][text mdat, when there is timed text][mdat + input 0][mdat + input 1]...
        var text = subtitles.ToArray();
        var textBox = text.Length > 0 ? Box("mdat", text) : [];
        var textPayloadAt = 0L;
        var bases = new long[inputs.Count];

        // Offsets depend on the header's own length, so it is built once to measure and once for real.
        // Every offset field is fixed width, which is what makes the two agree.
        for (var pass = 0; pass < 2; pass++)
        {
            var moovLength = Assemble(prepared, movieDuration, textPayloadAt, bases).Length;
            textPayloadAt = ftyp.Length + moovLength + 8;
            var at = (long)ftyp.Length + moovLength + textBox.Length;
            for (var i = 0; i < inputs.Count; i++)
            {
                at += MdatHeaderLength;
                bases[i] = at;
                at += inputs[i].Index.SourceLength;
            }
        }

        var moov = Assemble(prepared, movieDuration, textPayloadAt, bases);
        if (ftyp.Length + moov.Length + textBox.Length + MdatHeaderLength != bases[0])
        {
            // A header that lies about where the samples are is worse than no header at all.
            return null;
        }

        var wrappers = new List<byte[]>();
        foreach (var input in inputs)
        {
            var mdat = new byte[MdatHeaderLength];
            U32(1).CopyTo(mdat, 0);
            "mdat"u8.CopyTo(mdat.AsSpan(4));
            U64((ulong)(MdatHeaderLength + input.Index.SourceLength)).CopyTo(mdat, 8);
            wrappers.Add(mdat);
        }

        // Only the first wrapper can sit in the header; the rest have to be interleaved with the files
        // they wrap, which the stream does when it stitches the parts together.
        byte[] header = [.. ftyp, .. moov, .. textBox, .. wrappers[0]];
        return new Result(
            header,
            wrappers.Skip(1).ToList(),
            header.Length + inputs.Sum(input => input.Index.SourceLength)
                + ((inputs.Count - 1) * MdatHeaderLength),
            prepared.Select(track => track.SampleEntry).ToList());
    }

    /// <summary>
    /// One output track. <see cref="Placements"/> is where its samples are: for video and audio they are
    /// offsets into the source, for subtitles offsets into the small <c>mdat</c> the header carries,
    /// because a timed-text sample is rewritten rather than pointed at.
    /// </summary>
    private sealed record Prepared(
        IndexedTrack Track,
        string SampleEntry,
        byte[] Entry,
        IReadOnlyList<long> Deltas,
        IReadOnlyList<long>? CompositionOffsets,
        IReadOnlyList<int>? SyncSamples,
        long Duration,
        IReadOnlyList<(long Offset, int Size)> Placements,
        bool InHeader,
        int Input,
        int Timescale,
        /// <summary>Encoder priming to trim, in this track's own units. Zero for everything but AAC.</summary>
        long MediaTime = 0);

    private static Prepared? PrepareVideo(
        IndexedTrack track, long timestampScale, VideoSignalling signalling, int input)
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
        // Time is kept in the source's own ticks. Nanoseconds would be exact too, but a 32-bit sample
        // delta then tops out at 4.29 s — shorter than the gaps a subtitle track routinely has — and an
        // overflow there is a timing table that lies rather than one that fails.
        var timescale = TicksPerSecond(timestampScale);
        var presentation = track.Samples.Select(sample => sample.Timestamp).ToArray();

        // The decode timeline is the presentation timestamps in sorted order. Taking DefaultDuration as a
        // constant instead drifts — on a two-hour film it parted company with the real timestamps by half a
        // minute — so the durations are read from the file rather than assumed.
        var decode = presentation.Order().ToArray();
        var deltas = new long[count];
        for (var i = 0; i < count - 1; i++)
        {
            deltas[i] = decode[i + 1] - decode[i];
        }

        deltas[count - 1] = count > 1
            ? deltas[count - 2]
            : track.DefaultDuration / Math.Max(1, timestampScale);

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

        if (!Representable(deltas) || (reordered && !Representable(composition)))
        {
            return null;
        }

        return new Prepared(
            track,
            entryName,
            VideoEntry(track, entryName, codec.ConfigurationBox),
            deltas,
            reordered ? composition : null,
            // A sync table listing every sample says nothing; its absence already means "all of them".
            sync.Count == count ? null : sync,
            deltas.Sum(),
            [.. track.Samples.Select(sample => (sample.Offset, sample.Size))],
            InHeader: false,
            input,
            timescale);
    }

    private static Prepared? PrepareAudio(IndexedTrack track, Stream source, int input)
    {
        // What can be described lives in one place, so the resolver cannot offer a remux the synthesiser
        // then declines — which produced a file with no sound.
        if (!RemuxCodecs.CanPackageAudio(track))
        {
            return null;
        }

        string entryName;
        byte[] entry;
        int sampleRate;
        long frameSamples;

        // AAC is the one that needs nothing from the bitstream: Matroska carries the AudioSpecificConfig
        // in CodecPrivate, and that is the payload the esds wants. So it is described before the source is
        // touched at all.
        if (track.CodecId == "A_AAC")
        {
            if (track.CodecPrivate is not { } config || DescribeAac(config) is not { } aac)
            {
                return null;
            }

            // Matroska states the priming in nanoseconds and expects the demuxer to drop it. MP4 has no
            // such convention, so it becomes an edit list: without one the soundtrack starts a whole frame
            // late, which measured as 1024 samples — 21 ms — against the picture.
            //
            // Rounded, not truncated. A whole frame of priming at 48 kHz is 21333333.33 ns and the
            // container can only store whole nanoseconds, so truncating the conversion back gives 1023
            // samples and leaves the track one sample late — which is exactly what the round trip showed.
            const long Nanosecond = 1_000_000_000;
            var priming =
                ((Math.Max(0, track.CodecDelay) * aac.SampleRate) + (Nanosecond / 2)) / Nanosecond;

            return new Prepared(
                track, "mp4a", AudioEntry("mp4a", aac.Esds, aac.SampleRate, aac.Channels),
                Enumerable.Repeat((long)aac.SamplesPerFrame, track.Samples.Count).ToArray(),
                null, null, (long)aac.SamplesPerFrame * track.Samples.Count,
                [.. track.Samples.Select(sample => (sample.Offset, sample.Size))],
                InHeader: false,
                input,
                aac.SampleRate,
                MediaTime: priming);
        }

        var first = track.Samples[0];
        // Enough of the first access unit to read it: AC-3 needs a sync frame header, E-AC-3 needs to walk
        // its substreams, and both are small next to the frame itself.
        var probe = new byte[Math.Min(first.Size, 4096)];
        source.Position = first.Offset;
        source.ReadExactly(probe);

        if (track.CodecId == "A_EAC3")
        {
            if (DescribeEac3(probe) is not { } eac3)
            {
                return null;
            }

            entryName = "ec-3";
            entry = AudioEntry("ec-3", Box("dec3", eac3.Dec3), eac3.SampleRate, eac3.Channels);
            sampleRate = eac3.SampleRate;
            // Not always 1536: an E-AC-3 frame carries one, two, three or six blocks of 256 samples, and
            // nothing forbids a stream from varying it. Every frame is not read — that would be a seek per
            // sample on every request — but enough of them are read to know the answer is the same
            // throughout. A stream that varies is refused rather than given a timeline built on the
            // first frame, which would drift for the whole of its length.
            if (!SameThroughout(track, source, eac3.SamplesPerFrame))
            {
                return null;
            }

            frameSamples = eac3.SamplesPerFrame;
        }
        else
        {
            if (DescribeAc3(probe) is not { } ac3)
            {
                return null;
            }

            entryName = "ac-3";
            entry = AudioEntry("ac-3", Box("dac3", ac3.Dac3), ac3.SampleRate, ac3.Channels);
            sampleRate = ac3.SampleRate;
            // AC-3 is 1536 samples a frame, always. Counting in the stream's own sample rate makes that
            // exact at any rate, and does not depend on the per-frame timestamps a laced block cannot give.
            frameSamples = 1536;
        }

        var count = track.Samples.Count;
        var deltas = Enumerable.Repeat(frameSamples, count).ToArray();

        return new Prepared(
            track, entryName, entry, deltas, null, null, frameSamples * count,
            [.. track.Samples.Select(sample => (sample.Offset, sample.Size))],
            InHeader: false,
            input,
            sampleRate);
    }

    /// <summary>
    /// Rewrites a text subtitle track as <c>tx3g</c>. Unlike video and audio, none of this can be pointed
    /// at: a timed-text sample is a length-prefixed string, the markup has to come off, and the gaps
    /// between cues need empty samples that exist nowhere in the source. So the bytes are produced here
    /// and carried in the header's own <c>mdat</c> — a film's worth of dialogue is a hundred kilobytes or
    /// so, against a source of gigabytes.
    /// </summary>
    private static Prepared? PrepareSubtitle(
        IndexedTrack track, long timestampScale, Stream source, MemoryStream text)
    {
        if (!SubtitleText.IsConvertible(track.CodecId) || track.SampleDurations is not { } durations)
        {
            // Without a duration a cue has no end, and MP4 has no way to say "until the next one".
            return null;
        }

        var buffer = new byte[4096];
        var cues = new List<TextCue>(track.Samples.Count);
        for (var i = 0; i < track.Samples.Count; i++)
        {
            var sample = track.Samples[i];
            if (durations[i] <= 0)
            {
                continue;
            }

            if (buffer.Length < sample.Size)
            {
                buffer = new byte[sample.Size];
            }

            source.Position = sample.Offset;
            source.ReadExactly(buffer, 0, sample.Size);
            cues.Add(new TextCue(
                sample.Timestamp,
                durations[i],
                SubtitleText.Convert(buffer.AsSpan(0, sample.Size), track.CodecId)));
        }

        return PrepareText(track, cues, TicksPerSecond(timestampScale), text);
    }

    /// <summary>
    /// The same track, from a file beside the video rather than from inside it. A sidecar subtitle has no
    /// index and needs none — it is parsed per request — so it joins the embedded path here, once both are
    /// simply a list of cues.
    /// </summary>
    private static Prepared? PrepareExternalSubtitle(
        IReadOnlyList<TextCue> cues, string? language, MemoryStream text)
    {
        var track = new IndexedTrack
        {
            Number = 0,
            Kind = IndexedTrackKind.Subtitle,
            CodecId = "S_TEXT/UTF8",
            Language = language,
        };

        // Cues from a file are in milliseconds, which is a timescale of a thousand.
        return PrepareText(track, cues, 1000, text);
    }

    /// <summary>
    /// Lays cues onto a timeline: an empty sample wherever nothing is on screen, because timed text has no
    /// way to express a gap other than by saying nothing in it.
    /// </summary>
    private static Prepared? PrepareText(
        IndexedTrack track, IReadOnlyList<TextCue> cues, int timescale, MemoryStream text)
    {
        var placements = new List<(long Offset, int Size)>();
        var deltas = new List<long>();
        var cursor = 0L;

        foreach (var cue in cues.OrderBy(cue => cue.Start))
        {
            if (cue.Duration <= 0)
            {
                continue;
            }

            var start = cue.Start;
            if (start > cursor)
            {
                placements.Add((Place(text, []), 2));
                deltas.Add(start - cursor);
            }
            else if (start < cursor)
            {
                // Overlapping cues cannot both be shown by a track that carries one sample at a time; the
                // later one starts where the earlier ended rather than being dropped.
                start = cursor;
            }

            var encoded = System.Text.Encoding.UTF8.GetBytes(cue.Text);
            placements.Add((Place(text, encoded), 2 + encoded.Length));
            deltas.Add(cue.Duration);
            cursor = start + cue.Duration;
        }

        if (placements.Count == 0 || !Representable(deltas))
        {
            return null;
        }

        return new Prepared(
            track, "tx3g", TextEntry(), deltas, null, null, deltas.Sum(), placements,
            InHeader: true, Input: 0, Timescale: timescale);
    }

    /// <summary>Appends one timed-text sample — a 16-bit length then the text — and reports where it went.</summary>
    private static long Place(MemoryStream text, byte[] encoded)
    {
        var at = text.Position;
        text.Write(U16((ushort)encoded.Length));
        text.Write(encoded);
        return at;
    }

    private static byte[] TextEntry()
    {
        byte[] body =
        [
            .. new byte[6], .. U16(1),                          // reserved, data reference index
            .. U32(0),                                          // display flags
            0x01, 0xFF,                                         // horizontal centred, vertical bottom
            0x00, 0x00, 0x00, 0x00,                             // transparent background
            .. new byte[8],                                     // box record: the whole frame
            .. new byte[8],                                     // style record: defaults
            .. U32(0x00FFFFFF), 0xFF,                           // white text
        ];

        // A font table is required even when it says only "use something ordinary".
        var ftab = Box("ftab", U16(1), U16(1), [5], "Serif"u8.ToArray());
        return Box("tx3g", body, ftab);
    }

    /// <summary>
    /// The source's ticks per second. Matroska states nanoseconds per tick, and one millisecond is the
    /// usual answer, which makes this 1000.
    /// </summary>
    private static int TicksPerSecond(long timestampScale) =>
        timestampScale > 0 ? (int)Math.Max(1, 1_000_000_000L / timestampScale) : 1000;

    /// <summary>
    /// Whether every value fits the fixed-width field it is about to be written into. A table that
    /// overflows is a table that lies about time, and a track left out is easier to notice than one that
    /// plays at the wrong speed.
    /// </summary>
    private static bool Representable(IReadOnlyList<long> values) =>
        values.All(value => value is >= int.MinValue and <= uint.MaxValue);

    /// <summary>
    /// Whether every E-AC-3 frame carries as many blocks as the first, checked over a spread of the track
    /// rather than over all of it: a constant answer is what every real stream gives, and a seek per
    /// sample on every playback request is not worth paying to confirm it.
    /// </summary>
    private static bool SameThroughout(IndexedTrack track, Stream source, int expected)
    {
        const int Probes = 64;
        var step = Math.Max(1, track.Samples.Count / Probes);
        var buffer = new byte[64];

        for (var i = 0; i < track.Samples.Count; i += step)
        {
            var sample = track.Samples[i];
            var length = Math.Min(buffer.Length, sample.Size);
            source.Position = sample.Offset;
            source.ReadExactly(buffer, 0, length);

            if (DescribeEac3(buffer.AsSpan(0, length)) is not { } frame || frame.SamplesPerFrame != expected)
            {
                return false;
            }
        }

        return true;
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

    /// <param name="descriptor">The codec's own box, already assembled — <c>dac3</c>, <c>dec3</c> or
    /// <c>esds</c>. The last is a full box while the others are plain, which is why it arrives built
    /// rather than as a name and a payload.</param>
    private static byte[] AudioEntry(string entryName, byte[] descriptor, int sampleRate, int channels)
    {
        byte[] body =
        [
            .. new byte[6], .. U16(1),
            .. new byte[8],
            .. U16((ushort)channels), .. U16(16),
            .. new byte[4],
            .. U32((uint)sampleRate << 16),
        ];

        return Box(entryName, body, descriptor);
    }

    private static byte[] Assemble(
        IReadOnlyList<Prepared> tracks, long movieDuration, long textBase, IReadOnlyList<long> bases)
    {
        var traks = new List<byte[]>();
        var seen = new HashSet<IndexedTrackKind>();
        for (var i = 0; i < tracks.Count; i++)
        {
            // Rewritten text lives in the header; everything else lives in the file it came from.
            var at = tracks[i].InHeader ? textBase : bases[tracks[i].Input];
            // The caller's order is the viewer's choice, so the first track of each kind is the default.
            traks.Add(Trak(tracks[i], i + 1, movieDuration, at, first: seen.Add(tracks[i].Track.Kind)));
        }

        var mvhd = Full("mvhd", 1, 0,
            U64(0), U64(0), U32(1000), U64((ulong)movieDuration),
            U32(0x00010000), U16(0x0100), new byte[10],
            UnityMatrix(), new byte[24], U32((uint)tracks.Count + 1));

        return Box("moov", [mvhd, .. traks]);
    }

    /// <param name="first">Whether this is the first track of its kind in the caller's order, which is
    /// the viewer's choice. It decides two things a player reads: the <c>enabled</c> flag, and — for
    /// subtitles — whether anything appears on screen unbidden.</param>
    private static byte[] Trak(
        Prepared prepared, int id, long movieDuration, long sampleBase, bool first)
    {
        var track = prepared.Track;
        var isVideo = track.Kind == IndexedTrackKind.Video;
        var isText = track.Kind == IndexedTrackKind.Subtitle;

        // Bit 0 is "enabled", bit 1 "in movie". Every track is in the movie; only the default of its kind
        // is enabled. A second audio track marked enabled leaves the default ambiguous, and an enabled
        // subtitle track puts words on screen for a viewer who never asked for any — which is why
        // subtitles used to be left out of the container altogether rather than carried unselected.
        var flags = first ? 3u : 2u;

        // Tracks of one kind are alternatives to each other, not additions. Players group by media
        // characteristic anyway, but saying so is what makes the grouping the file's claim rather than
        // the player's inference.
        var alternateGroup = isVideo ? 0 : isText ? 2 : 1;

        var tkhd = Full("tkhd", 1, flags,
            U64(0), U64(0), U32((uint)id), new byte[4],
            U64((ulong)movieDuration), new byte[8],
            U16(0), U16((ushort)alternateGroup),
            U16((ushort)(isVideo || isText ? 0 : 0x0100)), new byte[2],
            UnityMatrix(),
            U32(isVideo ? (uint)(track.DisplayWidth > 0 ? track.DisplayWidth : track.Width) << 16 : 0),
            U32(isVideo ? (uint)(track.DisplayHeight > 0 ? track.DisplayHeight : track.Height) << 16 : 0));

        var mdhd = Full("mdhd", 1, 0,
            U64(0), U64(0), U32((uint)prepared.Timescale), U64((ulong)prepared.Duration),
            U16(0x55C4), U16(0));                               // 'und'

        var handler = isVideo ? "vide"u8.ToArray() : isText ? "text"u8.ToArray() : "soun"u8.ToArray();
        var hdlr = Full("hdlr", 0, 0, new byte[4], handler, new byte[12], [.. "MediaServer"u8, 0]);

        var mediaHeader = isVideo
            ? Box("vmhd", [0x00, 0x00, 0x00, 0x01], new byte[8])
            : isText
                ? Box("nmhd", new byte[4])
                : Box("smhd", new byte[8]);

        var dinf = Box("dinf", Full("dref", 0, 0, U32(1), Full("url ", 0, 1)));
        var minf = Box("minf", mediaHeader, dinf, Stbl(prepared, sampleBase));

        // An edit list only appears where something has to be skipped, which is the AAC priming and
        // nothing else. Writing one that starts at zero would say the same as writing none at all.
        byte[] edts = prepared.MediaTime > 0
            ? Box("edts", Full("elst", 0, 0,
                U32(1),
                U32((uint)Math.Max(0,
                    (prepared.Duration - prepared.MediaTime) * 1000 / Math.Max(1, prepared.Timescale))),
                I32((int)prepared.MediaTime),
                U32(0x00010000)))                   // rate 1.0
            : [];

        return Box("trak", tkhd, edts, Box("mdia", mdhd, hdlr, minf));
    }

    private static byte[] Stbl(Prepared prepared, long sampleBase)
    {
        var samples = prepared.Placements;
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
            [.. samples.SelectMany(sample => U64((ulong)(sample.Offset + sampleBase)))]));

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
