using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory.Trakt;

/// <summary>
/// Trakt's favorites, over <c>/sync/favorites</c>. Everything Trakt-shaped stays here: the movies/shows
/// payload split, the <c>favorited_at</c> field, and the account cap Trakt reports through
/// <c>list.item_count</c>.
/// </summary>
/// <remarks>
/// Favorites are a *work-level* statement — Trakt accepts movies and shows only, never seasons or
/// episodes — which is why this is a separate adapter from
/// <see cref="TraktWatchHistoryProvider"/> rather than more methods on it: history speaks in plays,
/// this speaks in works.
/// </remarks>
public sealed class TraktFavoritesProvider(
    MediaServerDbContext database,
    TraktOAuthClient oauth,
    TraktAuthorizationService authorization,
    ILogger<TraktFavoritesProvider> logger)
    : IWatchHistoryFavoritesProvider
{
    /// <summary>
    /// Trakt caps favorites at 100 for every account — VIP does not raise it. Reported so the core can
    /// warn before a write fails rather than hard-coding another service's limit.
    /// </summary>
    public const int FavoritesCapacity = 100;

    private const int PageSize = 100;
    private const int MaxPages = 20;

    public string ProviderKey => TraktAuthorizationService.ProviderKeyValue;

    public async Task<WatchHistoryResult<FavoritesSnapshot>> GetFavoritesAsync(int appUserId, CancellationToken cancellationToken)
    {
        var token = await AccessTokenAsync(appUserId, cancellationToken);
        if (!token.Succeeded)
        {
            return WatchHistoryResult<FavoritesSnapshot>.Failed(token.Failure!.Value, token.Detail, token.RetryAfter);
        }

        var favorites = new List<ProviderFavorite>();
        foreach (var (type, kind) in new[] { ("movies", FavoriteWorkKind.Movie), ("shows", FavoriteWorkKind.Series) })
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var path = $"sync/favorites/{type}?page={page}&limit={PageSize}";
                var response = await oauth.SendAsync(HttpMethod.Get, path, content: null, token.Value!, cancellationToken);
                if (!response.Succeeded)
                {
                    return WatchHistoryResult<FavoritesSnapshot>.Failed(response.Failure!.Value, response.Detail, response.RetryAfter);
                }

                using var document = response.Value!;
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return WatchHistoryResult<FavoritesSnapshot>.Failed(
                        WatchHistoryFailure.ContractViolation, $"Trakt returned a non-list favorites body for {type}.");
                }

                var before = favorites.Count;
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    if (ReadFavorite(element, kind) is { } favorite)
                    {
                        favorites.Add(favorite);
                    }
                }

                // Short page (or none at all) means the list is exhausted; Trakt's paging headers are not
                // needed for a list this small and capped.
                if (favorites.Count - before < PageSize)
                {
                    break;
                }
            }
        }

        return WatchHistoryResult<FavoritesSnapshot>.Success(
            new FavoritesSnapshot(favorites, favorites.Count, FavoritesCapacity));
    }

    public Task<WatchHistoryResult<FavoritesWriteResult>> AddFavoritesAsync(
        int appUserId, IReadOnlyCollection<FavoriteIdentity> identities, CancellationToken cancellationToken) =>
        WriteAsync(appUserId, identities, "sync/favorites", added: true, cancellationToken);

    public Task<WatchHistoryResult<FavoritesWriteResult>> RemoveFavoritesAsync(
        int appUserId, IReadOnlyCollection<FavoriteIdentity> identities, CancellationToken cancellationToken) =>
        WriteAsync(appUserId, identities, "sync/favorites/remove", added: false, cancellationToken);

    private async Task<WatchHistoryResult<FavoritesWriteResult>> WriteAsync(
        int appUserId, IReadOnlyCollection<FavoriteIdentity> identities, string path, bool added, CancellationToken cancellationToken)
    {
        var wanted = identities.Where(identity => identity.IsResolvable).ToList();
        if (wanted.Count == 0)
        {
            return WatchHistoryResult<FavoritesWriteResult>.Success(new FavoritesWriteResult(0, 0, 0, null));
        }

        var token = await AccessTokenAsync(appUserId, cancellationToken);
        if (!token.Succeeded)
        {
            return WatchHistoryResult<FavoritesWriteResult>.Failed(token.Failure!.Value, token.Detail, token.RetryAfter);
        }

        var payload = new Dictionary<string, object>(StringComparer.Ordinal);
        var movies = wanted.Where(identity => identity.Kind == FavoriteWorkKind.Movie).ToList();
        var shows = wanted.Where(identity => identity.Kind == FavoriteWorkKind.Series).ToList();
        if (movies.Count > 0)
        {
            payload["movies"] = movies.Select(identity => new { ids = Ids(identity) }).ToList();
        }

        if (shows.Count > 0)
        {
            payload["shows"] = shows.Select(identity => new { ids = Ids(identity) }).ToList();
        }

        using var content = JsonContent.Create(payload);
        var response = await oauth.SendAsync(HttpMethod.Post, path, content, token.Value!, cancellationToken);
        if (!response.Succeeded)
        {
            // A full list answers 420, which SendAsync maps to AccountLimitReached — terminal, so the
            // caller surfaces "your Trakt favorites are full" instead of retrying into the same wall.
            return WatchHistoryResult<FavoritesWriteResult>.Failed(response.Failure!.Value, response.Detail, response.RetryAfter);
        }

        using var document = response.Value!;
        var root = document.RootElement;
        var applied = CountOf(root, added ? "added" : "deleted");
        var unchanged = CountOf(root, added ? "existing" : "not_found");
        var notFound = added ? CountOf(root, "not_found") : 0;

        if (notFound > 0)
        {
            logger.LogInformation("Trakt did not recognise {Count} of {Total} favorited works.", notFound, wanted.Count);
        }

        return WatchHistoryResult<FavoritesWriteResult>.Success(
            new FavoritesWriteResult(applied, unchanged, notFound, RemoteCountOf(root)));
    }

    /// <summary>
    /// One favorites list entry. The work sits under <c>movie</c> or <c>show</c>; an entry whose ids
    /// carry neither a TMDb nor an IMDb id is skipped — nothing local could ever match it.
    /// </summary>
    private static ProviderFavorite? ReadFavorite(JsonElement element, FavoriteWorkKind kind)
    {
        var workName = kind == FavoriteWorkKind.Movie ? "movie" : "show";
        if (!element.TryGetProperty(workName, out var work) || !work.TryGetProperty("ids", out var ids))
        {
            return null;
        }

        int? tmdb = ids.TryGetProperty("tmdb", out var tmdbElement) && tmdbElement.ValueKind == JsonValueKind.Number
            ? tmdbElement.GetInt32()
            : null;
        var imdb = ids.TryGetProperty("imdb", out var imdbElement) && imdbElement.ValueKind == JsonValueKind.String
            ? imdbElement.GetString()
            : null;

        var identity = new FavoriteIdentity(kind, tmdb, imdb);
        if (!identity.IsResolvable)
        {
            return null;
        }

        DateTimeOffset? favoritedAt =
            element.TryGetProperty("favorited_at", out var at) && at.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(at.GetString(), out var parsed)
                ? parsed.ToUniversalTime()
                : null;

        return new ProviderFavorite(identity, favoritedAt);
    }

    /// <summary>Sums the movie and show counters of one section of a favorites write response.</summary>
    private static int CountOf(JsonElement root, string section)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(section, out var element))
        {
            return 0;
        }

        var total = 0;
        foreach (var name in new[] { "movies", "shows" })
        {
            if (element.TryGetProperty(name, out var value))
            {
                // "added"/"existing" report numbers; "not_found" reports the rejected objects themselves.
                total += value.ValueKind switch
                {
                    JsonValueKind.Number => value.GetInt32(),
                    JsonValueKind.Array => value.GetArrayLength(),
                    _ => 0,
                };
            }
        }

        return total;
    }

    /// <summary>How full Trakt says the list now is, from the <c>list.item_count</c> it echoes back.</summary>
    private static int? RemoteCountOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("list", out var list) &&
        list.TryGetProperty("item_count", out var count) &&
        count.ValueKind == JsonValueKind.Number
            ? count.GetInt32()
            : null;

    private static object Ids(FavoriteIdentity identity) =>
        identity.TmdbId is { } tmdb ? new { tmdb } : (object)new { imdb = identity.ImdbId };

    private async Task<WatchHistoryResult<string>> AccessTokenAsync(int appUserId, CancellationToken cancellationToken)
    {
        var connection = await database.WatchHistoryConnections.FirstOrDefaultAsync(
            entry => entry.AppUserId == appUserId && entry.ProviderKey == TraktAuthorizationService.ProviderKeyValue,
            cancellationToken);

        if (connection is null)
        {
            return WatchHistoryResult<string>.Failed(
                WatchHistoryFailure.AuthenticationRequired, "This user has no Trakt connection.");
        }

        // Propagate the failure kind rather than flattening it — see TraktWatchHistoryProvider.
        var credentials = await authorization.ReadCredentialsAsync(connection, cancellationToken);
        return credentials.Succeeded
            ? WatchHistoryResult<string>.Success(credentials.Value!.AccessToken)
            : WatchHistoryResult<string>.Failed(credentials.Failure!.Value, credentials.Detail, credentials.RetryAfter);
    }
}
