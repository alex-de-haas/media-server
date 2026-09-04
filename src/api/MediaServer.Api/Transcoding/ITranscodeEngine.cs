namespace MediaServer.Api.Transcoding;

/// <summary>A transcode request expressed in catalog terms: the input and output are each a mount
/// <see cref="InputMountLabel"/>/<see cref="OutputMountLabel"/> plus a path relative to that mount root.
/// The engine resolves them against its own media root with the same label (the same host path), so the job
/// reads and writes on the same filesystem as the catalog. <see cref="OutputMountLabel"/> defaults to
/// <see cref="InputMountLabel"/> when null.</summary>
public sealed record TranscodeJobRequest(
    string? InputMountLabel,
    string InputRelativePath,
    string? OutputMountLabel,
    string? OutputRelativePath,
    string VideoCodec,
    string HardwareAcceleration,
    string? QualityLevel,
    int? MaxHeight = null,
    IReadOnlyList<int>? AudioStreamIndexes = null,
    IReadOnlyList<int>? SubtitleStreamIndexes = null,
    int? DefaultAudioStreamIndex = null,
    int? DefaultSubtitleStreamIndex = null,
    IReadOnlyList<EngineAdditionalInput>? AdditionalInputs = null,
    IReadOnlyList<EngineMetadataOverride>? MetadataOverrides = null,
    IReadOnlyList<EngineAudioTarget>? AudioTargets = null,
    IReadOnlyList<EngineExtractionOutput>? Outputs = null,
    /// <summary><c>toProfile81</c> asks the engine to rewrite the copied picture's Dolby Vision from the
    /// dual-layer profile 7 to single-layer 8.1; null keeps it as it is.</summary>
    string? DolbyVision = null);

/// <summary>What the engine has beyond ffmpeg. <see cref="DolbyVisionConversion"/> is whether it carries the
/// tools a profile 7 → 8.1 rewrite runs on (<c>dovi_tool</c> and MKVToolNix); a consumer offers the option
/// only when it does, because an engine without them refuses the job rather than copying silently.</summary>
public sealed record TranscodeTooling(bool DolbyVisionConversion)
{
    public static readonly TranscodeTooling None = new(false);
}

/// <summary>
/// One stream of the input written out as its own file — the inverse of an
/// <see cref="EngineAdditionalInput"/>. Naming any makes the job an <b>extraction</b>: it composes no output
/// at all, so <see cref="TranscodeJobRequest.OutputRelativePath"/> is null and every field describing a
/// composed output must be left unset.
/// <para>
/// <see cref="StreamIndex"/> is a single absolute index in the input, and the language and title travel here
/// rather than as an <see cref="EngineMetadataOverride"/>: an override names a stream's position inside a
/// composed output, and an extracted track is always the only stream of its own file. <see cref="Codec"/>
/// defaults to a stream copy; the only other values are the text subtitle targets, for a codec with no file
/// form of its own.
/// </para>
/// </summary>
public sealed record EngineExtractionOutput(
    string? MountLabel,
    string RelativePath,
    int StreamIndex,
    string? Codec = null,
    string? Language = null,
    string? Title = null);

/// <summary>
/// Re-encodes one mapped audio track instead of copying it. <see cref="Input"/> is the ordinal of the file
/// the stream comes from — 0 is the primary input — and <see cref="StreamIndex"/> its absolute index there.
/// <see cref="BitrateKbps"/> may be left null, in which case the engine lets ffmpeg scale a default to the
/// channel count.
/// </summary>
public sealed record EngineAudioTarget(int Input, int StreamIndex, string Codec, int? BitrateKbps = null);

/// <summary>
/// Rewrites one output stream's language and/or title. <see cref="Input"/> is the ordinal of the file the
/// stream comes from — 0 is the primary input, 1 the first additional input — and <see cref="StreamIndex"/>
/// its absolute index in that file. A null field leaves the source stream's own value alone.
/// </summary>
public sealed record EngineMetadataOverride(int Input, int StreamIndex, string? Language, string? Title);

/// <summary>
/// A sidecar file whose streams join the output — how a merge is expressed. Naming any makes the job a
/// stream copy on the engine's side, so the encode-only options must be left alone. Selections are absolute
/// stream indexes within that file.
/// </summary>
public sealed record EngineAdditionalInput(
    string? MountLabel,
    string RelativePath,
    IReadOnlyList<int>? AudioStreamIndexes = null,
    IReadOnlyList<int>? SubtitleStreamIndexes = null);

/// <summary>What is known about a job right after it is created. <see cref="OutputPath"/> is the composed
/// output and is null for an extraction; <see cref="OutputPaths"/> lists every file the job will produce and
/// is the field to read when either shape is possible.</summary>
public sealed record JobDescriptor(
    string JobId,
    string InputPath,
    string? OutputPath,
    double? DurationSeconds,
    long? InputSizeBytes,
    IReadOnlyList<string>? OutputPaths = null);

/// <summary>A live, in-memory progress snapshot (never persisted).</summary>
public sealed record JobSnapshot(
    string JobId,
    string? Name,
    string State,
    bool Complete,
    double PercentComplete,
    double Fps,
    double Speed,
    long OutputSizeBytes,
    double? EtaSeconds);

/// <summary>
/// Abstraction over the transcode engine. The transcoding surface is the external
/// <c>transcode-engine</c> app (<see cref="RemoteTranscodeEngine"/>); <see cref="DisabledTranscodeEngine"/>
/// stands in when none is configured. Owns no database state; surfaces live snapshots and raises events for
/// the job transitions a consumer cares about.
/// </summary>
public interface ITranscodeEngine
{
    /// <summary>Creates a job on the engine (the engine probes the input, enqueues it, and runs it as soon
    /// as a worker is free) and returns the descriptor.</summary>
    Task<JobDescriptor> CreateAsync(TranscodeJobRequest request, CancellationToken cancellationToken);

    /// <summary>Cancels a running or queued job.</summary>
    Task CancelAsync(string jobId, CancellationToken cancellationToken);

    /// <summary>Forgets a job and, when <paramref name="deleteOutput"/> is set, deletes its output file.</summary>
    Task RemoveAsync(string jobId, bool deleteOutput, CancellationToken cancellationToken);

    JobSnapshot? GetSnapshot(string jobId);

    IReadOnlyList<JobSnapshot> GetAllSnapshots();

    /// <summary>What the engine can do beyond ffmpeg, read from its <c>GET /hardware</c>; best-effort, so an
    /// engine that cannot be reached answers <see cref="TranscodeTooling.None"/> rather than failing.</summary>
    Task<TranscodeTooling> GetToolingAsync(CancellationToken cancellationToken);

    /// <summary>Raised when a job transitions from queued to running.</summary>
    event EventHandler<string>? JobStarted;

    /// <summary>Raised when a job finishes successfully.</summary>
    event EventHandler<string>? JobCompleted;

    /// <summary>Raised when a job fails or errors.</summary>
    event EventHandler<string>? JobFailed;
}
