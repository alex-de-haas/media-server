using System.Globalization;
using System.Text.Json;
using MediaServer.Api.Data;
using MediaServer.Api.WatchHistory;
using MediaServer.Api.WatchHistory.Trakt;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations.Trakt;

/// <summary>
/// Trakt's own personalized recommendations, for users who connected an account.
/// </summary>
/// <remarks>
/// A thin adapter: Trakt runs the engine, over a viewing history far wider than this instance's — its
/// order is taken as the rank rather than re-scored here. Verified available on free accounts.
///
/// Availability is per user and health-gated. Everything here degrades to an empty list rather than
/// an error, because this source is an upgrade over the built-in engine, never a dependency of it.
/// </remarks>
public sealed class TraktRecommendationProvider(
    MediaServerDbContext database,
    TraktAuthorizationService authorization,
    TraktOAuthClient oauth,
    ILogger<TraktRecommendationProvider> logger) : IRecommendationProvider
{
    public const string ProviderKey = "trakt";

    public string Key => ProviderKey;

    public string DisplayName => "Trakt";

    public async Task<bool> IsAvailableAsync(int appUserId, CancellationToken cancellationToken)
    {
        // Connected *and* healthy: a connection awaiting reconnection would fail every call, and an
        // unavailable source is a cleaner story than a source that is always empty.
        var connection = await database.WatchHistoryConnections.AsNoTracking().FirstOrDefaultAsync(
            entry => entry.AppUserId == appUserId
                && entry.ProviderKey == TraktAuthorizationService.ProviderKeyValue
                && entry.Status == WatchHistoryConnectionStatus.Connected,
            cancellationToken);

        return connection is not null;
    }

    public async Task<IReadOnlyList<RecommendationCandidate>> GetAsync(
        int appUserId, int limit, CancellationToken cancellationToken)
    {
        var connection = await database.WatchHistoryConnections.FirstOrDefaultAsync(
            entry => entry.AppUserId == appUserId && entry.ProviderKey == TraktAuthorizationService.ProviderKeyValue,
            cancellationToken);

        if (connection is null)
        {
            return [];
        }

        var credentials = await authorization.ReadCredentialsAsync(connection, cancellationToken);
        if (!credentials.Succeeded)
        {
            // Includes the reconnect case, which ReadCredentialsAsync has already recorded on the
            // connection — Settings is where that gets surfaced, not the recommendations feed.
            logger.LogDebug("Trakt recommendations skipped: {Detail}", credentials.Detail);
            return [];
        }

        var token = credentials.Value!.AccessToken;
        var movies = await FetchAsync("recommendations/movies", RecommendationKind.Movie, limit, token, cancellationToken);
        var shows = await FetchAsync("recommendations/shows", RecommendationKind.Series, limit, token, cancellationToken);

        // Trakt ranks each kind separately, so interleave rather than concatenating: appending would
        // bury every series below every movie for no reason either list implies.
        return [.. Interleave(movies, shows).Take(limit).Select(
            (candidate, rank) => candidate with { Rank = rank })];
    }

    private async Task<List<RecommendationCandidate>> FetchAsync(
        string path, RecommendationKind kind, int limit, string token, CancellationToken cancellationToken)
    {
        var response = await oauth.SendAsync(
            HttpMethod.Get, $"{path}?limit={limit}", content: null, token, cancellationToken);

        if (!response.Succeeded)
        {
            logger.LogDebug("Trakt {Path} returned {Detail}", path, response.Detail);
            return [];
        }

        using var document = response.Value!;
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var candidates = new List<RecommendationCandidate>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            // Trakt returns either a bare title object or one wrapped under "movie"/"show"; accept both
            // so a response-shape change does not silently empty the feed.
            var node = element.TryGetProperty(kind == RecommendationKind.Movie ? "movie" : "show", out var wrapped)
                ? wrapped
                : element;

            if (Read(node, kind, candidates.Count) is { } candidate)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static RecommendationCandidate? Read(JsonElement node, RecommendationKind kind, int rank)
    {
        if (node.ValueKind != JsonValueKind.Object
            || !node.TryGetProperty("title", out var title)
            || title.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        // Only TMDb-identified titles can be merged with the built-in engine or matched to the
        // library. A Trakt-only id would produce a card nothing else could recognize.
        if (!node.TryGetProperty("ids", out var ids)
            || !ids.TryGetProperty("tmdb", out var tmdb)
            || tmdb.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var year = node.TryGetProperty("year", out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : (int?)null;

        return new RecommendationCandidate(
            new RecommendationIdentity(kind, tmdb.GetInt64().ToString(CultureInfo.InvariantCulture)),
            title.GetString()!,
            year,
            // Trakt carries no artwork; the feed service fills posters in from TMDb metadata.
            PosterUrl: null,
            rank);
    }

    /// <summary>Alternates the two lists so neither kind is systematically buried under the other.</summary>
    private static IEnumerable<RecommendationCandidate> Interleave(
        IReadOnlyList<RecommendationCandidate> first, IReadOnlyList<RecommendationCandidate> second)
    {
        for (var index = 0; index < Math.Max(first.Count, second.Count); index++)
        {
            if (index < first.Count)
            {
                yield return first[index];
            }

            if (index < second.Count)
            {
                yield return second[index];
            }
        }
    }
}
