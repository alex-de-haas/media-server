using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;

namespace MediaServer.Api.Metadata;

/// <summary>
/// TMDb implementation of <see cref="IMetadataProvider"/> for all catalog types in v1. Searches and
/// scores candidates, fetches localized details for every supported language, and lists language-tagged
/// images. The API key is sent as a query parameter and never logged (see <c>docs/planning/security.md</c>).
/// </summary>
public sealed class TmdbMetadataProvider(IHttpClientFactory httpClientFactory, MediaServerSettings settings, ILogger<TmdbMetadataProvider> logger)
    : IMetadataProvider
{
    public const string HttpClientName = "tmdb";
    private const string ImageBaseUrl = "https://image.tmdb.org/t/p/original";

    // The provider is a singleton; cache movie-vs-tv resolution so enrich + images don't re-probe.
    private readonly ConcurrentDictionary<string, string> _resolvedTypes = new(StringComparer.Ordinal);

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
        ProviderRef reference, IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        // The canonical kind is encoded by which endpoint resolves; movies and series share the shape.
        var type = await ResolveTypeAsync(reference, cancellationToken);
        if (type is null)
        {
            return [];
        }

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
        ProviderRef reference, IReadOnlyList<string> languages, CancellationToken cancellationToken)
    {
        var type = await ResolveTypeAsync(reference, cancellationToken);
        if (type is null)
        {
            return [];
        }

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

    /// <summary>Probes whether a reference id is a movie or tv id (the parsed kind is not always trusted).</summary>
    private async Task<string?> ResolveTypeAsync(ProviderRef reference, CancellationToken cancellationToken)
    {
        if (_resolvedTypes.TryGetValue(reference.Id, out var cached))
        {
            return cached;
        }

        if (await GetAsync($"movie/{reference.Id}?language=en-US", cancellationToken) is not null)
        {
            return _resolvedTypes[reference.Id] = "movie";
        }

        if (await GetAsync($"tv/{reference.Id}?language=en-US", cancellationToken) is not null)
        {
            return _resolvedTypes[reference.Id] = "tv";
        }

        return null;
    }

    private async Task<JsonDocument?> GetAsync(string pathWithQuery, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.TmdbApiKey))
        {
            throw new InvalidOperationException("TMDB_API_KEY is not configured.");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        var separator = pathWithQuery.Contains('?') ? '&' : '?';
        var requestUri = $"3/{pathWithQuery}{separator}api_key={settings.TmdbApiKey}";

        using var response = await client.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Log only the api_key-free path, never the full request URI.
            logger.LogDebug("TMDb request {Path} returned {StatusCode}.", pathWithQuery, (int)response.StatusCode);
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
