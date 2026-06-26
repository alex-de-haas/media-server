namespace MediaServer.Api.Transcoding;

/// <summary>
/// <see cref="ITranscodeEngine"/> used when no external <c>transcode-engine</c> dependency is configured
/// (<c>TranscodeEngineUrl</c> is unset). Transcoding is unavailable, but the rest of the app keeps working:
/// this is the graceful degradation that matches Hosty's advisory (non-blocking) dependency model. Only
/// <see cref="CreateAsync"/> — the one command that needs a running engine — fails with a clear message;
/// cancel/remove are deliberately no-ops so stale rows can still be cleaned up, and every query returns
/// empty/null.
/// </summary>
public sealed class DisabledTranscodeEngine : ITranscodeEngine
{
    private const string Unavailable =
        "the transcode-engine dependency is not configured (HOSTY_DEPENDENCY_TRANSCODE_ENGINE_URL is not set).";

    // Never raised — there is no engine to produce events. Suppress the "unused event" warning.
#pragma warning disable CS0067
    public event EventHandler<string>? JobStarted;
    public event EventHandler<string>? JobCompleted;
    public event EventHandler<string>? JobFailed;
#pragma warning restore CS0067

    public Task<JobDescriptor> CreateAsync(TranscodeJobRequest request, CancellationToken cancellationToken) =>
        Task.FromException<JobDescriptor>(new InvalidOperationException(Unavailable));

    // Commands against a job the engine never knew about are no-ops, so stale rows can still be cancelled /
    // removed (any DB-side cleanup runs regardless) without surfacing a 500.
    public Task CancelAsync(string jobId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveAsync(string jobId, bool deleteOutput, CancellationToken cancellationToken) => Task.CompletedTask;

    public JobSnapshot? GetSnapshot(string jobId) => null;

    public IReadOnlyList<JobSnapshot> GetAllSnapshots() => [];
}
