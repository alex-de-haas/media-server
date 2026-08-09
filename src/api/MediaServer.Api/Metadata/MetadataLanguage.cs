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
    /// record sharing its base language (<c>ru</c> matches <c>ru-RU</c>), then whatever was cached first.
    /// </summary>
    public static T Pick<T>(IReadOnlyList<T> records, string preferred, Func<T, string> languageOf)
    {
        var prefix = preferred.Length >= 2 ? preferred[..2] : preferred;
        return records.FirstOrDefault(record => string.Equals(languageOf(record), preferred, StringComparison.OrdinalIgnoreCase))
            ?? records.FirstOrDefault(record => languageOf(record).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?? records[0];
    }

    /// <summary>
    /// How to order display titles in <paramref name="language"/>. Catalog listings render the localized
    /// metadata title, so they must order by that same string — and under the display language's collation
    /// rather than SQLite's, whose <c>BINARY</c> ordering files every lowercase letter after every uppercase
    /// one and every Cyrillic title after every Latin one.
    /// </summary>
    public static StringComparer TitleOrder(string language)
    {
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(language);
        }
        catch (CultureNotFoundException)
        {
            // A language tag the host has no culture for still has to sort somehow; invariant collation is
            // the same fallback the runtime uses when globalization is switched off entirely.
            culture = CultureInfo.InvariantCulture;
        }

        return StringComparer.Create(culture, ignoreCase: true);
    }
}
