using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace MediaServer.Api.WatchHistory.Trakt;

/// <summary>
/// Remembers which Trakt id belongs to a TMDb id.
/// </summary>
/// <remarks>
/// A separate singleton because the resolver is scoped — it holds a scoped HTTP client — and a cache
/// that died with each request would not be a cache. The mapping between two catalogues' ids for the
/// same work never changes, so process lifetime is a fine horizon.
/// </remarks>
public sealed class TraktWorkIdCache
{
    private readonly ConcurrentDictionary<(WatchHistoryMediaKind Kind, int TmdbId), string?> map = new();

    public bool TryGet(WatchHistoryMediaKind kind, int tmdbId, out string? traktId) =>
        map.TryGetValue((kind, tmdbId), out traktId);

    public void Set(WatchHistoryMediaKind kind, int tmdbId, string? traktId) => map[(kind, tmdbId)] = traktId;
}

/// <summary>
/// Resolves the id Trakt's per-work paths actually accept.
/// </summary>
/// <remarks>
/// <c>/sync/history/{type}/{id}</c> takes a <b>Trakt id, slug, or IMDb id</b> — never a TMDb id. Handed
/// a TMDb id it answers <c>200</c> with an empty array, which is indistinguishable from "this account
/// has no history for that title". That silence is what made every ownership read-back come back
/// empty: no remote id was ever captured, so an unwatch had nothing it was allowed to remove and
/// completed having done nothing.
///
/// An IMDb id works directly and costs no extra request, so it is preferred when the identity carries
/// one. Otherwise the TMDb id is translated through Trakt's search endpoint and cached: the mapping
/// between two catalogues' ids for the same work does not change.
/// </remarks>
public sealed class TraktWorkIdResolver(
    TraktOAuthClient oauth, TraktWorkIdCache cache, ILogger<TraktWorkIdResolver> logger)
{

    /// <summary>
    /// The path segment to address this work by.
    /// </summary>
    /// <returns>
    /// A success carrying the id, a success carrying <c>null</c> when Trakt genuinely knows no such
    /// work, or a failure when the lookup could not be made.
    /// </returns>
    /// <remarks>
    /// The three outcomes have to stay distinguishable. Collapsing "I could not ask" into "there is
    /// nothing" is precisely the mistake this class exists to undo: the caller would report an
    /// authoritative empty history, and a delivery retry would re-post a play that already exists.
    /// </remarks>
    public async Task<WatchHistoryResult<string?>> ResolveAsync(
        WatchHistoryIdentity identity, string accessToken, CancellationToken cancellationToken)
    {
        // Trakt accepts an IMDb id on these paths, and it needs no lookup.
        if (!string.IsNullOrWhiteSpace(identity.ImdbId))
        {
            return WatchHistoryResult<string?>.Success(identity.ImdbId);
        }

        if (identity.TmdbId is not { } tmdbId)
        {
            return WatchHistoryResult<string?>.Success(null);
        }

        if (cache.TryGet(identity.Kind, tmdbId, out var cached))
        {
            return WatchHistoryResult<string?>.Success(cached);
        }

        var searched = await SearchAsync(identity.Kind, tmdbId, accessToken, cancellationToken);
        if (!searched.Succeeded)
        {
            // Not cached: an outage is not evidence about this work, and remembering it as "unknown"
            // would blank the title for the process's lifetime.
            return searched;
        }

        cache.Set(identity.Kind, tmdbId, searched.Value);
        return searched;
    }

    private async Task<WatchHistoryResult<string?>> SearchAsync(
        WatchHistoryMediaKind kind, int tmdbId, string accessToken, CancellationToken cancellationToken)
    {
        // Episodes are addressed through their show, so the search type follows the same split the
        // history paths use.
        var type = kind == WatchHistoryMediaKind.Movie ? "movie" : "show";
        var path = $"search/tmdb/{tmdbId.ToString(CultureInfo.InvariantCulture)}?type={type}";

        var response = await oauth.SendAsync(HttpMethod.Get, path, content: null, accessToken, cancellationToken);
        if (!response.Succeeded)
        {
            // Rate limits and outages travel back with their kind intact, so the caller can retry
            // rather than conclude anything about this work.
            logger.LogDebug("Trakt could not be searched for tmdb:{TmdbId}: {Detail}", tmdbId, response.Detail);
            return WatchHistoryResult<string?>.Failed(response.Failure!.Value, response.Detail, response.RetryAfter);
        }

        using var document = response.Value!;
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return WatchHistoryResult<string?>.Failed(
                WatchHistoryFailure.ContractViolation, "Trakt's search returned a body that is not a list.");
        }

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty(type, out var work)
                || !work.TryGetProperty("ids", out var ids))
            {
                continue;
            }

            if (ids.TryGetProperty("trakt", out var trakt) && trakt.ValueKind == JsonValueKind.Number)
            {
                return WatchHistoryResult<string?>.Success(trakt.GetInt64().ToString(CultureInfo.InvariantCulture));
            }
        }

        // Trakt answered and knows no such work. A real negative, worth remembering.
        logger.LogDebug("Trakt knows no {Type} for tmdb:{TmdbId}.", type, tmdbId);
        return WatchHistoryResult<string?>.Success(null);
    }
}
