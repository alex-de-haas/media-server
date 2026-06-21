using System.Globalization;
using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Library;

namespace MediaServer.Api.Jellyfin.Endpoints;

/// <summary>
/// Playback-state sync (M3): the <c>Sessions/Playing*</c> progress reports plus played/favorite toggles.
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

        // ---- Favorites ----
        secured.MapPost("/Users/{userId}/FavoriteItems/{itemId}", (string userId, string itemId, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken) =>
            SetFavoriteAsync(userId, itemId, favorite: true, principal, database, userData, cancellationToken));

        secured.MapDelete("/Users/{userId}/FavoriteItems/{itemId}", (string userId, string itemId, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, CancellationToken cancellationToken) =>
            SetFavoriteAsync(userId, itemId, favorite: false, principal, database, userData, cancellationToken));
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
