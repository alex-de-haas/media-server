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
        secured.MapPost("/Sessions/Playing", (PlaybackReportBody? body, HttpRequest request, ClaimsPrincipal principal, UserDataService userData, PlaybackDiagnostics diagnostics, CancellationToken cancellationToken) =>
            ReportAsync(body, request, principal, userData, diagnostics, PlaybackRouteKinds.Playing, isStopped: false, cancellationToken));

        secured.MapPost("/Sessions/Playing/Progress", (PlaybackReportBody? body, HttpRequest request, ClaimsPrincipal principal, UserDataService userData, PlaybackDiagnostics diagnostics, CancellationToken cancellationToken) =>
            ReportAsync(body, request, principal, userData, diagnostics, PlaybackRouteKinds.Progress, isStopped: false, cancellationToken));

        secured.MapPost("/Sessions/Playing/Stopped", (PlaybackReportBody? body, HttpRequest request, ClaimsPrincipal principal, UserDataService userData, PlaybackDiagnostics diagnostics, CancellationToken cancellationToken) =>
            ReportAsync(body, request, principal, userData, diagnostics, PlaybackRouteKinds.Stopped, isStopped: true, cancellationToken));

        // ---- Played / unplayed ----
        secured.MapPost("/Users/{userId}/PlayedItems/{itemId}", (string userId, string itemId, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, PlaybackDiagnostics diagnostics, CancellationToken cancellationToken) =>
            SetPlayedAsync(userId, itemId, played: true, request, principal, database, userData, diagnostics, cancellationToken));

        secured.MapDelete("/Users/{userId}/PlayedItems/{itemId}", (string userId, string itemId, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, PlaybackDiagnostics diagnostics, CancellationToken cancellationToken) =>
            SetPlayedAsync(userId, itemId, played: false, request, principal, database, userData, diagnostics, cancellationToken));

        // Newer Jellyfin (10.9+) form: the acting user is the optional UserId query parameter. Infuse
        // picks this form over the legacy one because we report a 10.11 server version.
        secured.MapPost("/UserPlayedItems/{itemId}", (string itemId, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, PlaybackDiagnostics diagnostics, CancellationToken cancellationToken) =>
            SetPlayedByQueryUserAsync(itemId, played: true, request, principal, database, userData, diagnostics, cancellationToken));

        secured.MapDelete("/UserPlayedItems/{itemId}", (string itemId, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database, UserDataService userData, PlaybackDiagnostics diagnostics, CancellationToken cancellationToken) =>
            SetPlayedByQueryUserAsync(itemId, played: false, request, principal, database, userData, diagnostics, cancellationToken));

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
        PlaybackReportBody? body, HttpRequest request, ClaimsPrincipal principal, UserDataService userData,
        PlaybackDiagnostics diagnostics, string routeKind, bool isStopped, CancellationToken cancellationToken)
    {
        var appUserId = JellyfinPrincipal.AppUserId(principal);
        var itemId = body?.ItemId ?? request.Query["ItemId"].ToString();
        var position = body?.PositionTicks ?? ParseLong(request.Query["PositionTicks"]);

        // Opened before the guards so a rejected or ignored report is still observed — "Infuse sent
        // this and we did nothing" is exactly the kind of answer Phase 0 needs.
        diagnostics.BeginRequest(
            routeKind,
            appUserId,
            NullIfEmpty(itemId),
            position,
            NullIfEmpty(body?.PlaySessionId),
            NullIfEmpty(body?.MediaSourceId),
            body?.IsPaused,
            isStopped,
            datePlayed: null,
            datePlayedSupplied: false);

        if (appUserId is not { } userId)
        {
            return await CompleteAsync(diagnostics, Results.Unauthorized(), StatusCodes.Status401Unauthorized, cancellationToken);
        }

        if (string.IsNullOrEmpty(itemId))
        {
            return await CompleteAsync(diagnostics, Results.NoContent(), StatusCodes.Status204NoContent, cancellationToken);
        }

        // The session id is what keeps one viewing from counting several times when the user rewinds
        // past the watched threshold and watches forward again.
        await userData.ReportPlaybackAsync(
            userId, itemId, Math.Max(0, position ?? 0), isStopped, body?.PlaySessionId, diagnostics, cancellationToken);
        return await CompleteAsync(diagnostics, Results.NoContent(), StatusCodes.Status204NoContent, cancellationToken);
    }

    private static async Task<IResult> SetPlayedAsync(
        string userId, string itemId, bool played, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database,
        UserDataService userData, PlaybackDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var actingUserId = await JellyfinPrincipal.ResolveActingUserIdAsync(principal, userId, database, cancellationToken);
        return await SetPlayedCoreAsync(actingUserId, itemId, played, request, userData, diagnostics, cancellationToken);
    }

    private static async Task<IResult> SetPlayedByQueryUserAsync(
        string itemId, bool played, HttpRequest request, ClaimsPrincipal principal, MediaServerDbContext database,
        UserDataService userData, PlaybackDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var actingUserId = await JellyfinPrincipal.ResolveQueryUserIdAsync(request, principal, database, cancellationToken);
        return await SetPlayedCoreAsync(actingUserId, itemId, played, request, userData, diagnostics, cancellationToken);
    }

    private static async Task<IResult> SetPlayedCoreAsync(
        int? actingUserId, string itemId, bool played, HttpRequest request, UserDataService userData,
        PlaybackDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        // Only a mark carries DatePlayed; an unmark has nothing to date. Whether the client supplied
        // one at all is itself an observation, so it is recorded separately from the parsed value.
        var datePlayed = played ? ParseDatePlayed(request) : null;
        diagnostics.BeginRequest(
            played ? PlaybackRouteKinds.PlayedItemsPost : PlaybackRouteKinds.PlayedItemsDelete,
            actingUserId,
            NullIfEmpty(itemId),
            positionTicks: null,
            playSessionId: null,
            mediaSourceId: null,
            isPaused: null,
            isStopped: false,
            datePlayed,
            datePlayedSupplied: played && !string.IsNullOrEmpty(request.Query["DatePlayed"]));

        if (actingUserId is null)
        {
            return await CompleteAsync(diagnostics, Results.StatusCode(StatusCodes.Status403Forbidden), StatusCodes.Status403Forbidden, cancellationToken);
        }

        var data = await userData.SetPlayedAsync(actingUserId.Value, itemId, played, datePlayed, diagnostics, cancellationToken);
        return data is null
            ? await CompleteAsync(diagnostics, Results.NotFound(), StatusCodes.Status404NotFound, cancellationToken)
            : await CompleteAsync(diagnostics, JellyfinJson.Ok(data), StatusCodes.Status200OK, cancellationToken);
    }

    private static async Task<IResult> CompleteAsync(
        PlaybackDiagnostics diagnostics, IResult result, int statusCode, CancellationToken cancellationToken)
    {
        await diagnostics.CompleteAsync(statusCode, cancellationToken);
        return result;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

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
