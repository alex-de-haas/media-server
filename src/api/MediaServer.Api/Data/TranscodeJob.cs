namespace MediaServer.Api.Data;

/// <summary>
/// One job on the external transcode-engine app, reading a movie <see cref="MediaSource"/>. Only durable
/// facts and <see cref="State"/> transitions are persisted; live progress (fps/speed/eta) stays in the engine
/// and is merged into the list snapshot. Scoped to movies for now.
/// <para>
/// <see cref="Kind"/> decides what completion means. A <see cref="TranscodeJobKind.Convert"/> writes one file
/// that lands in the catalog as a new version of the same movie, which the operator can verify before
/// deleting the original (the "shrink and replace" flow). A <see cref="TranscodeJobKind.Extract"/> writes one
/// file per track into <see cref="Outputs"/>, which become external streams of the source it read — the
/// source itself is never rewritten.
/// </para>
/// </summary>
public sealed class TranscodeJob
{
    public Guid Id { get; set; }

    /// <summary>Whether this job composes a new version or writes the source's tracks out beside it.</summary>
    public TranscodeJobKind Kind { get; set; }

    /// <summary>The job id returned by the transcode-engine; the key used to reconcile engine events.</summary>
    public required string EngineJobId { get; set; }

    /// <summary>The source being re-encoded.</summary>
    public Guid MediaSourceId { get; set; }

    /// <summary>The movie the source belongs to (denormalized so the job list can group by item).</summary>
    public Guid MediaItemId { get; set; }

    public Guid CatalogId { get; set; }

    /// <summary>Output file name (the version label shown once it is picked up as a new source).</summary>
    public string? Name { get; set; }

    /// <summary>Catalog-root-relative path of the input source file.</summary>
    public required string InputPath { get; set; }

    /// <summary>Catalog-root-relative path of the output file (a sibling of the input). Null for an
    /// extraction, which composes no single output — its files are in <see cref="Outputs"/>.</summary>
    public string? OutputPath { get; set; }

    /// <summary>The files an extraction produces, one per extracted track. Empty for a conversion.
    /// <para>
    /// Their names are fixed here at submit time because the engine writes them, so they cannot be recomputed
    /// when the job completes — and the language and title have to be carried rather than re-read, because a
    /// <c>.srt</c> has nowhere to hold them.
    /// </para></summary>
    public List<TranscodeJobOutput> Outputs { get; set; } = [];

    /// <summary><c>h264</c> or <c>hevc</c>.</summary>
    public required string VideoCodec { get; set; }

    /// <summary><c>auto</c>, <c>vaapi</c>, or <c>none</c>.</summary>
    public required string HardwareAcceleration { get; set; }

    /// <summary>The quality level asked for (<c>highest</c>/<c>high</c>/<c>balanced</c>/<c>small</c>).
    /// Stored rather than the CRF it resolved to, because the number depends on which encoder the host
    /// reached and the level is what the operator actually chose.
    /// <para>
    /// Null means the video was copied — or, for a job predating levels, that it ran on the encoder's own
    /// default. The migration that introduced this column mapped the CRFs it could and left the rest null
    /// rather than crediting them with a level nobody chose.
    /// </para></summary>
    public string? QualityLevel { get; set; }

    /// <summary>How many audio tracks this job re-encoded rather than copied, so a finished job can explain
    /// where its size went without keeping a row per track.</summary>
    public int ReEncodedAudioTracks { get; set; }

    /// <summary><c>toProfile81</c> when this video copy also rewrote the source's Dolby Vision from the
    /// dual-layer profile 7 to the single-layer 8.1; null otherwise. Stored so the imported version can be
    /// named for what the job did — the output's own record says profile 8, which a plain copy of a profile
    /// 8 source says too.</summary>
    public string? DolbyVision { get; set; }

    public TranscodeJobState State { get; set; }

    /// <summary>Last broadcast progress; live value is the engine snapshot when available.</summary>
    public double PercentComplete { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public MediaSource? MediaSource { get; set; }

    public MediaItem? MediaItem { get; set; }

    public Catalog? Catalog { get; set; }
}

/// <summary>
/// One file an extraction writes — a single track of the source, on its own, beside it.
/// <para>
/// <see cref="Language"/> and <see cref="Title"/> are stored rather than read back from the produced file
/// because the file cannot always hold them: a <c>.mka</c> carries its own tags, but a <c>.srt</c> has
/// nowhere to put one, and the label a track was extracted under would be lost between submitting the job
/// and importing its result.
/// </para>
/// </summary>
public sealed class TranscodeJobOutput
{
    public Guid Id { get; set; }

    public Guid TranscodeJobId { get; set; }

    /// <summary>The absolute index, in the source container, of the track this file holds.</summary>
    public int SourceStreamIndex { get; set; }

    /// <summary>Catalog-root-relative path of the file, beside the video it came out of.</summary>
    public required string RelativePath { get; set; }

    public StreamType StreamType { get; set; }

    public string? Language { get; set; }

    public string? Title { get; set; }

    public TranscodeJob? TranscodeJob { get; set; }
}
