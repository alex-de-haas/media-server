using System.Diagnostics;
using MediaServer.Api.Configuration;

namespace MediaServer.Api.Mux;

/// <summary>
/// Runs <c>ffmpeg</c> to append external audio tracks to a video with every stream copied — a remux, I/O
/// bound with no encoder involved, so it runs in-process rather than on the transcode-engine. The output
/// is always Matroska (the universal carrier for any codec combination). The binary path comes from the
/// <c>FFMPEG_PATH</c> setting, falling back to a PATH lookup (the image installs the full ffmpeg package).
/// </summary>
public sealed class FfmpegAudioMuxer(MediaServerSettings settings) : IAudioMuxer
{
    public async Task MuxAsync(AudioMuxPlan plan, CancellationToken cancellationToken)
    {
        var executable = string.IsNullOrWhiteSpace(settings.FfmpegPath) ? "ffmpeg" : settings.FfmpegPath;

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in BuildArguments(plan))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Don't leave an orphaned ffmpeg running when the drive is cancelled.
            TryKill(process);
            throw;
        }

        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}: {Tail(stderr)}");
        }
    }

    /// <summary>
    /// The full argument list: every stream of the video (<c>-map 0</c> keeps subtitles, chapters, and
    /// attached fonts) plus only the audio streams of each input (<c>-map i:a</c> — never an mp3's cover
    /// art), all stream-copied. Language/title metadata is set per appended output stream, positioned
    /// after the video's own audio streams; a null tag leaves the source stream's own value untouched.
    /// </summary>
    internal static IReadOnlyList<string> BuildArguments(AudioMuxPlan plan)
    {
        var arguments = new List<string> { "-nostdin", "-y", "-v", "error", "-i", plan.VideoAbsolutePath };
        foreach (var input in plan.AudioInputs)
        {
            arguments.Add("-i");
            arguments.Add(input.AbsolutePath);
        }

        arguments.Add("-map");
        arguments.Add("0");
        for (var index = 0; index < plan.AudioInputs.Count; index++)
        {
            arguments.Add("-map");
            arguments.Add($"{index + 1}:a");
        }

        arguments.Add("-c");
        arguments.Add("copy");

        var position = plan.VideoAudioStreamCount;
        foreach (var stream in plan.AudioInputs.SelectMany(input => input.Streams))
        {
            if (stream.Language is { Length: > 0 } language)
            {
                arguments.Add($"-metadata:s:a:{position}");
                arguments.Add($"language={language}");
            }

            if (stream.Title is { Length: > 0 } title)
            {
                arguments.Add($"-metadata:s:a:{position}");
                arguments.Add($"title={title}");
            }

            position++;
        }

        arguments.Add("-f");
        arguments.Add("matroska");
        arguments.Add(plan.OutputAbsolutePath);
        return arguments;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best effort: the process may have already exited or be unkillable.
        }
    }

    /// <summary>ffmpeg front-loads context lines; the actionable error is at the end of stderr.</summary>
    private static string Tail(string stderr)
    {
        var trimmed = stderr.Trim();
        return trimmed.Length <= 500 ? trimmed : "… " + trimmed[^500..];
    }
}
