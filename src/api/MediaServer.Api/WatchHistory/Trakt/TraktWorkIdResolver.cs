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
    /// The path segment to address this work by, or null when Trakt cannot recognize it at all.
    /// </summary>
    public async Task<string?> ResolveAsync(
        WatchHistoryIdentity identity, string accessToken, CancellationToken cancellationToken)
    {
        // Trakt accepts an IMDb id on these paths, and it needs no lookup.
        if (!string.IsNullOrWhiteSpace(identity.ImdbId))
        {
            return identity.ImdbId;
        }

        if (identity.TmdbId is not { } tmdbId)
        {
            return null;
        }

        if (cache.TryGet(identity.Kind, tmdbId, out var cached))
        {
            return cached;
        }

        var resolved = await SearchAsync(identity.Kind, tmdbId, accessToken, cancellationToken);
        if (resolved.Failed)
        {
            // Do not cache an outage as "unknown work": that would blank this title for the process's
            // lifetime. Returning null here only skips one read, which the caller retries.
            return null;
        }

        cache.Set(identity.Kind, tmdbId, resolved.TraktId);
        return resolved.TraktId;
    }

    private async Task<(bool Failed, string? TraktId)> SearchAsync(
        WatchHistoryMediaKind kind, int tmdbId, string accessToken, CancellationToken cancellationToken)
    {
        // Episodes are addressed through their show, so the search type follows the same split the
        // history paths use.
        var type = kind == WatchHistoryMediaKind.Movie ? "movie" : "show";
        var path = $"search/tmdb/{tmdbId.ToString(CultureInfo.InvariantCulture)}?type={type}";

        var response = await oauth.SendAsync(HttpMethod.Get, path, content: null, accessToken, cancellationToken);
        if (!response.Succeeded)
        {
            logger.LogDebug("Trakt could not be searched for tmdb:{TmdbId}: {Detail}", tmdbId, response.Detail);
            return (Failed: true, null);
        }

        using var document = response.Value!;
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return (Failed: false, null);
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
                return (Failed: false, trakt.GetInt64().ToString(CultureInfo.InvariantCulture));
            }
        }

        // Trakt answered and knows no such work. A real negative, worth remembering.
        logger.LogDebug("Trakt knows no {Type} for tmdb:{TmdbId}.", type, tmdbId);
        return (Failed: false, null);
    }
}
