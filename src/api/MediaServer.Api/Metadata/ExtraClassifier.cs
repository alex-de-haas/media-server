using System.Text.RegularExpressions;
using MediaServer.Api.Data;

namespace MediaServer.Api.Metadata;

/// <summary>What kind of series extra a file name looks like. Drives the suggested title and the review
/// dialog's pre-suggested action; never persisted (an imported extra only keeps its title).</summary>
public enum ExtraKind
{
    CreditlessOpening,
    CreditlessEnding,
    Opening,
    Ending,
    Special,
    PromoVideo,
    Trailer,
    Menu,
    Commercial,
    Other,
}

/// <summary>
/// A positive extras classification. <see cref="Title"/> is the suggested library title for the imported
/// extra (e.g. "Creditless Opening 2"); <see cref="SuggestSkip"/> marks kinds that are usually junk
/// (disc menus, commercials) where the review dialog should pre-suggest skipping instead of importing.
/// </summary>
public sealed record ExtraClassification(ExtraKind Kind, string Title, bool SuggestSkip);

/// <summary>
/// Detects series extras that have no provider identity — creditless OP/EDs ("NCOP", "Creditless ED 1"),
/// specials ("SP 2"), PVs, disc menus — from a file's name and parent folder. Used by Identify to route
/// such files straight to review (searching the provider for them is wasted or, worse, a false match) and
/// by the review UI to pre-suggest "attach as extra" or "skip" instead of parking the batch with no hint.
/// Only episodic catalogs classify — the shorthand tokens are far too ambiguous against movie titles
/// ("The Menu", "Special 26") — and a name carrying a real episode marker (SxxEyy) is never classified
/// from ambiguous tokens; only the definitive creditless forms win over an episode number.
/// </summary>
public static partial class ExtraClassifier
{
    /// <summary>Folder names that mark their contents as extras even when the file name itself is unhinted.</summary>
    private static readonly HashSet<string> ExtraFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "extra", "extras", "nc", "ncop", "nced", "menu", "menus", "pv", "pvs",
        "sp", "sps", "special", "specials", "trailer", "trailers", "cm", "cms", "preview", "previews",
    };

    /// <summary>Classifies a file by name (and parent folder), or null when it looks like regular content.
    /// Accepts either a bare file name or a relative path — folder hints only apply to the latter.</summary>
    public static ExtraClassification? Classify(string relativePath, CatalogType catalogType)
    {
        if (catalogType == CatalogType.Movie)
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(relativePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        // Dots/underscores are separator noise in release names; brackets stay (a bracketed [NCOP] is a signal).
        var name = fileName.Replace('.', ' ').Replace('_', ' ');

        // Definitive creditless forms win even next to an episode-looking token.
        if (NcRegex().Match(name) is { Success: true } nc)
        {
            return Creditless(nc.Groups["t"].Value, nc.Groups["n"].Value);
        }

        if (CreditlessRegex().Match(name) is { Success: true } creditless)
        {
            return Creditless(creditless.Groups["t"].Value, creditless.Groups["n"].Value);
        }

        // Everything below is ambiguous shorthand — an SxxEyy marker means it's a real episode, not an extra.
        if (EpisodeMarkerRegex().IsMatch(name))
        {
            return null;
        }

        if (BareOpEdRegex().Match(name) is { Success: true } opEd)
        {
            var opening = opEd.Groups["t"].Value.StartsWith("op", StringComparison.OrdinalIgnoreCase);
            return new ExtraClassification(
                opening ? ExtraKind.Opening : ExtraKind.Ending,
                Numbered(opening ? "Opening" : "Ending", opEd.Groups["n"].Value), SuggestSkip: false);
        }

        if (SpecialRegex().Match(name) is { Success: true } special)
        {
            return new ExtraClassification(ExtraKind.Special, Numbered("Special", special.Groups["n"].Value), SuggestSkip: false);
        }

        if (PvRegex().Match(name) is { Success: true } pv)
        {
            return new ExtraClassification(ExtraKind.PromoVideo, Numbered("PV", pv.Groups["n"].Value), SuggestSkip: false);
        }

        if (TrailerRegex().Match(name) is { Success: true } trailer)
        {
            return new ExtraClassification(ExtraKind.Trailer, Numbered("Trailer", trailer.Groups["n"].Value), SuggestSkip: false);
        }

        if (MenuRegex().Match(name) is { Success: true } menu)
        {
            return new ExtraClassification(ExtraKind.Menu, Numbered("Menu", menu.Groups["n"].Value), SuggestSkip: true);
        }

        if (CommercialRegex().Match(name) is { Success: true } commercial)
        {
            return new ExtraClassification(ExtraKind.Commercial, Numbered("CM", commercial.Groups["n"].Value), SuggestSkip: true);
        }

        // No token in the name, but the file sits in an extras-style folder — classify generically with the
        // cleaned file name as the title ("Extras/Interview with the staff.mkv" → "Interview with the staff").
        if (ParentFolderOf(relativePath) is { } folder && ExtraFolders.Contains(folder))
        {
            var title = CollapseSpacesRegex().Replace(name, " ").Trim(' ', '-', '[', ']', '(', ')');
            return title.Length > 0 ? new ExtraClassification(ExtraKind.Other, title, SuggestSkip: false) : null;
        }

        return null;
    }

    private static ExtraClassification Creditless(string token, string number)
    {
        var opening = token.StartsWith("op", StringComparison.OrdinalIgnoreCase);
        return new ExtraClassification(
            opening ? ExtraKind.CreditlessOpening : ExtraKind.CreditlessEnding,
            Numbered(opening ? "Creditless Opening" : "Creditless Ending", number), SuggestSkip: false);
    }

    private static string Numbered(string label, string number)
    {
        // Normalize a captured number ("02" → 2) so titles stay stable regardless of zero-padding.
        return number.Length > 0 && int.TryParse(number, out var value) ? $"{label} {value}" : label;
    }

    private static string? ParentFolderOf(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? segments[^2] : null;
    }

    // Same SxxEyy / NxM shapes NameParser treats as an episode marker.
    [GeneratedRegex(@"[Ss]\d{1,2}[\s._-]*[Ee]\d{1,3}|\b\d{1,2}x\d{1,3}\b")]
    private static partial Regex EpisodeMarkerRegex();

    // NCOP / NCED, optionally numbered ("NCOP1", "NC ED 02").
    [GeneratedRegex(@"(?<![\p{L}\p{N}])NC\s*(?<t>OP|ED)\s*(?<n>\d{1,3})?(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex NcRegex();

    // "Creditless OP 1", "Non-Credit Ending", "Clean Opening 2".
    [GeneratedRegex(@"\b(?:creditless|non[\s-]?credit(?:ed)?|clean)\s*(?<t>op(?:ening)?|ed|ending)\s*(?<n>\d{1,3})?(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex CreditlessRegex();

    // Bare "OP2" / "ED 1" (numbered, ≤2 digits — 3+ digits reads as an absolute episode number, e.g. a
    // One Piece "OP 1071"), or a bracketed bare "(OP)" / "[ED]".
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<t>OP|ED)\s*(?<n>\d{1,2})(?![\p{L}\p{N}])|[\[(](?<t>OP|ED)[\])]", RegexOptions.IgnoreCase)]
    private static partial Regex BareOpEdRegex();

    // "SP 2", "SP01", or a delimited bare "SP"; the spelled-out "Special(s)" only counts when numbered —
    // the bare word is a common part of real titles.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:SP\s*(?<n>\d{1,2})?|Specials?\s*(?<n>\d{1,2}))(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])PV\s*(?<n>\d{1,2})?(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex PvRegex();

    // Only at the end of the name (trailing release tags allowed) or bracketed — "Trailer Park Boys" is content.
    [GeneratedRegex(@"\btrailers?\s*(?<n>\d{1,2})?\s*(?:\[[^\]]*\]\s*|\([^)]*\)\s*)*$|[\[(]trailers?[\])]", RegexOptions.IgnoreCase)]
    private static partial Regex TrailerRegex();

    // The whole name must be the menu token (group/release tags allowed around it) or a bracketed "(Menu)" —
    // a title merely containing the word ("The Menu") is content.
    [GeneratedRegex(@"^\s*(?:\[[^\]]*\]\s*)*(?:BD[\s-]?)?Menu\s*(?<n>\d{1,2})?\s*(?:\[[^\]]*\]\s*|\([^)]*\)\s*)*$|[\[(](?:BD[\s-]?)?Menu[\])]", RegexOptions.IgnoreCase)]
    private static partial Regex MenuRegex();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])CM\s*(?<n>\d{1,2})?(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex CommercialRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex CollapseSpacesRegex();
}
