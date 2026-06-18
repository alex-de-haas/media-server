using System.Text.RegularExpressions;
using AnitomySharp;
using MediaServer.Api.Data;
using AnitomyParser = AnitomySharp.AnitomySharp;

namespace MediaServer.Api.Metadata;

/// <summary>Result of parsing a torrent/file name. <see cref="Kind"/> is Movie or Episode.</summary>
public sealed record ParsedName(MediaKind Kind, string Title, int? Year, int? Season, int? Episode, int? EpisodeEnd);

/// <summary>
/// Parses a release/file name into title + year (movies) or title + season/episode (series/anime).
/// The parser is selected by catalog type: a Jellyfin-style regex engine for movie/series, and
/// AnitomySharp for anime absolute numbering. See <c>docs/planning/metadata.md</c>.
/// </summary>
public interface INameParser
{
    ParsedName Parse(string name, CatalogType catalogType);
}

public sealed partial class NameParser : INameParser
{
    public ParsedName Parse(string name, CatalogType catalogType)
    {
        var cleaned = Clean(StripExtension(name));

        return catalogType switch
        {
            CatalogType.Anime => ParseAnime(name, cleaned),
            CatalogType.Series => ParseSeries(cleaned),
            _ => ParseMovie(cleaned),
        };
    }

    private static ParsedName ParseMovie(string cleaned)
    {
        var yearMatch = YearRegex().Match(cleaned);
        if (yearMatch.Success)
        {
            var title = TidyTitle(cleaned[..yearMatch.Index]);
            return new ParsedName(MediaKind.Movie, title, int.Parse(yearMatch.Groups[1].Value), null, null, null);
        }

        return new ParsedName(MediaKind.Movie, TidyTitle(StripQuality(cleaned)), null, null, null, null);
    }

    private static ParsedName ParseSeries(string cleaned)
    {
        var match = SeasonEpisodeRegex().Match(cleaned);
        if (match.Success)
        {
            var title = TidyTitle(cleaned[..match.Index]);
            var season = int.Parse(match.Groups["s"].Value);
            var episode = int.Parse(match.Groups["e"].Value);
            int? episodeEnd = match.Groups["e2"].Success ? int.Parse(match.Groups["e2"].Value) : null;
            var year = ExtractYear(title);
            return new ParsedName(MediaKind.Episode, StripYear(title), year, season, episode, episodeEnd);
        }

        // No SxxEyy pattern: treat as a series-level title (e.g. a whole-show pack).
        var fallbackYear = ExtractYear(cleaned);
        return new ParsedName(MediaKind.Series, StripYear(TidyTitle(StripQuality(cleaned))), fallbackYear, null, null, null);
    }

    private static ParsedName ParseAnime(string original, string cleaned)
    {
        try
        {
            var elements = AnitomyParser.Parse(original).ToList();
            var title = elements.FirstOrDefault(element => element.Category == Element.ElementCategory.ElementAnimeTitle)?.Value;
            var episodeText = elements.FirstOrDefault(element => element.Category == Element.ElementCategory.ElementEpisodeNumber)?.Value;
            var yearText = elements.FirstOrDefault(element => element.Category == Element.ElementCategory.ElementAnimeYear)?.Value;
            var seasonText = elements.FirstOrDefault(element => element.Category == Element.ElementCategory.ElementAnimeSeason)?.Value;

            if (!string.IsNullOrWhiteSpace(title) && int.TryParse(episodeText, out var episode))
            {
                var season = int.TryParse(seasonText, out var parsedSeason) ? parsedSeason : 1;
                int? year = int.TryParse(yearText, out var parsedYear) ? parsedYear : null;
                return new ParsedName(MediaKind.Episode, TidyTitle(title), year, season, episode, null);
            }

            if (!string.IsNullOrWhiteSpace(title))
            {
                int? year = int.TryParse(yearText, out var parsedYear) ? parsedYear : null;
                return new ParsedName(MediaKind.Series, TidyTitle(title), year, null, null, null);
            }
        }
        catch
        {
            // Fall through to the series regex parser on any Anitomy failure.
        }

        return ParseSeries(cleaned);
    }

    private static string StripExtension(string name)
    {
        var extension = Path.GetExtension(name);
        return Media.MediaFormats.VideoExtensions.Contains(extension) ? Path.GetFileNameWithoutExtension(name) : name;
    }

    private static string Clean(string value) =>
        CollapseSpaces(value.Replace('.', ' ').Replace('_', ' ').Replace('+', ' ')).Trim();

    private static string TidyTitle(string value)
    {
        var trimmed = CollapseSpaces(value.Replace('-', ' ')).Trim(' ', '-', '(', ')', '[', ']');
        return trimmed;
    }

    private static string StripQuality(string value)
    {
        var match = QualityRegex().Match(value);
        return match.Success ? value[..match.Index] : value;
    }

    private static int? ExtractYear(string value)
    {
        var match = YearRegex().Match(value);
        return match.Success ? int.Parse(match.Groups[1].Value) : null;
    }

    private static string StripYear(string value) => TidyTitle(YearRegex().Replace(value, string.Empty));

    private static string CollapseSpaces(string value) => MultiSpaceRegex().Replace(value, " ");

    [GeneratedRegex(@"\b(19\d\d|20\d\d)\b")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"[Ss](?<s>\d{1,2})[\s._-]*[Ee](?<e>\d{1,3})(?:[\s._-]*[Ee]?(?<e2>\d{1,3}))?|(?<s>\d{1,2})x(?<e>\d{1,3})")]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"\b(480p|576p|720p|1080p|1440p|2160p|4k|bluray|blu-ray|bdrip|brrip|webrip|web-dl|webdl|hdtv|dvdrip|x264|x265|h264|h265|hevc|aac|ac3|dts|hdr|remux)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QualityRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();
}
