using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;

namespace MediaServer.Api.WatchHistory;

/// <summary>The instant to stamp on a recorded play.</summary>
public sealed record SetWatchedAtRequest(DateTimeOffset? WatchedAt);

/// <summary>Authenticated endpoints for the caller's local watch history.</summary>
public static class WatchHistoryEndpoints
{
    public static void MapWatchHistoryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/watch-history").RequireAuthorization();

        group.MapGet("/calendar", async (
            DateTimeOffset from,
            DateTimeOffset toExclusive,
            ClaimsPrincipal principal,
            WatchHistoryCalendarService calendar,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var user = await principal.ResolveAppUserAsync(database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (toExclusive <= from)
            {
                return Results.BadRequest(new { error = "'toExclusive' must be after 'from'." });
            }

            return Results.Ok(await calendar.LoadAsync(user.Id, from, toExclusive, cancellationToken));
        });

        group.MapGet("/calendar/undated", async (
            MediaKind? kind,
            ClaimsPrincipal principal,
            WatchHistoryCalendarService calendar,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var user = await principal.ResolveAppUserAsync(database, cancellationToken);
            return user is null
                ? Results.Unauthorized()
                : Results.Ok(await calendar.LoadUndatedAsync(user.Id, kind, cancellationToken));
        });

        // Sets when a play happened: a mark that was never timed takes its instant, and one timed
        // wrongly is moved to the right one. The play itself is untouched either way — nothing is
        // recorded and nothing is removed, so the item's play count does not move.
        group.MapPatch("/entries/{entryId:guid}", async (
            Guid entryId,
            SetWatchedAtRequest request,
            ClaimsPrincipal principal,
            WatchHistoryEntryService entries,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var user = await principal.ResolveAppUserAsync(database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            if (request.WatchedAt is not { } watchedAt)
            {
                return Results.BadRequest(new { error = "'watchedAt' is required." });
            }

            return ToResult(await entries.SetWatchedAtAsync(user.Id, entryId, watchedAt, cancellationToken));
        });

        group.MapDelete("/entries/{entryId:guid}", async (
            Guid entryId,
            ClaimsPrincipal principal,
            WatchHistoryEntryService entries,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var user = await principal.ResolveAppUserAsync(database, cancellationToken);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            // "Delete this play" names a specific row, and
            // answering 204 for an id the caller does not own would confirm that it exists.
            return await entries.DeleteAsync(user.Id, entryId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

    }

    internal static IResult ToResult(SetWatchedAtStatus status) => status switch
    {
        SetWatchedAtStatus.Updated => Results.NoContent(),
        SetWatchedAtStatus.NotFound => Results.NotFound(),
        _ => Results.BadRequest(new { error = "'watchedAt' cannot be in the future." }),
    };

}
