using System.Globalization;
using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;

namespace MediaServer.Api.Metadata;

/// <summary>
/// TMDb implementation of <see cref="IMetadataProvider"/> for all catalog types in v1. Searches and
/// scores candidates, fetches localized details for every supported language, and lists language-tagged
/// images. The API key is sent as a query parameter and never logged (see <c>docs/features/security.md</c>).
/// </summary>
public sealed class TmdbMetadataProvider(IHttpClientFactory httpClientFactory, MediaServerSettings settings, ILogger<TmdbMetadataProvider> logger)
    : IMetadataProvider
{
    public const string HttpClientName = "tmdb";
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/original";
    // Smaller rendition for search-candidate thumbnails in the manual-match UI.
    private const string PosterThumbBaseUrl = "https://image.tmdb.org/t/p/w154";

    public string Key => "tmdb";

    public async Task<IReadOnlyList<MetadataCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        var type = TmdbType(query.Kind);
        var yearParam = query.Year is { } year
            ? type == "movie" ? $"&year={year}" : $"&first_air_date_year={year}"
            : string.Empty;

        // A query in a non-Latin script asks TMDb for the matching configured metadata language: the
        // search itself matches translated titles regardless, but the response `title` comes back in the
        // request language — and the local re-score below can only see the match when the candidate title
        // is in the query's script (a RU query against "Back to the Future" scores zero title overlap).
        var languageParam = SearchLanguageFor(query.Title) is { } language
            ? $"&language={Uri.EscapeDataString(language)}"
            : string.Empty;

        var path = $"search/{type}?query={Uri.EscapeDataString(query.Title)}{yearParam}{languageParam}";
        var document = await GetAsync(path, cancellationToken);
        if (document is null || !document.RootElement.TryGetProperty("results", out var results))
        {
            return [];
        }

        var candidates = new List<MetadataCandidate>();
        foreach (var result in results.EnumerateArray())
        {
            var id = GetString(result, "id");
            var originalTitle = GetString(result, type == "movie" ? "original_title" : "original_name");
            var title = GetString(result, type == "movie" ? "title" : "name") ?? originalTitle;
            if (id is null || title is null)
            {
                continue;
            }

            var candidateYear = ParseYear(GetString(result, type == "movie" ? "release_date" : "first_air_date"));
            // Best of display vs original title: a film searched by its original-language name still
            // scores when the display title is the English one (and vice versa).
            var score = TitleScoring.Score(query.Title, query.Year, title, candidateYear);
            if (originalTitle is not null && originalTitle != title)
            {
                score = Math.Max(score, TitleScoring.Score(query.Title, query.Year, originalTitle, candidateYear));
            }
            var posterPath = GetString(result, "poster_path");
            var posterUrl = string.IsNullOrEmpty(posterPath) ? null : PosterThumbBaseUrl + posterPath;
            candidates.Add(new MetadataCandidate(new ProviderRef(Key, id), title, candidateYear, score, posterUrl));
        }

        return candidates.OrderByDescending(candidate => candidate.Score).ToList();
    }

    public async Task<IReadOnlyList<ProviderMetadata>> FetchAsync(
        ProviderRef reference, MediaKind kind, IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        // TMDb has separate id spaces for movies and tv, so the same numeric id can be valid in both
        // (e.g. tv 95480 "Slow Horses" vs movie 95480 "Flesh, TX"). The caller knows the kind from the
        // matched MediaItem, so pick the endpoint from it instead of probing — probing movie-first would
        // silently fetch an unrelated title whenever a tv id collides with a movie id.
        var type = TmdbType(kind);

        // append_to_response folds credits/external ids/videos/certification/keywords into the same detail
        // call (TMDb allows up to 20 sub-requests) — no extra round-trips, and the full payload lands in
        // MetadataRecord.Raw for the read layer to project from. Certification lives in release_dates for
        // movies and content_ratings for tv.
        var appends = type == "movie"
            ? "credits,external_ids,videos,release_dates,keywords"
            : "credits,external_ids,videos,content_ratings,keywords";

        var records = new List<ProviderMetadata>(languages.Count);
        foreach (var language in languages)
        {
            var document = await GetAsync(
                $"{type}/{reference.Id}?language={Uri.EscapeDataString(language)}&append_to_response={appends}",
                cancellationToken);
            if (document is null)
            {
                continue;
            }

            records.Add(MapDetails(reference, language, type, document.RootElement));
        }

        return records;
    }

    public async Task<IReadOnlyList<RemoteImage>> GetImagesAsync(
        ProviderRef reference, MediaKind kind, IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        var type = TmdbType(kind);
        var imageLanguages = string.Join(',', languages.Select(language => language.Split('-')[0]).Distinct().Append("null"));
        var document = await GetAsync($"{type}/{reference.Id}/images?include_image_language={imageLanguages}", cancellationToken);
        if (document is null)
        {
            return [];
        }

        var images = new List<RemoteImage>();
        AppendImages(document.RootElement, "posters", ImageType.Primary, images);
        AppendImages(document.RootElement, "backdrops", ImageType.Backdrop, images);
        AppendImages(document.RootElement, "logos", ImageType.Logo, images);
        return images;
    }

    public async Task<PersonDetails?> FetchPersonAsync(ProviderRef reference, string language, CancellationToken cancellationToken)
    {
        // Person biography/birth fields are not part of the media credits payload, so they need their own
        // /person/{id} call. Like the detail fetch, the api_key rides as a query/Bearer credential in GetAsync
        // and is never logged. profile_path is returned raw here; the caller derives the absolute image URL.
        var document = await GetAsync($"person/{reference.Id}?language={Uri.EscapeDataString(language)}", cancellationToken);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        return new PersonDetails(
            EmptyToNull(GetString(root, "name")),
            EmptyToNull(GetString(root, "biography")),
            EmptyToNull(GetString(root, "profile_path")),
            EmptyToNull(GetString(root, "known_for_department")),
            EmptyToNull(GetString(root, "birthday")),
            EmptyToNull(GetString(root, "deathday")),
            EmptyToNull(GetString(root, "place_of_birth")));
    }

    private static void AppendImages(JsonElement root, string property, ImageType type, List<RemoteImage> images)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var sortOrder = 0;
        foreach (var image in array.EnumerateArray())
        {
            var filePath = GetString(image, "file_path");
            if (filePath is null)
            {
                continue;
            }

            var language = image.TryGetProperty("iso_639_1", out var iso) && iso.ValueKind == JsonValueKind.String ? iso.GetString() : null;
            images.Add(new RemoteImage(type, language, ImageBaseUrl + filePath, sortOrder++));
        }
    }

    private ProviderMetadata MapDetails(ProviderRef reference, string language, string type, JsonElement root)
    {
        var genres = new List<string>();
        if (root.TryGetProperty("genres", out var genreArray) && genreArray.ValueKind == JsonValueKind.Array)
        {
            genres.AddRange(genreArray.EnumerateArray().Select(genre => GetString(genre, "name")).OfType<string>());
        }

        long? runtimeTicks = type == "movie"
            ? GetInt(root, "runtime") is { } minutes ? minutes * TimeSpan.TicksPerMinute : null
            : FirstEpisodeRuntimeTicks(root);

        return new ProviderMetadata(
            reference,
            language,
            GetString(root, type == "movie" ? "title" : "name"),
            GetString(root, type == "movie" ? "original_title" : "original_name"),
            GetString(root, "original_language"),
            EmptyToNull(GetString(root, "overview")),
            EmptyToNull(GetString(root, "tagline")),
            genres,
            OfficialRating: ParseOfficialRating(root, type, PreferredRegion(language)),
            CommunityRating: GetDouble(root, "vote_average"),
            ReleaseDate: ParseDate(GetString(root, type == "movie" ? "release_date" : "first_air_date")),
            RuntimeTicks: runtimeTicks,
            Raw: root.GetRawText());
    }

    // The certification (PG-13, TV-MA, 16, …) for the operator's region. TMDb keys it by country, so
    // prefer the region implied by the requested language, then fall back to US, then any available rating.
    private static string? ParseOfficialRating(JsonElement root, string type, string region) => type == "movie"
        ? PickByRegion(root, "release_dates", region, MovieCertification)
        : PickByRegion(root, "content_ratings", region, entry => EmptyToNull(GetString(entry, "rating")));

    private static string? MovieCertification(JsonElement entry)
    {
        if (!entry.TryGetProperty("release_dates", out var dates) || dates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var date in dates.EnumerateArray())
        {
            if (EmptyToNull(GetString(date, "certification")) is { } certification)
            {
                return certification;
            }
        }

        return null;
    }

    private static string? PickByRegion(JsonElement root, string property, string region, Func<JsonElement, string?> select)
    {
        if (!root.TryGetProperty(property, out var container) || container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? fallbackUs = null;
        string? fallbackAny = null;
        foreach (var entry in results.EnumerateArray())
        {
            if (select(entry) is not { } value)
            {
                continue;
            }

            // ValueEquals compares the UTF-8 bytes directly (no string allocation per entry). TMDb
            // returns iso_3166_1 as an upper-case alpha-2 code, and region is upper-cased to match.
            if (entry.TryGetProperty("iso_3166_1", out var iso) && iso.ValueKind == JsonValueKind.String)
            {
                if (iso.ValueEquals(region))
                {
                    return value;
                }

                if (iso.ValueEquals("US"))
                {
                    fallbackUs ??= value;
                }
            }

            fallbackAny ??= value;
        }

        return fallbackUs ?? fallbackAny;
    }

    // "ru-RU" → "RU"; "zh-Hans-CN" → "CN"; a tag with no region (bare "en") defaults to US, TMDb's most
    // complete certification set. The region is the first 2-letter subtag after the language code, so a
    // script subtag (4 letters) between them is skipped.
    private static string PreferredRegion(string language)
    {
        var parts = language.Split('-');
        for (var i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length == 2)
            {
                return parts[i].ToUpperInvariant();
            }
        }

        return "US";
    }

    private static long? FirstEpisodeRuntimeTicks(JsonElement root)
    {
        if (root.TryGetProperty("episode_run_time", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in array.EnumerateArray())
            {
                if (value.TryGetInt32(out var minutes))
                {
                    return minutes * TimeSpan.TicksPerMinute;
                }
            }
        }

        return null;
    }

    private Task<JsonDocument?> GetAsync(string pathWithQuery, CancellationToken cancellationToken) =>
        TmdbRequest.GetAsync(httpClientFactory, settings, logger, pathWithQuery, cancellationToken);

    /// <summary>
    /// Unicode ranges a metadata language's titles are written in, keyed by ISO 639-1 code. Latin-script
    /// languages are absent on purpose: the language-less search already answers in (Latin) en-US, and
    /// characters carry no signal that would pick, say, German over English.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (char First, char Last)[]> ScriptRangesByLanguage =
        new Dictionary<string, (char, char)[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ru"] = [('\u0400', '\u04FF')], // Cyrillic
            ["uk"] = [('\u0400', '\u04FF')],
            ["be"] = [('\u0400', '\u04FF')],
            ["bg"] = [('\u0400', '\u04FF')],
            ["sr"] = [('\u0400', '\u04FF')],
            ["el"] = [('\u0370', '\u03FF')], // Greek
            ["he"] = [('\u0590', '\u05FF')], // Hebrew
            ["ar"] = [('\u0600', '\u06FF')], // Arabic
            ["th"] = [('\u0E00', '\u0E7F')], // Thai
            ["ja"] = [('\u3040', '\u30FF'), ('\u4E00', '\u9FFF')], // Hiragana/Katakana + CJK ideographs
            ["zh"] = [('\u4E00', '\u9FFF')],
            ["ko"] = [('\uAC00', '\uD7AF'), ('\u1100', '\u11FF')], // Hangul syllables + jamo
        };

    /// <summary>
    /// The configured metadata language whose script the query title is (at least partly) written in, or
    /// null for Latin/unknown scripts — the search then runs language-less as before. Any character of the
    /// script counts: a mixed name ("Терминатор Terminator") still wants the localized response title, and
    /// the original-title fallback in the scoring covers its Latin half.
    /// </summary>
    private string? SearchLanguageFor(string title)
    {
        foreach (var language in settings.SupportedLanguages)
        {
            if (ScriptRangesByLanguage.TryGetValue(language.Split('-')[0], out var ranges) &&
                title.Any(character => ranges.Any(range => character >= range.First && character <= range.Last)))
            {
                return language;
            }
        }

        return null;
    }

    private static string TmdbType(MediaKind kind) => kind is MediaKind.Movie or MediaKind.Video ? "movie" : "tv";

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? ParseYear(string? date) => ParseDate(date)?.Year;

    private static DateTimeOffset? ParseDate(string? date) =>
        DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static int? GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static double? GetDouble(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number)
            ? number
            : null;
}
