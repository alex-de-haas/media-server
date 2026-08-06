using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;

namespace MediaServer.Api.Probe;

/// <summary>
/// Probes through the external <c>transcode-engine</c>'s <c>POST /probe</c>, which runs the ffprobe that
/// app already carries. This is the source of truth: it reads colour information out of the codec bitstream,
/// tells HDR10 from Dolby Vision, and reports codec profiles — none of which a container header states.
/// <para>
/// The engine addresses files by media mount, exactly as job creation does, so a path outside the catalog
/// roots bound into it cannot be probed and the caller falls back.
/// </para>
/// </summary>
public sealed class RemoteMediaProbe(HttpClient http, MediaServerSettings settings, ILogger<RemoteMediaProbe> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Short deadline: this sits on the ingest path, and a hung engine must degrade to the header
    /// reader rather than stall a drive.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Probes through the engine, or returns null when it cannot answer — an unreachable engine, a
    /// file outside its mounts, or anything it refuses. Never throws for those; the caller falls back.</summary>
    public async Task<ProbeResult?> TryProbeAsync(string absolutePath, CancellationToken cancellationToken)
    {
        if (!CatalogMounts.TryResolve(settings, absolutePath, out var label, out var relative))
        {
            logger.LogDebug(
                "{Path} is not under a catalog root bound as a media mount, so the engine cannot probe it.",
                absolutePath);
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            using var response = await http.PostAsJsonAsync(
                "probe", new { mountLabel = label, path = relative }, Json, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug("The engine refused to probe {Path}: {Status}.", relative, response.StatusCode);
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<EngineProbe>(Json, timeout.Token);
            return body is null ? null : Map(body, absolutePath);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException &&
                                          !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Could not reach the engine to probe {Path}.", relative);
            return null;
        }
    }

    private static ProbeResult Map(EngineProbe probe, string absolutePath)
    {
        var streams = probe.Streams
            .Select(MapStream)
            .OfType<ProbedStream>()
            .ToList();

        return new ProbeResult(
            probe.Container,
            (long)((probe.DurationSeconds ?? 0) * TimeSpan.TicksPerSecond),
            probe.Bitrate,
            probe.SizeBytes,
            streams,
            ProbeSource.Engine);
    }

    private static ProbedStream? MapStream(EngineStream stream)
    {
        var type = stream.Kind switch
        {
            "Video" => StreamType.Video,
            "Audio" => StreamType.Audio,
            "Subtitle" => StreamType.Subtitle,
            // Data and attachment streams are not modelled here. Dropping them is safe: every stream keeps
            // its own absolute index, so the ones that remain still line up with the engine's numbering.
            _ => (StreamType?)null,
        };

        return type is null
            ? null
            : new ProbedStream(
                type.Value,
                stream.Index,
                stream.Codec,
                stream.Profile,
                ProbeVocabulary.Language(stream.Language),
                stream.Width,
                stream.Height,
                stream.FrameRate is { } rate ? Math.Round(rate, 3) : null,
                stream.BitDepth,
                Hdr(stream.Hdr),
                stream.Channels,
                stream.SampleRate,
                stream.Bitrate,
                stream.IsDefault,
                stream.IsForced,
                stream.Title);
    }

    /// <summary>The engine's HDR vocabulary, mapped to the labels this library stores. It should never send
    /// <c>Unknown</c> — that member exists for the header reader — but an unrecognized value is treated as
    /// unknown rather than asserted as SDR.</summary>
    private static string? Hdr(string? hdr) => hdr switch
    {
        "DolbyVision" => "Dolby Vision",
        "Hdr10Plus" => "HDR10+",
        "Hdr10" => "HDR10",
        "Hlg" => "HLG",
        "Sdr" => ProbeVocabulary.Sdr,
        _ => null,
    };

    private sealed record EngineProbe(
        string Container,
        double? DurationSeconds,
        int? Bitrate,
        long SizeBytes,
        IReadOnlyList<EngineStream> Streams);

    private sealed record EngineStream(
        int Index,
        string Kind,
        string? Codec,
        string? Profile,
        string? Language,
        string? Title,
        bool IsDefault,
        bool IsForced,
        int? Width,
        int? Height,
        double? FrameRate,
        int? BitDepth,
        string? Hdr,
        int? Channels,
        int? SampleRate,
        int? Bitrate);
}
