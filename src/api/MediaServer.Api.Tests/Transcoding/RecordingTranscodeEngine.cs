using MediaServer.Api.Transcoding;

namespace MediaServer.Api.Tests.Transcoding;

/// <summary>
/// Captures the request instead of talking to an engine, and answers with a descriptor. Shared by the
/// conversion and extraction tests, which submit to the same engine and read the same request back.
/// </summary>
internal sealed class RecordingTranscodeEngine : ITranscodeEngine
{
    private int _created;

    public TranscodeJobRequest? Seen { get; private set; }

    public Task<JobDescriptor> CreateAsync(TranscodeJobRequest request, CancellationToken cancellationToken)
    {
        Seen = request;
        // A fresh id per call, because EngineJobId is unique: a test that extracts twice is testing the
        // second attempt, not the index.
        return Task.FromResult(new JobDescriptor(
            $"engine-{++_created}", request.InputRelativePath, request.OutputRelativePath, 120, 1000,
            request.Outputs?.Select(output => output.RelativePath).ToList()));
    }

    public Task CancelAsync(string jobId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveAsync(string jobId, bool deleteOutput, CancellationToken cancellationToken) => Task.CompletedTask;

    public JobSnapshot? GetSnapshot(string jobId) => null;

    public IReadOnlyList<JobSnapshot> GetAllSnapshots() => [];

    public Task<TranscodeTooling> GetToolingAsync(CancellationToken cancellationToken) => Task.FromResult(TranscodeTooling.None);

#pragma warning disable CS0067 // The consumer surface raises these; nothing here does.
    public event EventHandler<string>? JobStarted;
    public event EventHandler<string>? JobCompleted;
    public event EventHandler<string>? JobFailed;
#pragma warning restore CS0067
}

