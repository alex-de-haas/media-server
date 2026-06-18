using MediaServer.Api.Data;

namespace MediaServer.Api.Probe;

public sealed record ProbedStream(
    StreamType Type,
    int Index,
    string? Codec,
    string? Profile,
    string? Language,
    int? Width,
    int? Height,
    double? FrameRate,
    int? BitDepth,
    string? HdrFormat,
    int? Channels,
    int? SampleRate,
    bool IsDefault,
    bool IsForced);

public sealed record ProbeResult(
    string Container,
    long DurationTicks,
    int? Bitrate,
    long SizeBytes,
    IReadOnlyList<ProbedStream> Streams);

/// <summary>Probes a library file with <c>ffprobe</c> to discover media sources and streams.</summary>
public interface IMediaProbe
{
    Task<ProbeResult> ProbeAsync(string absolutePath, CancellationToken cancellationToken);
}
