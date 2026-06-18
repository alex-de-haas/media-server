using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;

namespace MediaServer.Api.Probe;

/// <summary>
/// Runs <c>ffprobe</c> (no transcoding in v1) and maps its JSON output to a <see cref="ProbeResult"/>.
/// The binary path comes from the <c>FFPROBE_PATH</c> setting, falling back to a PATH lookup.
/// </summary>
public sealed class FfprobeMediaProbe(MediaServerSettings settings) : IMediaProbe
{
    public async Task<ProbeResult> ProbeAsync(string absolutePath, CancellationToken cancellationToken)
    {
        var executable = string.IsNullOrWhiteSpace(settings.FfprobePath) ? "ffprobe" : settings.FfprobePath;

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("quiet");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add(absolutePath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            throw new InvalidOperationException($"ffprobe exited with code {process.ExitCode}: {stderr}");
        }

        return Parse(stdout, absolutePath);
    }

    internal static ProbeResult Parse(string json, string absolutePath)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;
        var container = GetString(format, "format_name") ?? Path.GetExtension(absolutePath).TrimStart('.');
        var durationTicks = (long)(GetDouble(format, "duration") ?? 0) * TimeSpan.TicksPerSecond;
        var bitrate = (int?)GetLong(format, "bit_rate");
        var size = GetLong(format, "size") ?? SafeFileSize(absolutePath);

        var streams = new List<ProbedStream>();
        if (root.TryGetProperty("streams", out var streamArray) && streamArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streamArray.EnumerateArray())
            {
                var mapped = MapStream(stream);
                if (mapped is not null)
                {
                    streams.Add(mapped);
                }
            }
        }

        return new ProbeResult(container, durationTicks, bitrate, size, streams);
    }

    private static ProbedStream? MapStream(JsonElement stream)
    {
        var codecType = GetString(stream, "codec_type");
        var type = codecType switch
        {
            "video" => StreamType.Video,
            "audio" => StreamType.Audio,
            "subtitle" => StreamType.Subtitle,
            _ => (StreamType?)null,
        };
        if (type is null)
        {
            return null;
        }

        var disposition = stream.TryGetProperty("disposition", out var dispositionElement) ? dispositionElement : default;
        var tags = stream.TryGetProperty("tags", out var tagsElement) ? tagsElement : default;
        var transfer = GetString(stream, "color_transfer");

        return new ProbedStream(
            type.Value,
            (int)(GetLong(stream, "index") ?? 0),
            GetString(stream, "codec_name"),
            GetString(stream, "profile"),
            GetString(tags, "language"),
            (int?)GetLong(stream, "width"),
            (int?)GetLong(stream, "height"),
            ParseFrameRate(GetString(stream, "r_frame_rate")),
            (int?)GetLong(stream, "bits_per_raw_sample"),
            MapHdr(transfer),
            (int?)GetLong(stream, "channels"),
            int.TryParse(GetString(stream, "sample_rate"), out var sampleRate) ? sampleRate : null,
            GetLong(disposition, "default") == 1,
            GetLong(disposition, "forced") == 1);
    }

    private static string? MapHdr(string? colorTransfer) => colorTransfer switch
    {
        "smpte2084" => "HDR10",
        "arib-std-b67" => "HLG",
        _ => null,
    };

    private static double? ParseFrameRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('/');
        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) &&
            denominator != 0)
        {
            return Math.Round(numerator / denominator, 3);
        }

        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) ? single : null;
    }

    private static long SafeFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? GetLong(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static double? GetDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}
