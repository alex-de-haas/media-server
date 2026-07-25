using System.Text.Json;
using MediaServer.Api.Data;

namespace MediaServer.Api.Metadata;

/// <summary>A name plus its (optional) absolute logo URL — networks and production companies share this shape.</summary>
public sealed record TmdbBrand(string Name, string? LogoUrl);

/// <summary>One billed cast member as the payload records them; <see cref="ProviderId"/> is the TMDb person id.</summary>
public sealed record TmdbCastMember(string ProviderId, string Name, string? Character, string? ProfileUrl);

/// <summary>
/// The facts a TMDb detail payload carries beyond the localized columns: crew, brands, artwork paths,
/// external ids, the trailer, and the counts.
/// </summary>
public sealed record TmdbPayloadFacts
{
    public static readonly TmdbPayloadFacts Empty = new();

    /// <summary>Empty for movies — networks are a series concept.</summary>
    public IReadOnlyList<TmdbBrand> Networks { get; init; } = [];

    public IReadOnlyList<TmdbBrand> Studios { get; init; } = [];

    public IReadOnlyList<string> Directors { get; init; } = [];

    public IReadOnlyList<string> Creators { get; init; } = [];

    public IReadOnlyList<string> Keywords { get; init; } = [];

    /// <summary>Top-billed cast, in the payload's own billing order, capped at <see cref="MaxCast"/>.</summary>
    public IReadOnlyList<TmdbCastMember> Cast { get; init; } = [];

    public string? Status { get; init; }

    public int? VoteCount { get; init; }

    public int? SeasonCount { get; init; }

    public int? EpisodeCount { get; init; }

    public string? CollectionName { get; init; }

    public string? Homepage { get; init; }

    public string? ImdbId { get; init; }

    public string? TrailerUrl { get; init; }

    /// <summary>Artwork straight from the detail payload; the library reads its own language-matched assets instead.</summary>
    public string? PosterUrl { get; init; }

    public string? BackdropUrl { get; init; }
}

/// <summary>
/// Reads a cached TMDb detail payload (the <c>append_to_response</c> document
/// <see cref="TmdbMetadataProvider.FetchAsync"/> stores in <c>MetadataRecord.Raw</c>) into
/// <see cref="TmdbPayloadFacts"/>.
/// </summary>
/// <remarks>
/// Shared on purpose: a library detail page and a preview of a title nobody holds are two readers of the
/// same document, and the facts they state about a title must not drift apart. Everything here is derived
/// at read time rather than persisted per field — the payload is already stored whole.
/// </remarks>
public static class TmdbPayload
{
    /// <summary>The TMDb image CDN base for the raw <c>*_path</c> values inside a payload.</summary>
    public const string ImageBaseUrl = "https://image.tmdb.org/t/p/original";

    private const int MaxCast = 20;
    private const int MaxKeywords = 16;

    /// <summary>
    /// Parses <paramref name="raw"/>; a null, empty or malformed payload yields
    /// <see cref="TmdbPayloadFacts.Empty"/> rather than throwing, because a detail view is worth
    /// rendering from the columns alone.
    /// </summary>
    public static TmdbPayloadFacts Parse(string? raw, MediaKind kind)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TmdbPayloadFacts.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return TmdbPayloadFacts.Empty;
            }

            return new TmdbPayloadFacts
            {
                // Networks are a series concept; keep them empty for movies so the UI hides the rail.
                Networks = kind == MediaKind.Series ? ParseBrands(root, "networks") : [],
                Studios = ParseBrands(root, "production_companies"),
                Directors = ParseCrewJob(root, "Director"),
                Creators = ParseNames(root, "created_by"),
                Keywords = ParseKeywords(root),
                Cast = ParseCast(root),
                Status = EmptyToNull(JsonString(root, "status")),
                VoteCount = JsonInt(root, "vote_count"),
                SeasonCount = JsonInt(root, "number_of_seasons"),
                EpisodeCount = JsonInt(root, "number_of_episodes"),
                CollectionName = ParseCollectionName(root),
                Homepage = EmptyToNull(JsonString(root, "homepage")),
                ImdbId = ParseImdbId(root),
                TrailerUrl = ParseTrailerUrl(root),
                PosterUrl = ImageUrl(JsonString(root, "poster_path")),
                BackdropUrl = ImageUrl(JsonString(root, "backdrop_path")),
            };
        }
        catch (JsonException)
        {
            return TmdbPayloadFacts.Empty;
        }
    }

    /// <summary>Absolute URL for a raw TMDb <c>*_path</c> value, or null when there is none.</summary>
    public static string? ImageUrl(string? path) => string.IsNullOrWhiteSpace(path) ? null : ImageBaseUrl + path;

    /// <summary>
    /// The localized columns of a detail payload — title, overview, genres, certification, rating, release
    /// date and runtime — with the whole document carried along as <see cref="ProviderMetadata.Raw"/>.
    /// </summary>
    /// <remarks>
    /// Used both when a payload arrives from TMDb and when one is read back out of a cache, so a title's
    /// facts do not depend on which of the two produced them.
    /// </remarks>
    public static ProviderMetadata MapDetails(ProviderRef reference, string language, MediaKind kind, JsonElement root)
    {
        var movie = kind is MediaKind.Movie or MediaKind.Video;

        var genres = new List<string>();
        if (root.TryGetProperty("genres", out var genreArray) && genreArray.ValueKind == JsonValueKind.Array)
        {
            genres.AddRange(genreArray.EnumerateArray().Select(genre => JsonText(genre, "name")).OfType<string>());
        }

        long? runtimeTicks = movie
            ? JsonInt(root, "runtime") is { } minutes ? minutes * TimeSpan.TicksPerMinute : null
            : FirstEpisodeRuntimeTicks(root);

        return new ProviderMetadata(
            reference,
            language,
            JsonText(root, movie ? "title" : "name"),
            JsonText(root, movie ? "original_title" : "original_name"),
            JsonText(root, "original_language"),
            EmptyToNull(JsonText(root, "overview")),
            EmptyToNull(JsonText(root, "tagline")),
            genres,
            OfficialRating: ParseOfficialRating(root, movie, PreferredRegion(language)),
            CommunityRating: JsonDouble(root, "vote_average"),
            ReleaseDate: ParseDate(JsonText(root, movie ? "release_date" : "first_air_date")),
            RuntimeTicks: runtimeTicks,
            Raw: root.GetRawText());
    }

    // The certification (PG-13, TV-MA, 16, …) for the operator's region. TMDb keys it by country, so
    // prefer the region implied by the requested language, then fall back to US, then any available rating.
    private static string? ParseOfficialRating(JsonElement root, bool movie, string region) => movie
        ? PickByRegion(root, "release_dates", region, MovieCertification)
        : PickByRegion(root, "content_ratings", region, entry => EmptyToNull(JsonText(entry, "rating")));

    private static string? MovieCertification(JsonElement entry)
    {
        if (!entry.TryGetProperty("release_dates", out var dates) || dates.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var date in dates.EnumerateArray())
        {
            if (EmptyToNull(JsonText(date, "certification")) is { } certification)
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

    private static DateTimeOffset? ParseDate(string? date) =>
        DateTimeOffset.TryParse(
            date, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    // A name + absolute logo url, shared by networks and production companies (identical TMDb shape).
    private static IReadOnlyList<TmdbBrand> ParseBrands(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var brands = new List<TmdbBrand>();
        foreach (var element in array.EnumerateArray())
        {
            if (EmptyToNull(JsonString(element, "name")) is { } name)
            {
                brands.Add(new TmdbBrand(name, ImageUrl(JsonString(element, "logo_path"))));
            }
        }

        return brands;
    }

    // Billed cast from the embedded credits, in TMDb's own order. The library detail page reads cast from
    // the normalized Person join instead — it has stable local ids — but a title nobody holds has no such
    // rows, so a preview parses the same people out of the payload.
    private static IReadOnlyList<TmdbCastMember> ParseCast(JsonElement root)
    {
        if (!TryGetArray(root, "credits", "cast", out var cast))
        {
            return [];
        }

        var members = new List<TmdbCastMember>(MaxCast);
        foreach (var member in cast.EnumerateArray())
        {
            if (member.ValueKind != JsonValueKind.Object
                || !member.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number
                || EmptyToNull(JsonString(member, "name")) is not { } name)
            {
                continue;
            }

            members.Add(new TmdbCastMember(
                id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
                name,
                EmptyToNull(JsonString(member, "character")),
                ImageUrl(JsonString(member, "profile_path"))));

            if (members.Count >= MaxCast)
            {
                break;
            }
        }

        return members;
    }

    private static IReadOnlyList<string> ParseCrewJob(JsonElement root, string job)
    {
        if (!TryGetArray(root, "credits", "crew", out var crew))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var member in crew.EnumerateArray())
        {
            if (JsonEquals(member, "job", job) &&
                EmptyToNull(JsonString(member, "name")) is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static IReadOnlyList<string> ParseNames(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var element in array.EnumerateArray())
        {
            if (EmptyToNull(JsonString(element, "name")) is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // Keywords nest under keywords.keywords for movies and keywords.results for tv.
    private static IReadOnlyList<string> ParseKeywords(JsonElement root)
    {
        if (!root.TryGetProperty("keywords", out var keywords) || keywords.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        JsonElement array;
        if (keywords.TryGetProperty("keywords", out var movie) && movie.ValueKind == JsonValueKind.Array)
        {
            array = movie;
        }
        else if (keywords.TryGetProperty("results", out var series) && series.ValueKind == JsonValueKind.Array)
        {
            array = series;
        }
        else
        {
            return [];
        }

        var names = new List<string>();
        foreach (var keyword in array.EnumerateArray())
        {
            if (EmptyToNull(JsonString(keyword, "name")) is { } name)
            {
                names.Add(name);
            }

            if (names.Count >= MaxKeywords)
            {
                break;
            }
        }

        return names;
    }

    private static string? ParseCollectionName(JsonElement root) =>
        root.TryGetProperty("belongs_to_collection", out var collection) && collection.ValueKind == JsonValueKind.Object
            ? EmptyToNull(JsonString(collection, "name"))
            : null;

    private static string? ParseImdbId(JsonElement root)
    {
        if (root.TryGetProperty("external_ids", out var external) && external.ValueKind == JsonValueKind.Object &&
            EmptyToNull(JsonString(external, "imdb_id")) is { } id)
        {
            return id;
        }

        // Movies also carry imdb_id at the top level.
        return EmptyToNull(JsonString(root, "imdb_id"));
    }

    // The best YouTube trailer: an official trailer, then any trailer, then any YouTube clip.
    private static string? ParseTrailerUrl(JsonElement root)
    {
        if (!TryGetArray(root, "videos", "results", out var results))
        {
            return null;
        }

        string? official = null;
        string? trailer = null;
        string? anyYoutube = null;
        foreach (var video in results.EnumerateArray())
        {
            if (!JsonEquals(video, "site", "YouTube") || EmptyToNull(JsonString(video, "key")) is not { } key)
            {
                continue;
            }

            var url = "https://www.youtube.com/watch?v=" + key;
            anyYoutube ??= url;
            if (!JsonEquals(video, "type", "Trailer"))
            {
                continue;
            }

            trailer ??= url;
            if (video.TryGetProperty("official", out var isOfficial) && isOfficial.ValueKind == JsonValueKind.True)
            {
                official ??= url;
            }
        }

        return official ?? trailer ?? anyYoutube;
    }

    private static bool TryGetArray(JsonElement root, string objectProperty, string arrayProperty, out JsonElement array)
    {
        array = default;
        if (root.TryGetProperty(objectProperty, out var container) && container.ValueKind == JsonValueKind.Object &&
            container.TryGetProperty(arrayProperty, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            array = value;
            return true;
        }

        return false;
    }

    private static string? JsonString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Ordinal compare against a string-valued property, allocation-free (ValueEquals reads the UTF-8 bytes).
    // The ValueKind guard keeps it from throwing on a non-string value, unlike a bare ValueEquals call.
    private static bool JsonEquals(JsonElement element, string property, string value) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var prop) &&
        prop.ValueKind == JsonValueKind.String && prop.ValueEquals(value);

    // Like JsonString, but tolerates a number where TMDb sometimes returns one (an id rendered as a value).
    private static string? JsonText(JsonElement element, string property)
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

    private static double? JsonDouble(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetDouble(out var number)
            ? number
            : null;

    private static int? JsonInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
