using System.Globalization;
using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;

namespace MediaServer.Api.Metadata;

/// <summary>
/// TMDb implementation of <see cref="IReleaseScheduleProvider"/>. Buckets the raw movie
/// <c>release_dates</c> codes into app <see cref="ReleaseType"/>s — Premiere (1) kept separate,
/// Theatrical merging limited (2) + wide (3) to the earliest, Digital (4), Physical (5) / TV (6)
/// dropped — per requested watch region. Series get the title-level <c>next_episode_to_air</c> /
/// <c>last_episode_to_air</c> from one <c>/tv/{id}</c> call, and opt-in per-season episode enumeration.
/// Regions here come from <c>WATCH_REGION</c> / per-entry overrides, deliberately independent of
/// <c>SUPPORTED_LANGUAGES</c> (the certification path in <see cref="TmdbMetadataProvider"/> stays
/// region-by-language).
/// </summary>
public sealed class TmdbReleaseScheduleProvider(
    IHttpClientFactory httpClientFactory,
    MediaServerSettings settings,
    ILogger<TmdbReleaseScheduleProvider> logger)
    : IReleaseScheduleProvider
{
    // Chip/drawer-row rendition; matches the search-candidate thumbnails.
    private const string PosterThumbBaseUrl = "https://image.tmdb.org/t/p/w154";

    // TMDb raw release_dates type codes.
    private const int RawPremiere = 1;
    private const int RawTheatricalLimited = 2;
    private const int RawTheatrical = 3;
    private const int RawDigital = 4;

    public string Key => "tmdb";

    public async Task<MovieReleaseSchedule?> GetMovieScheduleAsync(
        string providerId, IReadOnlyCollection<string> regions, CancellationToken cancellationToken)
    {
        // One call: the base movie payload (status/title/poster) plus release_dates appended.
        var document = await GetAsync($"movie/{providerId}?append_to_response=release_dates", cancellationToken);
        if (document is null)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            return new MovieReleaseSchedule(
                EmptyToNull(GetString(root, "title")) ?? EmptyToNull(GetString(root, "original_title")),
                ParseYear(GetString(root, "release_date")),
                PosterUrl(root),
                EmptyToNull(GetString(root, "status")),
                ParseTypedDates(root, regions));
        }
    }

    public async Task<SeriesReleaseSchedule?> GetSeriesScheduleAsync(string providerId, CancellationToken cancellationToken)
    {
        var document = await GetAsync($"tv/{providerId}", cancellationToken);
        if (document is null)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;

            var seasons = new List<int>();
            if (root.TryGetProperty("seasons", out var seasonArray) && seasonArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var season in seasonArray.EnumerateArray())
                {
                    if (GetInt(season, "season_number") is { } number)
                    {
                        seasons.Add(number);
                    }
                }
            }

            return new SeriesReleaseSchedule(
                EmptyToNull(GetString(root, "name")) ?? EmptyToNull(GetString(root, "original_name")),
                ParseYear(GetString(root, "first_air_date")),
                PosterUrl(root),
                EmptyToNull(GetString(root, "status")),
                ParseEpisode(root, "next_episode_to_air"),
                ParseEpisode(root, "last_episode_to_air"),
                seasons);
        }
    }

    public async Task<IReadOnlyList<EpisodeAirDate>> GetSeasonEpisodesAsync(
        string providerId, int season, CancellationToken cancellationToken)
    {
        var document = await GetAsync($"tv/{providerId}/season/{season}", cancellationToken);
        if (document is null)
        {
            return [];
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("episodes", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var episodes = new List<EpisodeAirDate>();
            foreach (var episode in array.EnumerateArray())
            {
                // An episode without an air date isn't a release event yet; it appears once TMDb dates it.
                if (ParseEpisodeElement(episode) is { } parsed)
                {
                    episodes.Add(parsed);
                }
            }

            return episodes;
        }
    }

    /// <summary>
    /// Buckets a movie's raw <c>release_dates</c> into app types for each requested region: one entry per
    /// <c>(region, app type)</c>, resolving multiples to the earliest date. Theatrical merges limited (2)
    /// and wide (3) — the first time it hits cinemas — keeping the winning entry's raw code; Premiere (1)
    /// stays separate (not a public release); Physical (5) and TV (6) are intentionally dropped.
    /// </summary>
    internal static IReadOnlyList<TypedReleaseDate> ParseTypedDates(JsonElement root, IReadOnlyCollection<string> regions)
    {
        var dates = new List<TypedReleaseDate>();
        if (!root.TryGetProperty("release_dates", out var container) || container.ValueKind != JsonValueKind.Object ||
            !container.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return dates;
        }

        foreach (var entry in results.EnumerateArray())
        {
            var region = GetString(entry, "iso_3166_1");
            if (region is null || !regions.Contains(region, StringComparer.OrdinalIgnoreCase) ||
                !entry.TryGetProperty("release_dates", out var regionDates) || regionDates.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            // Earliest raw entry per app bucket for this region.
            var buckets = new Dictionary<ReleaseType, TypedReleaseDate>();
            foreach (var date in regionDates.EnumerateArray())
            {
                var rawType = GetInt(date, "type");
                var parsedDate = ParseDate(GetString(date, "release_date"));
                if (rawType is null || parsedDate is null)
                {
                    continue;
                }

                ReleaseType type;
                switch (rawType.Value)
                {
                    case RawPremiere:
                        type = ReleaseType.Premiere;
                        break;
                    case RawTheatricalLimited:
                    case RawTheatrical:
                        type = ReleaseType.Theatrical;
                        break;
                    case RawDigital:
                        type = ReleaseType.Digital;
                        break;
                    default:
                        continue; // Physical (5) / TV (6): weakly applicable to a self-hosted flow — dropped.
                }

                var candidate = new TypedReleaseDate(
                    region.ToUpperInvariant(), type, rawType.Value, parsedDate.Value, EmptyToNull(GetString(date, "note")));
                if (!buckets.TryGetValue(type, out var existing) || candidate.Date < existing.Date)
                {
                    buckets[type] = candidate;
                }
            }

            dates.AddRange(buckets.Values.OrderBy(date => date.Type));
        }

        return dates;
    }

    private static EpisodeAirDate? ParseEpisode(JsonElement root, string property) =>
        root.TryGetProperty(property, out var episode) && episode.ValueKind == JsonValueKind.Object
            ? ParseEpisodeElement(episode)
            : null;

    private static EpisodeAirDate? ParseEpisodeElement(JsonElement episode)
    {
        var season = GetInt(episode, "season_number");
        var number = GetInt(episode, "episode_number");
        var airDate = ParseDate(GetString(episode, "air_date"));
        return season is null || number is null || airDate is null
            ? null
            : new EpisodeAirDate(season.Value, number.Value, airDate.Value, EmptyToNull(GetString(episode, "name")));
    }

    private Task<JsonDocument?> GetAsync(string pathWithQuery, CancellationToken cancellationToken) =>
        TmdbRequest.GetAsync(httpClientFactory, settings, logger, pathWithQuery, cancellationToken);

    private static string? PosterUrl(JsonElement root) =>
        EmptyToNull(GetString(root, "poster_path")) is { } path ? PosterThumbBaseUrl + path : null;

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? ParseYear(string? date) => ParseDate(date)?.Year;

    private static DateTimeOffset? ParseDate(string? date) =>
        DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;
}
