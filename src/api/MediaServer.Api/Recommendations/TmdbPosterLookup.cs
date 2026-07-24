using System.Text.Json;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations;

/// <summary>Fills in artwork for candidates whose source did not supply any.</summary>
public interface ITmdbPosterLookup
{
    /// <summary>
    /// Poster URLs for the given titles, keyed by identity. Titles TMDb has no poster for are simply
    /// absent; the caller renders a placeholder.
    /// </summary>
    Task<IReadOnlyDictionary<RecommendationIdentity, string>> ForAsync(
        IReadOnlyCollection<RecommendationIdentity> identities, CancellationToken cancellationToken);
}

/// <summary>
/// Looks up one title's poster from TMDb, cached aggressively.
/// </summary>
/// <remarks>
/// Trakt returns no artwork at all, so without this every Trakt-only suggestion renders as a grey
/// box — which reads as a broken feed rather than a working one. The lookup is one request per
/// title, so it is bounded to what the caller is actually about to display and cached with a long
/// life: a poster path changes about as often as the film's title does.
///
/// A title TMDb genuinely has no poster for is cached as a null, so a missing poster costs one
/// request ever rather than one per page view.
/// </remarks>
public sealed class TmdbPosterLookup(
    MediaServerDbContext database,
    IHttpClientFactory httpClientFactory,
    MediaServerSettings settings,
    TimeProvider time,
    ILogger<TmdbPosterLookup> logger) : ITmdbPosterLookup
{
    internal static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(30);

    public async Task<IReadOnlyDictionary<RecommendationIdentity, string>> ForAsync(
        IReadOnlyCollection<RecommendationIdentity> identities, CancellationToken cancellationToken)
    {
        var result = new Dictionary<RecommendationIdentity, string>();
        if (identities.Count == 0)
        {
            return result;
        }

        var now = time.GetUtcNow();
        var ids = identities.Select(identity => identity.TmdbId).Distinct().ToList();
        var cached = await database.TmdbPosterCache
            .Where(row => ids.Contains(row.TmdbId))
            .ToListAsync(cancellationToken);

        var byIdentity = cached.ToDictionary(row => new RecommendationIdentity(row.Kind, row.TmdbId));
        var wrote = false;

        foreach (var identity in identities.Distinct())
        {
            if (byIdentity.TryGetValue(identity, out var row) && now - row.FetchedAt < CacheLifetime)
            {
                if (row.PosterPath is { Length: > 0 } path)
                {
                    result[identity] = Url(path);
                }

                continue;
            }

            var fetched = await FetchAsync(identity, cancellationToken);
            if (fetched.Failed)
            {
                // Do not cache an outage as "no poster": that would blank the title for a month.
                continue;
            }

            if (row is null)
            {
                database.TmdbPosterCache.Add(new TmdbPosterCacheEntry
                {
                    Id = Guid.NewGuid(), Kind = identity.Kind, TmdbId = identity.TmdbId,
                    PosterPath = fetched.PosterPath, FetchedAt = now,
                });
            }
            else
            {
                row.PosterPath = fetched.PosterPath;
                row.FetchedAt = now;
            }

            wrote = true;
            if (fetched.PosterPath is { Length: > 0 } freshPath)
            {
                result[identity] = Url(freshPath);
            }
        }

        if (wrote)
        {
            try
            {
                await database.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
            {
                // Another request cached the same title first; its row is equally good.
                logger.LogDebug(exception, "A concurrent write already cached a TMDb poster.");
                database.ChangeTracker.Clear();
            }
        }

        return result;
    }

    private async Task<(bool Failed, string? PosterPath)> FetchAsync(
        RecommendationIdentity identity, CancellationToken cancellationToken)
    {
        var segment = identity.Kind == RecommendationKind.Movie ? "movie" : "tv";
        try
        {
            using var document = await TmdbRequest.GetAsync(
                httpClientFactory, settings, logger, $"{segment}/{identity.TmdbId}", cancellationToken);

            if (document is null)
            {
                return (Failed: true, null);
            }

            return document.RootElement.TryGetProperty("poster_path", out var poster)
                && poster.ValueKind == JsonValueKind.String
                ? (Failed: false, poster.GetString())
                // TMDb answered and the title has no poster: a real negative, worth caching.
                : (Failed: false, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "TMDb poster lookup for {Identity} failed.", identity);
            return (Failed: true, null);
        }
    }

    private static string Url(string posterPath) => $"https://image.tmdb.org/t/p/w500{posterPath}";
}
