using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.People;
using MediaServer.Api.Realtime;
using MediaServer.Api.Recommendations;
using MediaServer.Api.WatchHistory;
using MediaServer.Api.Watchlist;

namespace MediaServer.Api.Native;

/// <summary>
/// The read-only surfaces a client browses beside the library itself: recommendations, the release
/// calendar and its reminders, people, the watch diary, and the realtime stream.
///
/// Each one is a <b>thin route over the same service the web route uses</b>, not a second
/// implementation. An earlier draft simply pointed the client at <c>/api</c>; that was rejected
/// because it would leave a large part of the client's contract outside the OpenAPI document, publish
/// an internal BFF shape as a public one, and make every change to a web-facing route a potential
/// client break. See <c>docs/features/native-client-api/plan.md</c>.
/// </summary>
public static class NativeDiscoveryEndpoints
{
    private const int DefaultRecommendationLimit = 20;
    private const int MaxRecommendationLimit = 60;

    public static void MapNativeDiscoveryEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/recommendations", async (
            RecommendationKind? kind,
            int? limit,
            ClaimsPrincipal principal,
            RecommendationFeedService feed,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await NativePrincipal.AppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var bounded = Math.Clamp(limit ?? DefaultRecommendationLimit, 1, MaxRecommendationLimit);
            return Results.Ok(await feed.BuildAsync(userId, kind, bounded, cancellationToken));
        }).RequireAuthorization();

        group.MapGet("/watchlist", async (
            ClaimsPrincipal principal,
            WatchlistService service,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await NativePrincipal.AppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await service.ListAsync(userId, cancellationToken));
        }).RequireAuthorization();

        group.MapGet("/releases/calendar", async (
            DateOnly from,
            DateOnly to,
            ClaimsPrincipal principal,
            WatchlistService service,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await NativePrincipal.AppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            if (to < from)
            {
                return Results.BadRequest(new { error = "'to' must not be before 'from'." });
            }

            return Results.Ok(await service.CalendarAsync(userId, from, to, cancellationToken));
        }).RequireAuthorization();

        group.MapGet("/reminders", async (
            ClaimsPrincipal principal,
            ReminderService service,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await NativePrincipal.AppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await service.ListAsync(userId, cancellationToken));
        }).RequireAuthorization();

        group.MapGet("/people/{provider}/{providerId}", async (
            string provider,
            string providerId,
            PersonReadService people,
            CancellationToken cancellationToken) =>
        {
            var person = await people.GetAsync(provider, providerId, cancellationToken);
            return person is null ? Results.NotFound() : Results.Ok(person);
        }).RequireAuthorization();

        group.MapGet("/history/calendar", async (
            DateTimeOffset from,
            DateTimeOffset toExclusive,
            ClaimsPrincipal principal,
            WatchHistoryCalendarService calendar,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await NativePrincipal.AppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            if (toExclusive <= from)
            {
                return Results.BadRequest(new { error = "'toExclusive' must be after 'from'." });
            }

            // The same bound the web route applies: one request must not ask the database to scan a
            // decade of history.
            if (toExclusive - from > WatchHistoryCalendarService.MaxRange)
            {
                return Results.BadRequest(new
                {
                    error = $"The requested range exceeds {WatchHistoryCalendarService.MaxRange.TotalDays:0} days.",
                });
            }

            return Results.Ok(await calendar.LoadAsync(userId, from, toExclusive, cancellationToken));
        }).RequireAuthorization();

        group.MapGet("/history/calendar/undated", async (
            MediaKind? kind,
            ClaimsPrincipal principal,
            WatchHistoryCalendarService calendar,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await NativePrincipal.AppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await calendar.LoadUndatedAsync(userId, kind, cancellationToken));
        }).RequireAuthorization();

        // The same stream the web UI consumes, mapped here so a native client does not have to reach
        // through the BFF proxy for it. Server→client only; operator actions go through REST.
        group.MapGet("/events", SseEndpoints.StreamAsync).RequireAuthorization();
    }
}
