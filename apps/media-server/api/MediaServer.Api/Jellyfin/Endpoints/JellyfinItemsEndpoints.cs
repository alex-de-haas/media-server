using System.Security.Claims;

namespace MediaServer.Api.Jellyfin.Endpoints;

/// <summary>Item browsing, search, and show hierarchy endpoints.</summary>
internal static class JellyfinItemsEndpoints
{
    public static void MapJellyfinItemsEndpoints(this IEndpointRouteBuilder routes)
    {
        var secured = routes.MapGroup(string.Empty).RequireJellyfin();

        secured.MapGet("/Items", async (HttpRequest request, JellyfinLibraryService library, CancellationToken cancellationToken) =>
            JellyfinJson.Ok(await library.ListItemsAsync(ParseQuery(request), cancellationToken)));

        secured.MapGet("/Items/{itemId}", async (string itemId, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            var item = await library.GetItemAsync(itemId, includeMediaSources: true, cancellationToken);
            return item is null ? Results.NotFound() : JellyfinJson.Ok(item);
        });

        secured.MapGet("/Users/{userId}/Items", async (string userId, HttpRequest request, ClaimsPrincipal principal, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            if (!JellyfinPrincipal.CanActAs(principal, userId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return JellyfinJson.Ok(await library.ListItemsAsync(ParseQuery(request), cancellationToken));
        });

        secured.MapGet("/Users/{userId}/Items/Latest", async (string userId, HttpRequest request, ClaimsPrincipal principal, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            if (!JellyfinPrincipal.CanActAs(principal, userId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var parentId = request.Query["ParentId"].ToString();
            var limit = ParseInt(request.Query["Limit"]) ?? 20;
            var latest = await library.GetLatestAsync(string.IsNullOrEmpty(parentId) ? null : parentId, limit, cancellationToken);
            // Jellyfin returns a bare array for Latest.
            return JellyfinJson.Ok(latest.Items);
        });

        // Resume (M2: no playback state yet) and Next Up are empty until M3 persists user data.
        secured.MapGet("/Users/{userId}/Items/Resume", (string userId, ClaimsPrincipal principal) =>
            JellyfinPrincipal.CanActAs(principal, userId)
                ? JellyfinJson.Ok(new QueryResult<BaseItemDto>([], 0))
                : Results.StatusCode(StatusCodes.Status403Forbidden));

        secured.MapGet("/Shows/NextUp", () => JellyfinJson.Ok(new QueryResult<BaseItemDto>([], 0)));

        secured.MapGet("/Users/{userId}/Items/{itemId}", async (string userId, string itemId, ClaimsPrincipal principal, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            if (!JellyfinPrincipal.CanActAs(principal, userId))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            var item = await library.GetItemAsync(itemId, includeMediaSources: true, cancellationToken);
            return item is null ? Results.NotFound() : JellyfinJson.Ok(item);
        });

        secured.MapGet("/Shows/{seriesId}/Seasons", async (string seriesId, JellyfinLibraryService library, CancellationToken cancellationToken) =>
            JellyfinJson.Ok(await library.GetSeasonsAsync(seriesId, cancellationToken)));

        secured.MapGet("/Shows/{seriesId}/Episodes", async (string seriesId, HttpRequest request, JellyfinLibraryService library, CancellationToken cancellationToken) =>
        {
            var seasonId = request.Query["SeasonId"].ToString();
            var seasonNumber = ParseInt(request.Query["Season"]);
            return JellyfinJson.Ok(await library.GetEpisodesAsync(
                seriesId, string.IsNullOrEmpty(seasonId) ? null : seasonId, seasonNumber, cancellationToken));
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
