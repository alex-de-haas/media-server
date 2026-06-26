namespace MediaServer.Api.Data;

/// <summary>
/// A re-encode of one movie <see cref="MediaSource"/> into a smaller sibling file, run by the external
/// transcode-engine app. Only durable facts and <see cref="State"/> transitions are persisted; live
/// progress (fps/speed/eta) stays in the engine and is merged into the list snapshot. The output lands in
/// the catalog as a new version of the same movie, which the operator can verify before deleting the
/// original (the "shrink and replace" flow). Scoped to movies for now.
/// </summary>
public sealed class TranscodeJob
{
    public Guid Id { get; set; }

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

    /// <summary>Catalog-root-relative path of the output file (a sibling of the input).</summary>
    public required string OutputPath { get; set; }

    /// <summary><c>h264</c> or <c>hevc</c>.</summary>
    public required string VideoCodec { get; set; }

    /// <summary><c>auto</c>, <c>vaapi</c>, or <c>none</c>.</summary>
    public required string HardwareAcceleration { get; set; }

    /// <summary>Software-encoder quality (0–51); null for hardware.</summary>
    public int? Crf { get; set; }

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
