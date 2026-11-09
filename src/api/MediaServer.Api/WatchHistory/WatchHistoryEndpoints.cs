using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.WatchHistory;

/// <summary>The instant to stamp on a recorded play.</summary>
public sealed record SetWatchedAtRequest(DateTimeOffset? WatchedAt);

/// <summary>A selectable app user, without exposing their Host identity.</summary>
public sealed record WatchHistoryUser(int Id, string DisplayName, bool IsCurrentUser);

/// <summary>Authenticated watch-history reads, with owner-only entry edits.</summary>
public static class WatchHistoryEndpoints
{
    public static void MapWatchHistoryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/watch-history").RequireAuthorization();

        group.MapGet("/users", async (
            ClaimsPrincipal principal,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var currentUserId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
            if (currentUserId is null)
            {
                return Results.Unauthorized();
            }

            var users = await database.AppUsers.AsNoTracking()
                .Select(user => new { user.Id, user.DisplayName, user.Email })
                .ToListAsync(cancellationToken);
            return Results.Ok(users.Select(user => new WatchHistoryUser(
                    user.Id,
                    !string.IsNullOrWhiteSpace(user.DisplayName) ? user.DisplayName
                        : !string.IsNullOrWhiteSpace(user.Email) ? user.Email : $"User {user.Id}",
                    user.Id == currentUserId))
                .OrderBy(user => user.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(user => user.Id)
                .ToArray());
        }).RequireAuthorization(AppRoles.AdminPolicy);

        group.MapGet("/calendar", async (
            DateTimeOffset from,
            DateTimeOffset toExclusive,
            int? userId,
            ClaimsPrincipal principal,
            WatchHistoryCalendarService calendar,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var (selectedUserId, error) = await ResolveReadUserAsync(principal, userId, database, cancellationToken);
            if (error is not null)
            {
                return error;
            }

            if (toExclusive <= from)
            {
                return Results.BadRequest(new { error = "'toExclusive' must be after 'from'." });
            }

            return Results.Ok(await calendar.LoadAsync(selectedUserId, from, toExclusive, cancellationToken));
        });

        group.MapGet("/calendar/undated", async (
            MediaKind? kind,
            int? userId,
            ClaimsPrincipal principal,
            WatchHistoryCalendarService calendar,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var (selectedUserId, error) = await ResolveReadUserAsync(principal, userId, database, cancellationToken);
            return error ?? Results.Ok(await calendar.LoadUndatedAsync(selectedUserId, kind, cancellationToken));
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

    private static async Task<(int UserId, IResult? Error)> ResolveReadUserAsync(
        ClaimsPrincipal principal,
        int? requestedUserId,
        MediaServerDbContext database,
        CancellationToken cancellationToken)
    {
        if (await principal.ResolveAppUserIdAsync(database, cancellationToken) is not { } currentUserId)
        {
            return (0, Results.Unauthorized());
        }

        if (requestedUserId is not { } selectedUserId || selectedUserId == currentUserId)
        {
            return (currentUserId, null);
        }

        // Authorize before looking up the target, so ordinary users cannot probe app membership.
        if (!principal.IsInRole(AppRoles.Admin))
        {
            return (0, Results.Forbid());
        }

        return await database.AppUsers.AnyAsync(user => user.Id == selectedUserId, cancellationToken)
            ? (selectedUserId, null)
            : (0, Results.NotFound());
    }

    internal static IResult ToResult(SetWatchedAtStatus status) => status switch
    {
        SetWatchedAtStatus.Updated => Results.NoContent(),
        SetWatchedAtStatus.NotFound => Results.NotFound(),
        _ => Results.BadRequest(new { error = "'watchedAt' cannot be in the future." }),
    };

}
