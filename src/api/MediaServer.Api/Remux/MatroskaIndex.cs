namespace MediaServer.Api.Remux;

/// <summary>
/// Where a single frame lives in the source file. This is the whole point of the design: a sample is
/// <em>referenced</em>, never copied, so an MP4 can be computed over a Matroska file that is never touched.
/// </summary>
/// <param name="Timestamp">Presentation time, in the file's own <c>TimestampScale</c> ticks.</param>
/// <param name="Offset">Absolute byte offset of the frame's payload in the source.</param>
internal readonly record struct IndexedSample(long Timestamp, long Offset, int Size, bool IsKeyframe);

internal enum IndexedTrackKind
{
    Other = 0,
    Video = 1,
    Audio = 2,
    Subtitle = 17,
}

/// <summary>
/// One track's sample table, plus everything an MP4 sample entry needs — all of it carried from the source
/// rather than derived. <see cref="CodecPrivate"/> is already the payload of <c>hvcC</c> or <c>avcC</c>, and
/// <see cref="DolbyVisionConfiguration"/> already the payload of <c>dvvC</c>.
/// </summary>
internal sealed class IndexedTrack
{
    /// <summary>The Matroska track number, which is what a block refers to.</summary>
    public required ulong Number { get; init; }

    /// <summary>
    /// Position among the file's track entries, zero-based. This is what a probe calls the stream index
    /// and what the database stores, and it is not the same thing as <see cref="Number"/> — a file may
    /// number its tracks however it likes.
    /// </summary>
    public int Ordinal { get; set; }
    public IndexedTrackKind Kind { get; set; }
    public string CodecId { get; set; } = string.Empty;
    public byte[]? CodecPrivate { get; set; }
    public byte[]? DolbyVisionConfiguration { get; set; }
    public string? Language { get; set; }
    public string? Name { get; set; }

    /// <summary>Nanoseconds per frame as the file states it. Useful, but not to be trusted as a duration —
    /// see <see cref="MatroskaIndexer"/>.</summary>
    public long DefaultDuration { get; set; }

    /// <summary>
    /// Nanoseconds of encoder priming at the head of the stream, which the decoder produces but nobody
    /// should hear. AAC always has it — a whole frame of it, typically — and AC-3 never does. In Matroska
    /// the demuxer is expected to drop it; in MP4 an edit list says so instead.
    /// </summary>
    public long CodecDelay { get; set; }

    public int Width { get; set; }
    public int Height { get; set; }
    public int DisplayWidth { get; set; }
    public int DisplayHeight { get; set; }

    // Colour as the container states it. Often absent: this library's own files carry none at all and keep
    // the information in the HEVC SPS instead, so zero here means "the container did not say".
    public int ColourPrimaries { get; set; }
    public int TransferCharacteristics { get; set; }
    public int MatrixCoefficients { get; set; }
    public bool FullRange { get; set; }

    public double SampleRate { get; set; }
    public int Channels { get; set; }

    public List<IndexedSample> Samples { get; } = [];

    /// <summary>
    /// How long each sample is shown, in timestamp ticks. Only subtitles have one: their cues have a
    /// duration of their own rather than lasting until the next sample, and a track that never stated one
    /// leaves this null rather than carrying a list of zeroes for every frame of a film.
    /// </summary>
    public List<long>? SampleDurations { get; set; }

    /// <summary>How many blocks held more than one frame. Diagnostic: lacing is invisible in test material
    /// produced by ffmpeg, so a zero here on a real file is worth a second look rather than relief.</summary>
    public int LacedBlocks { get; set; }
}

/// <summary>
/// A whole file's sample tables. Megabytes, against a source of gigabytes — which is what makes building
/// this ahead of time acceptable where producing a second copy of the media is not.
/// </summary>
internal sealed class MatroskaIndex
{
    public required long SourceLength { get; init; }

    /// <summary>Nanoseconds per timestamp tick; 1 000 000 (one millisecond) unless the file says otherwise.</summary>
    public long TimestampScale { get; set; } = 1_000_000;

    /// <summary>The duration the file claims, in ticks. Claimed, not measured.</summary>
    public double DurationTicks { get; set; }

    public List<IndexedTrack> Tracks { get; } = [];

    public IndexedTrack? Track(ulong number) => Tracks.FirstOrDefault(track => track.Number == number);
}
