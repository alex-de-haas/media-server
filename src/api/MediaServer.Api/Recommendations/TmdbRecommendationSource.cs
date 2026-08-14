using System.Text.Json;
using System.Text.Json.Serialization;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations;

/// <summary>One title TMDb recommends for a seed. The shape persisted in the cache.</summary>
/// <remarks>
/// Everything after the poster comes free with the request that was already being made: a TMDb list
/// object carries genres, votes, popularity and original language, and the old projection threw them
/// away. Since only the projection is persisted, discarding them meant the scorer could never see a
/// feature without a second fetch — so widening this is what makes the quality and popularity terms
/// possible at zero additional cost. Property names are short because this is stored per seed × 20.
/// </remarks>
public sealed record TmdbRecommendedTitle(
    [property: JsonPropertyName("id")] string TmdbId,
    [property: JsonPropertyName("t")] string Title,
    [property: JsonPropertyName("y")] int? Year,
    [property: JsonPropertyName("p")] string? PosterPath,
    [property: JsonPropertyName("g")] IReadOnlyList<int>? GenreIds = null,
    [property: JsonPropertyName("va")] double? VoteAverage = null,
    [property: JsonPropertyName("vc")] int? VoteCount = null,
    [property: JsonPropertyName("pop")] double? Popularity = null,
    [property: JsonPropertyName("lang")] string? OriginalLanguage = null);

/// <summary>Where per-title recommendations come from. A seam: the engine does not care that the
/// real one is HTTP plus a cache.</summary>
public interface ITmdbRecommendationSource
{
    /// <summary>
    /// One seed title's list, from the named generator; empty when the source has nothing or cannot
    /// answer.
    /// </summary>
    Task<IReadOnlyList<TmdbRecommendedTitle>> ForSeedAsync(
        RecommendationIdentity seed, TmdbRecommendationGenerator generator, CancellationToken cancellationToken);

    /// <summary>
    /// Any cached TMDb list, for generators whose question is not "what is like this title".
    /// </summary>
    /// <param name="cacheKey">
    /// What makes this list unique inside its generator — a person id, or a hash of the facets a
    /// discovery query was built from. Capped at the cache column's width by the caller.
    /// </param>
    /// <param name="path">The API path to fetch on a miss, query string included.</param>
    /// <param name="lifetime">How long the answer stays usable; a person's filmography moves slowly.</param>
    /// <param name="arrays">
    /// Which properties of the response hold titles. <c>results</c> for most, but credits arrive under
    /// <c>cast</c> and <c>crew</c>.
    /// </param>
    Task<IReadOnlyList<TmdbRecommendedTitle>> ForListAsync(
        TmdbRecommendationGenerator generator,
        RecommendationKind kind,
        string cacheKey,
        string path,
        TimeSpan lifetime,
        IReadOnlyList<string> arrays,
        CancellationToken cancellationToken);
}

/// <summary>
/// Reads TMDb's per-title recommendations, through a database cache.
/// </summary>
/// <remarks>
/// Cached because the fan-out is per seed: a cold user costs one request per seed title, and without
/// a cache every page view would repeat them. The lists themselves move slowly — they are aggregate
/// behavior over TMDb's whole audience — so a multi-day TTL costs nothing in freshness.
///
/// The cache is keyed by the seed title alone and is therefore shared across users. That is safe by
/// construction: a row records what TMDb says about a public title, never who asked for it.
/// </remarks>
public sealed class TmdbRecommendationSource(
    MediaServerDbContext database,
    IHttpClientFactory httpClientFactory,
    MediaServerSettings settings,
    TimeProvider time,
    ILogger<TmdbRecommendationSource> logger) : ITmdbRecommendationSource
{
    /// <summary>How long a cached list stays usable. Recommendation lists change on the order of weeks.</summary>
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// The shape <see cref="TmdbRecommendedTitle"/> is written in today. A row at any other version is
    /// a miss: rows written before the projection widened would otherwise deserialize cleanly with
    /// every new field null, and be scored as titles with no votes rather than titles nobody asked
    /// about.
    /// </summary>
    internal const int PayloadVersion = 2;

    /// <summary>TMDb returns 20 per page; one page per seed is plenty once several seeds are aggregated.</summary>
    private const int PerSeed = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Recommendations for one seed title, from cache when fresh and from TMDb otherwise.
    /// </summary>
    /// <remarks>
    /// Returns an empty list rather than throwing when TMDb is unreachable or the title is unknown:
    /// one bad seed must not cost the user their whole feed.
    /// </remarks>
    public Task<IReadOnlyList<TmdbRecommendedTitle>> ForSeedAsync(
        RecommendationIdentity seed, TmdbRecommendationGenerator generator, CancellationToken cancellationToken)
    {
        var segment = seed.Kind == RecommendationKind.Movie ? "movie" : "tv";
        var list = generator == TmdbRecommendationGenerator.Similar ? "similar" : "recommendations";
        var language = Language();

        return ForListAsync(
            generator,
            seed.Kind,
            seed.TmdbId,
            $"{segment}/{seed.TmdbId}/{list}?language={Uri.EscapeDataString(language)}",
            CacheLifetime,
            ["results"],
            cancellationToken);
    }

    public async Task<IReadOnlyList<TmdbRecommendedTitle>> ForListAsync(
        TmdbRecommendationGenerator generator,
        RecommendationKind kind,
        string cacheKey,
        string path,
        TimeSpan lifetime,
        IReadOnlyList<string> arrays,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var cached = await database.TmdbRecommendationCache.FirstOrDefaultAsync(
            row => row.Generator == generator && row.Kind == kind && row.TmdbId == cacheKey,
            cancellationToken);

        // A stale row is a miss, not a lie: the TTL is enforced on read so an outage cannot serve
        // month-old data indefinitely, and the row is reused as the write target below. A row written
        // in an older shape is a miss for the same reason — see PayloadVersion.
        if (cached is not null && cached.PayloadVersion == PayloadVersion && now - cached.FetchedAt < lifetime)
        {
            return Deserialize(cached.Payload);
        }

        var fetched = await FetchAsync(kind, path, arrays, cancellationToken);
        if (fetched is null)
        {
            // TMDb did not answer. Serving the stale payload beats an empty feed — it was accurate a
            // week ago and recommendations are not time-critical. An older shape still reads: fewer
            // features is a worse answer than the current one, and a better answer than none.
            return cached is not null ? Deserialize(cached.Payload) : [];
        }

        await StoreAsync(cached, generator, kind, cacheKey, fetched, now, cancellationToken);
        return fetched;
    }

    private string Language() => settings.SupportedLanguages.Count > 0 ? settings.SupportedLanguages[0] : "en-US";

    private async Task<IReadOnlyList<TmdbRecommendedTitle>?> FetchAsync(
        RecommendationKind kind, string path, IReadOnlyList<string> arrays, CancellationToken cancellationToken)
    {
        JsonDocument? document;
        try
        {
            document = await TmdbRequest.GetAsync(httpClientFactory, settings, logger, path, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "TMDb list {Path} could not be fetched.", path);
            return null;
        }

        if (document is null)
        {
            return null;
        }

        using (document)
        {
            var titles = new List<TmdbRecommendedTitle>(PerSeed);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var array in arrays)
            {
                if (!document.RootElement.TryGetProperty(array, out var results)
                    || results.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var element in results.EnumerateArray())
                {
                    if (titles.Count >= PerSeed)
                    {
                        break;
                    }

                    // A person's cast and crew arrays overlap whenever they acted in something they
                    // also directed, and the same title twice would count as its own agreement.
                    if (Read(element, kind) is { } title && seen.Add(title.TmdbId))
                    {
                        titles.Add(title);
                    }
                }
            }

            // An answer with no readable rows is still an answer; only a failed request is a null.
            return titles;
        }
    }

    private static TmdbRecommendedTitle? Read(JsonElement element, RecommendationKind kind)
    {
        if (!element.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        // TMDb names the title field differently per media type, and a recommendation list for a
        // series can still contain the other shape.
        var title = Text(element, "title") ?? Text(element, "name");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var date = Text(element, "release_date") ?? Text(element, "first_air_date");
        var year = date is { Length: >= 4 } && int.TryParse(date[..4], out var parsed) ? parsed : (int?)null;

        return new TmdbRecommendedTitle(
            id.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
            title!,
            year,
            Text(element, "poster_path"),
            GenreIds(element),
            Number(element, "vote_average"),
            Number(element, "vote_count") is { } votes ? (int)votes : null,
            Number(element, "popularity"),
            Text(element, "original_language"));

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        static double? Number(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : null;

        static IReadOnlyList<int>? GenreIds(JsonElement element)
        {
            if (!element.TryGetProperty("genre_ids", out var ids) || ids.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            // Null and empty are different answers: null means TMDb was never asked, empty means it
            // was and the title genuinely has none. The scorer treats the two differently.
            var genres = new List<int>(ids.GetArrayLength());
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var genre))
                {
                    genres.Add(genre);
                }
            }

            return genres;
        }
    }

    private async Task StoreAsync(
        TmdbRecommendationCacheEntry? existing,
        TmdbRecommendationGenerator generator,
        RecommendationKind kind,
        string cacheKey,
        IReadOnlyList<TmdbRecommendedTitle> titles,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(titles, Json);
        if (existing is not null)
        {
            existing.Payload = payload;
            existing.PayloadVersion = PayloadVersion;
            existing.FetchedAt = now;
        }
        else
        {
            database.TmdbRecommendationCache.Add(new TmdbRecommendationCacheEntry
            {
                Id = Guid.NewGuid(), Generator = generator, Kind = kind, TmdbId = cacheKey,
                Payload = payload, PayloadVersion = PayloadVersion, FetchedAt = now,
            });
        }

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Two users can seed the same title at once and race on the unique index. The fetch
            // already succeeded, so the caller gets its answer; the other writer's row is equally good.
            logger.LogDebug(
                exception, "A concurrent write already cached this TMDb list ({Generator}/{Key}).", generator, cacheKey);
            database.ChangeTracker.Clear();
        }
    }

    private static IReadOnlyList<TmdbRecommendedTitle> Deserialize(string payload)
    {
        try
        {
            return JsonSerializer.Deserialize<List<TmdbRecommendedTitle>>(payload, Json) ?? [];
        }
        catch (JsonException)
        {
            // A payload written by an older shape is worth re-fetching, not crashing over.
            return [];
        }
    }
}
