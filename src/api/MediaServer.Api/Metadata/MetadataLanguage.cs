using System.Globalization;

namespace MediaServer.Api.Metadata;

/// <summary>
/// Display-language helpers shared by the read surfaces. Metadata is cached per language, so every
/// surface has to answer the same two questions: which cached record to render, and how to order the
/// titles it rendered.
/// </summary>
public static class MetadataLanguage
{
    /// <summary>
    /// The record whose language best matches <paramref name="preferred"/>: the exact tag first, then any
    /// record sharing its primary subtag (<c>ru</c> matches <c>ru-RU</c>), then whatever was cached first.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="records"/> is empty. There is no sensible record to return, and every caller either
    /// checks first or is iterating groups, which are never empty — so an empty list is a caller bug rather
    /// than a state this can paper over.
    /// </exception>
    public static T Pick<T>(IReadOnlyList<T> records, string preferred, Func<T, string> languageOf)
    {
        if (records.Count == 0)
        {
            throw new ArgumentException("Cannot pick a language from an empty record set.", nameof(records));
        }

        // Compared as a whole subtag rather than a two-character prefix, which would read "fil-PH" as
        // Filipino-matches-Finnish.
        var primary = PrimarySubtag(preferred);
        return records.FirstOrDefault(record => string.Equals(languageOf(record), preferred, StringComparison.OrdinalIgnoreCase))
            ?? records.FirstOrDefault(record => string.Equals(PrimarySubtag(languageOf(record)), primary, StringComparison.OrdinalIgnoreCase))
            ?? records[0];
    }

    /// <summary>The language part of a BCP 47 tag: <c>ru</c> for <c>ru-RU</c>, <c>fil</c> for <c>fil-PH</c>.</summary>
    private static string PrimarySubtag(string language)
    {
        var separator = language.IndexOf('-');
        return separator < 0 ? language : language[..separator];
    }

    /// <summary>
    /// How to order display titles in <paramref name="language"/>. Catalog listings render the localized
    /// metadata title, so they must order by that same string — and under the display language's collation
    /// rather than SQLite's, whose <c>BINARY</c> ordering files every lowercase letter after every uppercase
    /// one and every Cyrillic title after every Latin one.
    /// </summary>
    public static StringComparer TitleOrder(string? language)
    {
        // A listing has to come back ordered whatever the configured tag looks like, so an unusable one
        // degrades to invariant collation rather than failing the request. Config parsing already drops
        // blank entries; this covers a settings object built by hand.
        var culture = CultureInfo.InvariantCulture;
        if (!string.IsNullOrWhiteSpace(language))
        {
            try
            {
                culture = CultureInfo.GetCultureInfo(language);
            }
            catch (CultureNotFoundException)
            {
                // A tag the host has no culture for: same fallback the runtime uses when globalization is
                // switched off entirely.
            }
        }

        return StringComparer.Create(culture, ignoreCase: true);
    }
}
