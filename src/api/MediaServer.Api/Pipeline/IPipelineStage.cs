using MediaServer.Api.Data;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// One ordered, idempotent pipeline stage over a shared <see cref="IngestContext"/>. New stages
/// (including future acquisition stages) implement the same contract and slot in by <see cref="Order"/>
/// without touching existing ones. See <c>docs/features/automation-pipeline.md</c>.
/// </summary>
public interface IPipelineStage
{
    string Key { get; }

    PipelinePhase Phase { get; }

    /// <summary>Global execution order (ascending). Acquisition stages sort before processing stages.</summary>
    int Order { get; }

    /// <summary>The persisted stage label this stage maps to while it runs.</summary>
    IngestStage Stage { get; }

    /// <summary>Idempotency guard: false when already satisfied, so re-runs are side-effect free.</summary>
    bool ShouldRun(IngestContext context);

    Task<StageResult> RunAsync(IngestContext context, CancellationToken cancellationToken);
}
