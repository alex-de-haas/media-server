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
    public required ulong Number { get; init; }
    public IndexedTrackKind Kind { get; set; }
    public string CodecId { get; set; } = string.Empty;
    public byte[]? CodecPrivate { get; set; }
    public byte[]? DolbyVisionConfiguration { get; set; }
    public string? Language { get; set; }
    public string? Name { get; set; }

    /// <summary>Nanoseconds per frame as the file states it. Useful, but not to be trusted as a duration —
    /// see <see cref="MatroskaIndexer"/>.</summary>
    public long DefaultDuration { get; set; }

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
