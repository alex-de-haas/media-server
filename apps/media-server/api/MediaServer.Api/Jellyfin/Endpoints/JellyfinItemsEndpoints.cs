using System.Security.Claims;
using MediaServer.Api.Data;

namespace MediaServer.Api.Jellyfin.Endpoints;

/// <summary>Item browsing, search, and show hierarchy endpoints.</summary>
internal static class JellyfinItemsEndpoints
{
    public static void MapJellyfinItemsEndpoints(this IEndpointRouteBuilder routes)
    {
        var secured = routes.MapGroup(string.Empty).RequireJellyfin();

        secured.MapGet("/Items", async (HttpRequest request, ClaimsPrincipal principal, JellyfinLibraryService library, CancellationToken cancellationToken) =>
            JellyfinJson.Ok(await library.ListItemsAsync(ParseQuery(request), JellyfinPrincipal.AppUserId(principal), cancellationToken)));

        secured.MapGet("/Items/{itemId}", async (string itemId, ClaimsPrincipal principal, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            var item = await library.GetItemAsync(itemId, includeMediaSources: true, JellyfinPrincipal.AppUserId(principal), cancellationToken);
            return item is null ? Results.NotFound() : JellyfinJson.Ok(item);
        });

        secured.MapGet("/Users/{userId}/Items", async (string userId, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            var actingUserId = await JellyfinPrincipal.ResolveActingUserIdAsync(principal, userId, database, cancellationToken);
            if (actingUserId is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return JellyfinJson.Ok(await library.ListItemsAsync(ParseQuery(request), actingUserId, cancellationToken));
        });

        secured.MapGet("/Users/{userId}/Items/Latest", async (string userId, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            var actingUserId = await JellyfinPrincipal.ResolveActingUserIdAsync(principal, userId, database, cancellationToken);
            if (actingUserId is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var parentId = request.Query["ParentId"].ToString();
            var limit = ParseInt(request.Query["Limit"]) ?? 20;
            var latest = await library.GetLatestAsync(string.IsNullOrEmpty(parentId) ? null : parentId, limit, actingUserId, cancellationToken);
            // Jellyfin returns a bare array for Latest.
            return JellyfinJson.Ok(latest.Items);
        });

        secured.MapGet("/Users/{userId}/Items/Resume", async (string userId, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            var actingUserId = await JellyfinPrincipal.ResolveActingUserIdAsync(principal, userId, database, cancellationToken);
            if (actingUserId is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var parentId = request.Query["ParentId"].ToString();
            var limit = ParseInt(request.Query["Limit"]) ?? 20;
            return JellyfinJson.Ok(await library.GetResumeAsync(
                actingUserId.Value, string.IsNullOrEmpty(parentId) ? null : parentId, limit, cancellationToken));
        });

        secured.MapGet("/Shows/NextUp", async (HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            // NextUp targets the caller unless an explicit (authorized) UserId is supplied.
            var requestedUserId = request.Query["UserId"].ToString();
            var actingUserId = string.IsNullOrEmpty(requestedUserId)
                ? JellyfinPrincipal.AppUserId(principal)
                : await JellyfinPrincipal.ResolveActingUserIdAsync(principal, requestedUserId, database, cancellationToken);
            if (actingUserId is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var seriesId = request.Query["SeriesId"].ToString();
            var limit = ParseInt(request.Query["Limit"]) ?? 20;
            return JellyfinJson.Ok(await library.GetNextUpAsync(
                actingUserId.Value, string.IsNullOrEmpty(seriesId) ? null : seriesId, limit, cancellationToken));
        });

        secured.MapGet("/Users/{userId}/Items/{itemId}", async (string userId, string itemId, ClaimsPrincipal principal, MediaServerDbContext database, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            var actingUserId = await JellyfinPrincipal.ResolveActingUserIdAsync(principal, userId, database, cancellationToken);
            if (actingUserId is null)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var item = await library.GetItemAsync(itemId, includeMediaSources: true, actingUserId, cancellationToken);
            return item is null ? Results.NotFound() : JellyfinJson.Ok(item);
        });

        secured.MapGet("/Shows/{seriesId}/Seasons", async (string seriesId, ClaimsPrincipal principal, JellyfinLibraryService library, CancellationToken cancellationToken) =>
            JellyfinJson.Ok(await library.GetSeasonsAsync(seriesId, JellyfinPrincipal.AppUserId(principal), cancellationToken)));

        secured.MapGet("/Shows/{seriesId}/Episodes", async (string seriesId, HttpRequest request, ClaimsPrincipal principal, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            var seasonId = request.Query["SeasonId"].ToString();
            var seasonNumber = ParseInt(request.Query["Season"]);
            return JellyfinJson.Ok(await library.GetEpisodesAsync(
                seriesId, string.IsNullOrEmpty(seasonId) ? null : seasonId, seasonNumber,
                JellyfinPrincipal.AppUserId(principal), cancellationToken));
        });
    }

    private static JellyfinItemsQuery ParseQuery(HttpRequest request)
    {
        var query = request.Query;
        return new JellyfinItemsQuery
        {
            ParentId = NullIfEmpty(query["ParentId"]),
            Ids = SplitList(query["Ids"]),
            IncludeItemTypes = SplitList(query["IncludeItemTypes"])?.ToHashSet(StringComparer.OrdinalIgnoreCase),
            SearchTerm = NullIfEmpty(query["SearchTerm"]),
            Recursive = bool.TryParse(query["Recursive"], out var recursive) && recursive,
            IncludeMediaSources = SplitList(query["Fields"])?.Any(field =>
                field.Equals("MediaSources", StringComparison.OrdinalIgnoreCase)) ?? false,
            StartIndex = ParseInt(query["StartIndex"]),
            Limit = ParseInt(query["Limit"]),
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string>? SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int? ParseInt(string? value) => int.TryParse(value, out var parsed) ? parsed : null;
}
