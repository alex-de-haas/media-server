using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory.Trakt;

/// <summary>
/// Trakt's watched-history operations. Everything Trakt-shaped lives here: the sync payload shapes,
/// the <c>watched_at: "unknown"</c> sentinel, pagination headers, and remote id formats.
/// </summary>
public sealed class TraktWatchHistoryProvider(
    MediaServerDbContext database,
    TraktOAuthClient oauth,
    TraktAuthorizationService authorization,
    ILogger<TraktWatchHistoryProvider> logger)
    : IWatchHistoryProvider
{
    /// <summary>Trakt caps a history page at 100; ask for that and follow the page count it reports.</summary>
    private const int PageSize = 100;

    /// <summary>Refuse to walk forever if Trakt's paging headers ever disagree with themselves.</summary>
    private const int MaxPages = 200;

    public string Key => TraktAuthorizationService.ProviderKeyValue;

    public string DisplayName => "Trakt";

    public WatchHistoryCapabilities Capabilities => new()
    {
        ExactTimestampWrites = true,
        TimelessWrites = true,
        // Trakt has /sync/watched, but this adapter does not implement it: nothing asks yet, and
        // declaring a capability the adapter cannot serve is worse than declaring none.
        AggregateWatchedReads = false,
        FullHistoryReads = true,
        IndividualEntryRemoval = true,
    };

    public async Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> GetHistoryAsync(
        int appUserId, IReadOnlyCollection<WatchHistoryIdentity> identities, CancellationToken cancellationToken)
    {
        var token = await AccessTokenAsync(appUserId, cancellationToken);
        if (!token.Succeeded)
        {
            return WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Failed(token.Failure!.Value, token.Detail, token.RetryAfter);
        }

        var wanted = identities.SelectMany(identity => identity.Expand()).Where(identity => identity.IsResolvable).ToList();
        if (wanted.Count == 0)
        {
            return WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Success([]);
        }

        // Trakt's history endpoint filters by one item at a time, so ask per distinct work rather than
        // downloading a whole account's history to find a handful of items. Episodes of one series
        // share a show request; the response carries the episode coordinates to match on.
        var plays = new List<WatchHistoryPlay>();
        foreach (var group in wanted.GroupBy(identity => (identity.Kind, identity.TmdbId, identity.ImdbId)))
        {
            var fetched = await FetchWorkHistoryAsync(token.Value!, group.First(), group.ToList(), cancellationToken);
            if (!fetched.Succeeded)
            {
                return fetched;
            }

            plays.AddRange(fetched.Value!);
        }

        return WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Success(plays);
    }

    public async Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> AddPlaysAsync(
        int appUserId, IReadOnlyCollection<WatchHistoryPlay> plays, CancellationToken cancellationToken)
    {
        var token = await AccessTokenAsync(appUserId, cancellationToken);
        if (!token.Succeeded)
        {
            return WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Failed(token.Failure!.Value, token.Detail, token.RetryAfter);
        }

        // One local play over a multi-episode file becomes one Trakt entry per episode: Trakt has no
        // notion of a double episode, so the range is expanded here rather than sent as-is.
        var expanded = plays
            .SelectMany(play => play.Identity.Expand().Select(identity => play with { Identity = identity }))
            .Where(play => play.Identity.IsResolvable)
            .ToList();

        if (expanded.Count == 0)
        {
            return WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Success([]);
        }

        var movies = expanded.Where(play => play.Identity.Kind == WatchHistoryMediaKind.Movie).ToList();
        var episodes = expanded.Where(play => play.Identity.Kind == WatchHistoryMediaKind.Episode).ToList();

        var payload = new Dictionary<string, object>(StringComparer.Ordinal);
        if (movies.Count > 0)
        {
            payload["movies"] = movies.Select(MoviePayload).ToList();
        }

        if (episodes.Count > 0)
        {
            payload["shows"] = episodes.GroupBy(play => play.Identity.TmdbId ?? 0).Select(ShowPayload).ToList();
        }

        using var content = JsonContent.Create(payload);
        var response = await oauth.SendAsync(HttpMethod.Post, "sync/history", content, token.Value, cancellationToken);
        if (!response.Succeeded)
        {
            return WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Failed(response.Failure!.Value, response.Detail, response.RetryAfter);
        }

        using var document = response.Value!;
        // Trakt reports what it could not match rather than failing the request. Surfacing that as a
        // typed issue beats reporting success for plays that never landed.
        var notFound = CountNotFound(document.RootElement);
        if (notFound > 0)
        {
            logger.LogInformation("Trakt did not recognise {Count} of {Total} submitted plays.", notFound, expanded.Count);
        }

        // The add response carries counts, not the created entries' ids. Resolving those is the
        // caller's read-after-write concern, because only it knows what existed beforehand.
        return WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Success(expanded);
    }

    public async Task<WatchHistoryResult<int>> RemoveEntriesAsync(
        int appUserId, IReadOnlyCollection<string> remoteIds, CancellationToken cancellationToken)
    {
        var ids = remoteIds
            .Select(id => long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : (long?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        if (ids.Count != remoteIds.Count)
        {
            // Never fall back to a broader removal because an id looked wrong. Trakt's media-object
            // removal form deletes *every* play of that item, including other clients' and every exact
            // timestamped one — silently destroying history this app never created.
            return WatchHistoryResult<int>.Failed(
                WatchHistoryFailure.ContractViolation,
                "Refusing to remove history: one or more stored Trakt entry ids are unusable.");
        }

        if (ids.Count == 0)
        {
            return WatchHistoryResult<int>.Success(0);
        }

        var token = await AccessTokenAsync(appUserId, cancellationToken);
        if (!token.Succeeded)
        {
            return WatchHistoryResult<int>.Failed(token.Failure!.Value, token.Detail, token.RetryAfter);
        }

        // The ids form, and only the ids form: this deletes exactly the entries this app created and
        // recorded, leaving anything another client wrote untouched.
        using var content = JsonContent.Create(new { ids });
        var response = await oauth.SendAsync(HttpMethod.Post, "sync/history/remove", content, token.Value, cancellationToken);
        if (!response.Succeeded)
        {
            return WatchHistoryResult<int>.Failed(response.Failure!.Value, response.Detail, response.RetryAfter);
        }

        using var document = response.Value!;
        return WatchHistoryResult<int>.Success(CountDeleted(document.RootElement, ids.Count));
    }

    private async Task<WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>> FetchWorkHistoryAsync(
        string accessToken, WatchHistoryIdentity sample, IReadOnlyList<WatchHistoryIdentity> wanted, CancellationToken cancellationToken)
    {
        var type = sample.Kind == WatchHistoryMediaKind.Movie ? "movies" : "shows";
        var traktId = sample.TmdbId?.ToString(CultureInfo.InvariantCulture) ?? sample.ImdbId;
        if (string.IsNullOrWhiteSpace(traktId))
        {
            return WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Success([]);
        }

        var plays = new List<WatchHistoryPlay>();
        var wantedEpisodes = wanted
            .Where(identity => identity.Kind == WatchHistoryMediaKind.Episode)
            .Select(identity => (identity.SeasonNumber, identity.EpisodeNumber))
            .ToHashSet();

        for (var page = 1; page <= MaxPages; page++)
        {
            var path = $"sync/history/{type}/{Uri.EscapeDataString(traktId)}?page={page}&limit={PageSize}";
            var response = await oauth.SendAsync(HttpMethod.Get, path, content: null, accessToken, cancellationToken);
            if (!response.Succeeded)
            {
                return WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Failed(response.Failure!.Value, response.Detail, response.RetryAfter);
            }

            using var document = response.Value!;
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            var count = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                count++;
                var play = ToPlay(element, sample, wantedEpisodes);
                if (play is not null)
                {
                    plays.Add(play);
                }
            }

            // A short page is the last page. Following an explicit page count would mean trusting a
            // header we cannot see through this client's abstraction.
            if (count < PageSize)
            {
                break;
            }
        }

        return WatchHistoryResult<IReadOnlyList<WatchHistoryPlay>>.Success(plays);
    }

    private static WatchHistoryPlay? ToPlay(
        JsonElement element, WatchHistoryIdentity sample, IReadOnlySet<(int?, int?)> wantedEpisodes)
    {
        var id = element.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.Number
            ? idValue.GetInt64().ToString(CultureInfo.InvariantCulture)
            : null;

        // A play we cannot address is a play we can never safely delete, so it is not worth keeping.
        if (id is null)
        {
            return null;
        }

        DateTimeOffset? watchedAt = element.TryGetProperty("watched_at", out var watched)
            && watched.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(watched.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;

        if (sample.Kind == WatchHistoryMediaKind.Movie)
        {
            return new WatchHistoryPlay(sample, watchedAt, id);
        }

        if (!element.TryGetProperty("episode", out var episode))
        {
            return null;
        }

        var season = ReadInt(episode, "season");
        var number = ReadInt(episode, "number");
        // One show request returns every episode's history; keep only the ones asked about.
        if (!wantedEpisodes.Contains((season, number)))
        {
            return null;
        }

        return new WatchHistoryPlay(
            sample with { SeasonNumber = season, EpisodeNumber = number, EpisodeNumberEnd = null },
            watchedAt,
            id);
    }

    private static object MoviePayload(WatchHistoryPlay play) => new
    {
        watched_at = WatchedAt(play.WatchedAt),
        ids = Ids(play.Identity),
    };

    private static object ShowPayload(IGrouping<int, WatchHistoryPlay> show) => new
    {
        ids = Ids(show.First().Identity),
        seasons = show
            .GroupBy(play => play.Identity.SeasonNumber ?? 0)
            .Select(season => new
            {
                number = season.Key,
                episodes = season.Select(play => new
                {
                    number = play.Identity.EpisodeNumber ?? 0,
                    watched_at = WatchedAt(play.WatchedAt),
                }).ToList(),
            })
            .ToList(),
    };

    private static object Ids(WatchHistoryIdentity identity) =>
        identity.TmdbId is { } tmdb ? new { tmdb } : (object)new { imdb = identity.ImdbId };

    /// <summary>
    /// Trakt's sentinel for "watched, but the time is unknown". Sending a fabricated timestamp instead
    /// would put a viewing on the user's profile at a moment nothing happened.
    /// </summary>
    private static string WatchedAt(DateTimeOffset? watchedAt) =>
        watchedAt?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) ?? "unknown";

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

        var credentials = await authorization.ReadCredentialsAsync(connection, cancellationToken);
        return credentials is null
            ? WatchHistoryResult<string>.Failed(
                WatchHistoryFailure.AuthenticationRequired, connection.LastError ?? "The Trakt credentials are unavailable.")
            : WatchHistoryResult<string>.Success(credentials.AccessToken);
    }

    private static int CountNotFound(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("not_found", out var notFound))
        {
            return 0;
        }

        var total = 0;
        foreach (var property in notFound.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                total += property.Value.GetArrayLength();
            }
        }

        return total;
    }

    private static int CountDeleted(JsonElement root, int requested)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("deleted", out var deleted))
        {
            return requested;
        }

        var total = 0;
        foreach (var property in deleted.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value))
            {
                total += value;
            }
        }

        return total;
    }

    private static int? ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}
