using System.Text.Json;
using System.Text.Json.Serialization;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations;

/// <summary>One title TMDb recommends for a seed. The shape persisted in the cache.</summary>
public sealed record TmdbRecommendedTitle(
    [property: JsonPropertyName("id")] string TmdbId,
    [property: JsonPropertyName("t")] string Title,
    [property: JsonPropertyName("y")] int? Year,
    [property: JsonPropertyName("p")] string? PosterPath);

/// <summary>Where per-title recommendations come from. A seam: the engine does not care that the
/// real one is HTTP plus a cache.</summary>
public interface ITmdbRecommendationSource
{
    /// <summary>Recommendations for one seed title; empty when the source has nothing or cannot answer.</summary>
    Task<IReadOnlyList<TmdbRecommendedTitle>> ForSeedAsync(
        RecommendationIdentity seed, CancellationToken cancellationToken);
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
    public async Task<IReadOnlyList<TmdbRecommendedTitle>> ForSeedAsync(
        RecommendationIdentity seed, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var cached = await database.TmdbRecommendationCache
            .FirstOrDefaultAsync(row => row.Kind == seed.Kind && row.TmdbId == seed.TmdbId, cancellationToken);

        // A stale row is a miss, not a lie: the TTL is enforced on read so an outage cannot serve
        // month-old data indefinitely, and the row is reused as the write target below.
        if (cached is not null && now - cached.FetchedAt < CacheLifetime)
        {
            return Deserialize(cached.Payload);
        }

        var fetched = await FetchAsync(seed, cancellationToken);
        if (fetched is null)
        {
            // TMDb did not answer. Serving the stale payload beats an empty feed — it was accurate a
            // week ago and recommendations are not time-critical.
            return cached is not null ? Deserialize(cached.Payload) : [];
        }

        await StoreAsync(cached, seed, fetched, now, cancellationToken);
        return fetched;
    }

    private async Task<IReadOnlyList<TmdbRecommendedTitle>?> FetchAsync(
        RecommendationIdentity seed, CancellationToken cancellationToken)
    {
        var segment = seed.Kind == RecommendationKind.Movie ? "movie" : "tv";
        var language = settings.SupportedLanguages.Count > 0 ? settings.SupportedLanguages[0] : "en-US";

        JsonDocument? document;
        try
        {
            document = await TmdbRequest.GetAsync(
                httpClientFactory,
                settings,
                logger,
                $"{segment}/{seed.TmdbId}/recommendations?language={Uri.EscapeDataString(language)}",
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "TMDb recommendations for {Seed} could not be fetched.", seed);
            return null;
        }

        if (document is null)
        {
            return null;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var titles = new List<TmdbRecommendedTitle>(PerSeed);
            foreach (var element in results.EnumerateArray().Take(PerSeed))
            {
                if (Read(element, seed.Kind) is { } title)
                {
                    titles.Add(title);
                }
            }

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
            Text(element, "poster_path"));

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private async Task StoreAsync(
        TmdbRecommendationCacheEntry? existing,
        RecommendationIdentity seed,
        IReadOnlyList<TmdbRecommendedTitle> titles,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(titles, Json);
        if (existing is not null)
        {
            existing.Payload = payload;
            existing.FetchedAt = now;
        }
        else
        {
            database.TmdbRecommendationCache.Add(new TmdbRecommendationCacheEntry
            {
                Id = Guid.NewGuid(), Kind = seed.Kind, TmdbId = seed.TmdbId, Payload = payload, FetchedAt = now,
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
            logger.LogDebug(exception, "A concurrent write already cached TMDb recommendations for {Seed}.", seed);
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
