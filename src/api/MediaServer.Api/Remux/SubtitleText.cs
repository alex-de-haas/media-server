using System.Text;
using System.Text.RegularExpressions;

namespace MediaServer.Api.Remux;

/// <summary>
/// Turns a Matroska subtitle sample into the plain text a <c>tx3g</c> track carries.
///
/// This is the one place the design stops referencing and starts rewriting. Video and audio samples are
/// the same bytes in both containers, so they are pointed at; a subtitle sample is not — SubRip keeps
/// markup, ASS keeps a row of fields and override codes, and MP4 wants neither. Styling is lost, which is
/// the accepted cost recorded in the epic: text subtitles are what this library has, and they read
/// correctly without their italics.
/// </summary>
internal static partial class SubtitleText
{
    /// <summary>Matroska codec ids this can convert, and whether the payload is an ASS dialogue row.</summary>
    internal static bool IsConvertible(string codecId) => codecId is
        "S_TEXT/UTF8" or "S_TEXT/ASCII" or "S_TEXT/ASS" or "S_TEXT/SSA";

    private static bool IsAss(string codecId) => codecId is "S_TEXT/ASS" or "S_TEXT/SSA";

    public static string Convert(ReadOnlySpan<byte> payload, string codecId)
    {
        var text = Encoding.UTF8.GetString(payload);
        return IsAss(codecId) ? FromAss(text) : FromSubRip(text);
    }

    /// <summary>
    /// SubRip in Matroska is the cue's text and nothing else — no numbering, no timing line, since the
    /// container already holds both. What is left is the inline markup.
    /// </summary>
    private static string FromSubRip(string text) => Normalise(Markup().Replace(text, string.Empty));

    /// <summary>
    /// An ASS block is a dialogue row minus its "Dialogue:" prefix and timings: the fields up to and
    /// including Effect, then the text. Only the text is wanted, and the field before it may itself
    /// contain commas, so the split is by count from the left rather than by the last comma.
    /// </summary>
    private static string FromAss(string text)
    {
        // ReadOrder, Layer, Style, Name, MarginL, MarginR, MarginV, Effect — eight fields, then the text.
        const int FieldsBeforeText = 8;
        var body = text;
        for (var i = 0; i < FieldsBeforeText; i++)
        {
            var comma = body.IndexOf(',');
            if (comma < 0)
            {
                // Not the shape expected; better to show the row than to show nothing.
                return Normalise(text);
            }

            body = body[(comma + 1)..];
        }

        body = Overrides().Replace(body, string.Empty);
        body = body.Replace("\\N", "\n", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\h", " ", StringComparison.Ordinal);
        return Normalise(body);
    }

    private static string Normalise(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

    /// <summary>SubRip's inline tags: the italics, bold and font colours MP4 timed text has no place for.</summary>
    [GeneratedRegex(@"</?[a-zA-Z][^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Markup();

    /// <summary>ASS override blocks — positioning, karaoke, fades — which are instructions, not words.</summary>
    [GeneratedRegex(@"\{[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex Overrides();
}
