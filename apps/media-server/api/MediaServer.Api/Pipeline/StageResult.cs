using MediaServer.Api.Metadata;

namespace MediaServer.Api.Pipeline;

/// <summary>Outcome of a pipeline stage; the orchestrator advances, backs off, parks, or fails on it.</summary>
public abstract record StageResult
{
    public sealed record Completed : StageResult;

    public sealed record NeedsReview(string Reason, IReadOnlyList<MetadataCandidate> Candidates) : StageResult;

    /// <summary>Transient: the reconciler retries after the delay (e.g. waiting on a download).</summary>
    public sealed record Deferred(TimeSpan RetryAfter) : StageResult;

    public sealed record Failed(string Error, bool Retryable) : StageResult;

    public static readonly StageResult Done = new Completed();
}
