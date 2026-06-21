namespace MediaServer.Api.Organizer;

/// <summary>
/// Derives distinct, human-readable version labels for a set of files that all map to the same movie or
/// episode (e.g. a release that ships a black-and-white and a regular cut of one episode). The labels feed
/// both the disambiguated canonical filename and the client-facing version picker. The contract: the
/// returned list is aligned to the input order and every entry is non-empty and unique within the group.
/// </summary>
public static class EditionLabeler
{
    // Recognised quality/edition tokens → display labels. Anything not listed degrades to "Version N".
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BW"] = "Black & White",
        ["B&W"] = "Black & White",
        ["BLACKANDWHITE"] = "Black & White",
        ["DV"] = "Dolby Vision",
        ["DOLBYVISION"] = "Dolby Vision",
        ["HDR"] = "HDR",
        ["HDR10"] = "HDR10",
        ["SDR"] = "SDR",
        ["EXTENDED"] = "Extended",
        ["UNCUT"] = "Uncut",
        ["UNRATED"] = "Unrated",
        ["THEATRICAL"] = "Theatrical",
        ["REMASTERED"] = "Remastered",
        ["IMAX"] = "IMAX",
        ["PROPER"] = "Proper",
        ["REPACK"] = "Repack",
        ["2160P"] = "2160p",
        ["1080P"] = "1080p",
        ["720P"] = "720p",
        ["480P"] = "480p",
    };

    private static readonly char[] Separators = ['.', '_', '-', ' ', '(', ')', '[', ']'];

    /// <summary>
    /// Labels each path in <paramref name="relativePaths"/>. Tokens shared by every file (title, season,
    /// common quality tags) are not distinguishing and are ignored; a file's label is built from the
    /// recognised tokens unique to it. Files with no recognised distinguishing token become "Standard"
    /// when a sibling has one, otherwise the whole group falls back to "Version 1..N" so the labels —
    /// and the filenames derived from them — never collide.
    /// </summary>
    public static IReadOnlyList<string> Label(IReadOnlyList<string> relativePaths)
    {
        // Keep each filename's tokens in their original order — string hashing is randomized per process,
        // so a HashSet's iteration order (and any label/filename built from it) would drift across restarts.
        var tokenLists = relativePaths
            .Select(path => Tokenize(Path.GetFileNameWithoutExtension(path)))
            .ToList();

        // A set only for the "is this token shared by every file?" lookup; it never drives output order.
        var common = tokenLists
            .Skip(1)
            .Aggregate(
                new HashSet<string>(tokenLists[0], StringComparer.OrdinalIgnoreCase),
                (accumulator, tokens) =>
                {
                    accumulator.IntersectWith(tokens);
                    return accumulator;
                });

        var labels = tokenLists
            .Select(tokens => string.Join(
                " ",
                tokens.Where(token => !common.Contains(token))
                    .Select(token => Known.GetValueOrDefault(token))
                    .OfType<string>()
                    .Distinct()))
            .ToList();

        // Already distinct and fully named — use as-is.
        if (labels.All(label => label.Length > 0) && AreDistinct(labels))
        {
            return labels;
        }

        // Fill the unnamed (plain) files with "Standard"; if that's still distinct we're done.
        var filled = labels.Select(label => label.Length > 0 ? label : "Standard").ToList();
        if (AreDistinct(filled))
        {
            return filled;
        }

        // No reliable signal — fall back to stable ordinals so paths and picker entries stay unique.
        return Enumerable.Range(1, relativePaths.Count).Select(number => $"Version {number}").ToList();
    }

    private static string[] Tokenize(string name) =>
        name.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

    private static bool AreDistinct(IReadOnlyList<string> labels) =>
        labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() == labels.Count;
}
