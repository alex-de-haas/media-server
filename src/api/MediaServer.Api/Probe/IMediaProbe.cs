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
    /// <summary>This stream's own bitrate in bits per second, or null when the file states none. The header
    /// reader always answers null: a per-track rate is not in the bytes it reads.</summary>
    int? Bitrate,
    bool IsDefault,
    bool IsForced,
    string? Title);

public sealed record ProbeResult(
    string Container,
    long DurationTicks,
    int? Bitrate,
    long SizeBytes,
    IReadOnlyList<ProbedStream> Streams,
    ProbeSource Source = ProbeSource.Engine);

/// <summary>
/// Which provider produced a result. Recorded against every stored source because the two do not know the
/// same things: a null field from <see cref="Header"/> may simply be beyond a container header's reach,
/// while the same null from <see cref="Engine"/> is an answer. It is also what lets rows read by the weaker
/// provider be found again and filled in once the engine is attached.
/// </summary>
public enum ProbeSource
{
    /// <summary>The external transcode-engine, running ffprobe. The source of truth.</summary>
    Engine = 0,

    /// <summary>This app's own container-header reader, used when the engine is absent or fails.</summary>
    Header = 1,
}

/// <summary>Discovers a library file's media sources and streams.</summary>
public interface IMediaProbe
{
    Task<ProbeResult> ProbeAsync(string absolutePath, CancellationToken cancellationToken);
}
