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
    string OutputRelativePath,
    string VideoCodec,
    string HardwareAcceleration,
    int? Crf);

/// <summary>What is known about a job right after it is created.</summary>
public sealed record JobDescriptor(
    string JobId,
    string InputPath,
    string OutputPath,
    double? DurationSeconds,
    long? InputSizeBytes);

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

    /// <summary>Raised when a job transitions from queued to running.</summary>
    event EventHandler<string>? JobStarted;

    /// <summary>Raised when a job finishes successfully.</summary>
    event EventHandler<string>? JobCompleted;

    /// <summary>Raised when a job fails or errors.</summary>
    event EventHandler<string>? JobFailed;
}
