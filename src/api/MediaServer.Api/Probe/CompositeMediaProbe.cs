namespace MediaServer.Api.Probe;

/// <summary>
/// The probe the rest of the app uses. The external engine leads and the container-header reader follows:
/// a deployment with the engine attached behaves exactly as it always has, and the reader only ever adds
/// capability where there was none.
/// <para>
/// It also removes a failure mode. Probing used to run a local <c>ffprobe</c> whose failure propagated and
/// parked an ingest item; now an engine that is absent, unreachable or refusing degrades to the reader
/// instead, and only a file neither can read fails.
/// </para>
/// <para>
/// While both can answer, their durations are compared and a real disagreement is logged with enough to act
/// on. The reader is in effect on probation: the log is the evidence that decides, field by field, whether
/// it is ever promoted past being a fallback.
/// </para>
/// </summary>
public sealed class CompositeMediaProbe(
    RemoteMediaProbe? remote,
    HeaderMediaProbe header,
    ILogger<CompositeMediaProbe> logger) : IMediaProbe
{
    /// <summary>
    /// What counts as a real disagreement rather than container noise. Measured over a 49-file library, the
    /// natural spread between what a header states and what ffprobe reports topped out at 57 ms on a 2 h 12 m
    /// file — the difference between the video and audio track lengths, not a parse error. One second, or
    /// half a percent on a long file, is comfortably clear of that.
    /// </summary>
    private static readonly TimeSpan AbsoluteTolerance = TimeSpan.FromSeconds(1);
    private const double RelativeTolerance = 0.005;

    public async Task<ProbeResult> ProbeAsync(string absolutePath, CancellationToken cancellationToken)
    {
        var fromHeader = header.TryProbe(absolutePath);

        if (remote is not null)
        {
            var fromEngine = await remote.TryProbeAsync(absolutePath, cancellationToken);
            if (fromEngine is not null)
            {
                ReportDivergence(absolutePath, fromEngine, fromHeader);
                return fromEngine;
            }

            logger.LogInformation(
                "The transcode engine could not probe {Path}; falling back to its container header.", absolutePath);
        }

        return fromHeader
            ?? throw new InvalidOperationException(
                $"Neither the transcode engine nor the container header of '{absolutePath}' could be read.");
    }

    /// <summary>
    /// Logs a duration the header reader got materially wrong, with what it takes to group the reports: the
    /// file, both values, and — for Matroska — the application that wrote it. A pattern by writer is what
    /// found the OpenDML defect in the first place; "some files are off" would not have.
    /// </summary>
    private void ReportDivergence(string absolutePath, ProbeResult engine, ProbeResult? headerResult)
    {
        if (headerResult is null || engine.DurationTicks <= 0 || headerResult.DurationTicks <= 0)
        {
            return;
        }

        var engineDuration = TimeSpan.FromTicks(engine.DurationTicks);
        var headerDuration = TimeSpan.FromTicks(headerResult.DurationTicks);
        var delta = (engineDuration - headerDuration).Duration();
        if (delta <= AbsoluteTolerance || delta.TotalSeconds <= engineDuration.TotalSeconds * RelativeTolerance)
        {
            return;
        }

        logger.LogWarning(
            "Container-header duration for {Path} diverges from the engine: header {HeaderSeconds:F3}s, " +
            "engine {EngineSeconds:F3}s, off by {DeltaSeconds:F3}s. Written by {WritingApp}.",
            absolutePath,
            headerDuration.TotalSeconds,
            engineDuration.TotalSeconds,
            delta.TotalSeconds,
            header.TryReadWritingApp(absolutePath) ?? "an unnamed muxer");
    }
}
