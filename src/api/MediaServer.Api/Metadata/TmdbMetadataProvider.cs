using System.Globalization;
using System.Net.Http.Headers;
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

    public string Key => "tmdb";

    public async Task<IReadOnlyList<MetadataCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        var type = TmdbType(query.Kind);
        var yearParam = query.Year is { } year
            ? type == "movie" ? $"&year={year}" : $"&first_air_date_year={year}"
            : string.Empty;

        var path = $"search/{type}?query={Uri.EscapeDataString(query.Title)}{yearParam}";
        var document = await GetAsync(path, cancellationToken);
        if (document is null || !document.RootElement.TryGetProperty("results", out var results))
        {
            return [];
        }

        var candidates = new List<MetadataCandidate>();
        foreach (var result in results.EnumerateArray())
        {
            var id = GetString(result, "id");
            var title = GetString(result, type == "movie" ? "title" : "name")
                        ?? GetString(result, type == "movie" ? "original_title" : "original_name");
            if (id is null || title is null)
            {
                continue;
            }

            var candidateYear = ParseYear(GetString(result, type == "movie" ? "release_date" : "first_air_date"));
            var score = TitleScoring.Score(query.Title, query.Year, title, candidateYear);
            candidates.Add(new MetadataCandidate(new ProviderRef(Key, id), title, candidateYear, score));
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

        var records = new List<ProviderMetadata>(languages.Count);
        foreach (var language in languages)
        {
            var document = await GetAsync($"{type}/{reference.Id}?language={Uri.EscapeDataString(language)}", cancellationToken);
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
            OfficialRating: null,
            CommunityRating: GetDouble(root, "vote_average"),
            ReleaseDate: ParseDate(GetString(root, type == "movie" ? "release_date" : "first_air_date")),
            RuntimeTicks: runtimeTicks,
            Raw: root.GetRawText());
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

    private async Task<JsonDocument?> GetAsync(string pathWithQuery, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.TmdbApiKey))
        {
            throw new InvalidOperationException("TMDB_API_KEY is not configured.");
        }

        var key = settings.TmdbApiKey;
        // TMDb accepts either a v3 API key (sent as the api_key query parameter) or a v4 API Read Access
        // Token — a JWT — sent as a Bearer header. Detect which the operator configured so pasting either
        // works; a v4 token sent as api_key would be rejected with 401 (and vice-versa).
        var useBearer = key.Contains('.');

        var client = httpClientFactory.CreateClient(HttpClientName);
        var requestUri = useBearer
            ? $"3/{pathWithQuery}"
            : $"3/{pathWithQuery}{(pathWithQuery.Contains('?') ? '&' : '?')}api_key={key}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (useBearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // 401 almost always means a mis-configured credential — surface that at warning level so it
            // is diagnosable, while never logging the api_key query value or the token itself.
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                logger.LogWarning(
                    "TMDb rejected the configured TMDB_API_KEY (HTTP 401). Provide a valid v3 API key or v4 API Read Access Token.");
            }
            else
            {
                logger.LogDebug("TMDb request {Path} returned {StatusCode}.", pathWithQuery, (int)response.StatusCode);
            }

            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
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
