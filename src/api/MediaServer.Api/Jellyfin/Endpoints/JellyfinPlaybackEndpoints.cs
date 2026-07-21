using System.Globalization;
using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Library;

namespace MediaServer.Api.Jellyfin.Endpoints;

/// <summary>
/// Playback-state sync (M3): the <c>Sessions/Playing*</c> progress reports plus played/favorite toggles.
/// The toggles are served in both route generations — legacy <c>/Users/{userId}/PlayedItems/…</c> and the
/// 10.9+ <c>/UserPlayedItems/…</c> query-user form clients pick against our reported server version.
/// Progress reports apply the resume + watched-threshold policy in <see cref="UserDataService"/>; the
/// toggles return the updated <see cref="UserItemDataDto"/>. See
/// <c>docs/features/jellyfin-compatibility.md</c> ("Playback state").
/// </summary>
internal static class JellyfinPlaybackEndpoints
{
    public static void MapJellyfinPlaybackEndpoints(this IEndpointRouteBuilder routes)
    {
        var secured = routes.MapGroup(string.Empty).RequireJellyfin();

        // ---- Progress reporting ----
        secured.MapPost("/Sessions/Playing", (PlaybackReportBody? body, HttpRequest request, ClaimsPrincipal principal, UserDataService userData, CancellationToken cancellationToken) =>
            ReportAsync(body, request, principal, userData, isStopped: false, cancellationToken));

        secured.MapPost("/Sessions/Playing/Progress", (PlaybackReportBody? body, HttpRequest request, ClaimsPrincipal principal, UserDataService userData, CancellationToken cancellationToken) =>
            ReportAsync(body, request, principal, userData, isStopped: false, cancellationToken));

        secured.MapPost("/Sessions/Playing/Stopped", (PlaybackReportBody? body, HttpRequest request, ClaimsPrincipal principal, UserDataService userData, CancellationToken cancellationToken) =>
            ReportAsync(body, request, principal, userData, isStopped: true, cancellationToken));

        // ---- Played / unplayed ----
        secured.MapPost("/Users/{userId}/PlayedItems/{itemId}", (string userId, string itemId, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken) =>
            SetPlayedAsync(userId, itemId, played: true, ParseDatePlayed(request), principal, database, userData, cancellationToken));

        secured.MapDelete("/Users/{userId}/PlayedItems/{itemId}", (string userId, string itemId, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken) =>
            SetPlayedAsync(userId, itemId, played: false, playedAt: null, principal, database, userData, cancellationToken));

        // Newer Jellyfin (10.9+) form: the acting user is the optional UserId query parameter. Infuse
        // picks this form over the legacy one because we report a 10.11 server version.
        secured.MapPost("/UserPlayedItems/{itemId}", (string itemId, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken) =>
            SetPlayedByQueryUserAsync(itemId, played: true, ParseDatePlayed(request), request, principal, database, userData, cancellationToken));

        secured.MapDelete("/UserPlayedItems/{itemId}", (string itemId, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken) =>
            SetPlayedByQueryUserAsync(itemId, played: false, playedAt: null, request, principal, database, userData, cancellationToken));

        // ---- Favorites ----
        secured.MapPost("/Users/{userId}/FavoriteItems/{itemId}", (string userId, string itemId, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken) =>
            SetFavoriteAsync(userId, itemId, favorite: true, principal, database, userData, cancellationToken));

        secured.MapDelete("/Users/{userId}/FavoriteItems/{itemId}", (string userId, string itemId, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken) =>
            SetFavoriteAsync(userId, itemId, favorite: false, principal, database, userData, cancellationToken));

        secured.MapPost("/UserFavoriteItems/{itemId}", (string itemId, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken) =>
            SetFavoriteByQueryUserAsync(itemId, favorite: true, request, principal, database, userData, cancellationToken));

        secured.MapDelete("/UserFavoriteItems/{itemId}", (string itemId, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken) =>
            SetFavoriteByQueryUserAsync(itemId, favorite: false, request, principal, database, userData, cancellationToken));
    }

    private static async Task<IResult> ReportAsync(
        PlaybackReportBody? body, HttpRequest request, ClaimsPrincipal principal, UserDataService userData, bool isStopped, CancellationToken cancellationToken)
    {
        if (JellyfinPrincipal.AppUserId(principal) is not { } appUserId)
        {
            return Results.Unauthorized();
        }

        var itemId = body?.ItemId ?? request.Query["ItemId"].ToString();
        if (string.IsNullOrEmpty(itemId))
        {
            return Results.NoContent();
        }

        var position = body?.PositionTicks ?? ParseLong(request.Query["PositionTicks"]) ?? 0;
        await userData.ReportPlaybackAsync(appUserId, itemId, Math.Max(0, position), isStopped, cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> SetPlayedAsync(
        string userId, string itemId, bool played, DateTimeOffset? playedAt, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken)
    {
        var actingUserId = await JellyfinPrincipal.ResolveActingUserIdAsync(principal, userId, database, cancellationToken);
        return await SetPlayedCoreAsync(actingUserId, itemId, played, playedAt, userData, cancellationToken);
    }

    private static async Task<IResult> SetPlayedByQueryUserAsync(
        string itemId, bool played, DateTimeOffset? playedAt, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken)
    {
        var actingUserId = await JellyfinPrincipal.ResolveQueryUserIdAsync(request, principal, database, cancellationToken);
        return await SetPlayedCoreAsync(actingUserId, itemId, played, playedAt, userData, cancellationToken);
    }

    private static async Task<IResult> SetPlayedCoreAsync(
        int? actingUserId, string itemId, bool played, DateTimeOffset? playedAt, UserDataService userData, CancellationToken cancellationToken)
    {
        if (actingUserId is null)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var data = await userData.SetPlayedAsync(actingUserId.Value, itemId, played, playedAt, cancellationToken);
        return data is null ? Results.NotFound() : JellyfinJson.Ok(data);
    }

    private static async Task<IResult> SetFavoriteAsync(
        string userId, string itemId, bool favorite, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken)
    {
        var actingUserId = await JellyfinPrincipal.ResolveActingUserIdAsync(principal, userId, database, cancellationToken);
        return await SetFavoriteCoreAsync(actingUserId, itemId, favorite, userData, cancellationToken);
    }

    private static async Task<IResult> SetFavoriteByQueryUserAsync(
        string itemId, bool favorite, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken)
    {
        var actingUserId = await JellyfinPrincipal.ResolveQueryUserIdAsync(request, principal, database, cancellationToken);
        return await SetFavoriteCoreAsync(actingUserId, itemId, favorite, userData, cancellationToken);
    }

    private static async Task<IResult> SetFavoriteCoreAsync(
        int? actingUserId, string itemId, bool favorite, UserDataService userData, CancellationToken cancellationToken)
    {
        if (actingUserId is null)
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        var data = await userData.SetFavoriteAsync(actingUserId.Value, itemId, favorite, cancellationToken);
        return data is null ? Results.NotFound() : JellyfinJson.Ok(data);
    }

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static DateTimeOffset? ParseDatePlayed(HttpRequest request) =>
        DateTimeOffset.TryParse(request.Query["DatePlayed"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}
