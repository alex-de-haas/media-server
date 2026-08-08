using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MediaServer.Api.Remux;

/// <summary>One line of dialogue and how long it is on screen, in milliseconds.</summary>
internal readonly record struct TextCue(long Start, long Duration, string Text);

/// <summary>
/// Reads a subtitle file beside the video into cues.
///
/// A sidecar subtitle is the one thing here with no index and no need of one: a film's dialogue is a
/// hundred kilobytes, so it is parsed per request rather than walked in the background. What comes out
/// joins the embedded path at exactly the same point — a list of cues, which the synthesiser turns into a
/// timed-text track.
/// </summary>
internal static partial class SubtitleFile
{
    /// <summary>Roughly ten times the largest subtitle file worth expecting, and far below what a film is.</summary>
    private const int MaxBytes = 16 * 1024 * 1024;

    internal static bool IsConvertible(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".ass" or ".ssa" or ".vtt";

    /// <summary>Returns the cues in start order, or an empty list when nothing could be read.</summary>
    public static IReadOnlyList<TextCue> Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxBytes)
            {
                return [];
            }

            // Subtitle files are routinely written with a byte-order mark and occasionally in a legacy
            // encoding; detection covers the first and gets the common cases of the second.
            var text = File.ReadAllText(path, Encoding.UTF8);
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var cues = extension is ".ass" or ".ssa" ? ReadAss(text) : ReadSubRip(text);

            return [.. cues.Where(cue => cue.Duration > 0 && cue.Text.Length > 0).OrderBy(cue => cue.Start)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// SubRip, and WebVTT with it: the two differ in a header line and in whether the timestamps use a
    /// comma or a dot, neither of which changes how a cue is read.
    /// </summary>
    private static IEnumerable<TextCue> ReadSubRip(string text)
    {
        foreach (var block in Blocks().Split(text.Replace("\r\n", "\n", StringComparison.Ordinal)))
        {
            var lines = block.Split('\n', StringSplitOptions.TrimEntries);
            var timingAt = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timingAt < 0)
            {
                continue;
            }

            var parts = lines[timingAt].Split("-->", StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || ParseTimestamp(parts[0]) is not { } start
                || ParseTimestamp(parts[1].Split(' ')[0]) is not { } end)
            {
                continue;
            }

            // Everything after the timing line is the cue; the optional number before it is not.
            var body = string.Join('\n', lines[(timingAt + 1)..]).Trim();
            yield return new TextCue(start, end - start, SubtitleText.Convert(
                Encoding.UTF8.GetBytes(body), "S_TEXT/UTF8"));
        }
    }

    private static IEnumerable<TextCue> ReadAss(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Dialogue: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            var fields = trimmed["Dialogue:".Length..].Split(',', 10);
            if (fields.Length < 10
                || ParseTimestamp(fields[1]) is not { } start
                || ParseTimestamp(fields[2]) is not { } end)
            {
                continue;
            }

            // The row is handed on whole so the same conversion strips the override codes here as it does
            // for an embedded ASS track.
            var row = string.Join(',', fields[3..]);
            yield return new TextCue(start, end - start, SubtitleText.Convert(
                Encoding.UTF8.GetBytes("0,0," + row), "S_TEXT/ASS"));
        }
    }

    /// <summary>
    /// <c>HH:MM:SS,mmm</c>, <c>HH:MM:SS.mmm</c> or ASS's <c>H:MM:SS.cc</c> — the last counting hundredths
    /// rather than thousandths, which is why the fraction is scaled by its own width.
    /// </summary>
    private static long? ParseTimestamp(string value)
    {
        var match = Timestamp().Match(value.Trim());
        if (!match.Success)
        {
            return null;
        }

        var hours = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minutes = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var seconds = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var fraction = match.Groups[4].Success ? match.Groups[4].Value : "0";
        var scaled = long.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);

        return (((hours * 60) + minutes) * 60 + seconds) * 1000 + scaled;
    }

    [GeneratedRegex(@"\n\s*\n", RegexOptions.CultureInvariant)]
    private static partial Regex Blocks();

    [GeneratedRegex(@"^(\d+):(\d{1,2}):(\d{1,2})(?:[.,](\d{1,3}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Timestamp();
}
