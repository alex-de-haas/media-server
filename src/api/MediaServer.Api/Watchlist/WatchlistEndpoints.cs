using System.Security.Claims;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Watchlist;

/// <summary>
/// The internal <c>/api/watchlist</c> + <c>/api/reminders</c> surface (camelCase, Host identity).
/// Discrete commands so the same operations can be exposed as MCP tools in M6. Every route resolves the
/// acting <see cref="AppUser"/> and passes its id down — reads and mutations are always user-scoped.
/// </summary>
public static class WatchlistEndpoints
{
    public static void MapWatchlistEndpoints(this IEndpointRouteBuilder routes)
    {
        var watchlist = routes.MapGroup("/api/watchlist").RequireAuthorization();

        watchlist.MapGet("/", async (
            ClaimsPrincipal principal, WatchlistService service, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await service.ListAsync(userId, cancellationToken));
        });

        watchlist.MapPost("/", async (
            AddWatchlistRequest request, ClaimsPrincipal principal, WatchlistService service, MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var result = await service.AddAsync(userId, request, cancellationToken);
            if (result.Error is not null)
            {
                return Results.BadRequest(new { error = result.Error });
            }

            // Adding an already tracked title is idempotent: the existing entry comes back with a 200.
            return result.Created ? Results.Created($"/api/watchlist/{result.Item!.Id}", result.Item) : Results.Ok(result.Item);
        });

        watchlist.MapPatch("/{id:guid}", async (
            Guid id, UpdateWatchlistRequest request, ClaimsPrincipal principal, WatchlistService service,
            MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var item = await service.UpdateAsync(userId, id, request, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        watchlist.MapDelete("/{id:guid}", async (
            Guid id, ClaimsPrincipal principal, WatchlistService service, MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            return await service.RemoveAsync(userId, id, cancellationToken) ? Results.NoContent() : Results.NotFound();
        });

        watchlist.MapGet("/calendar", async (
            DateOnly from, DateOnly to, ClaimsPrincipal principal, WatchlistService service, MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            if (to < from)
            {
                return Results.BadRequest(new { error = "'to' must not be before 'from'." });
            }

            return Results.Ok(await service.CalendarAsync(userId, from, to, cancellationToken));
        });

        watchlist.MapPost("/{id:guid}/refresh", async (
            Guid id, ClaimsPrincipal principal, WatchlistService service, MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            return await service.RefreshAsync(userId, id, cancellationToken) ? Results.Accepted() : Results.NotFound();
        });

        var reminders = routes.MapGroup("/api/reminders").RequireAuthorization();

        reminders.MapGet("/", async (
            ClaimsPrincipal principal, ReminderService service, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(await service.ListAsync(userId, cancellationToken));
        });

        reminders.MapPost("/", async (
            CreateReminderRequest request, ClaimsPrincipal principal, ReminderService service, MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var result = await service.CreateAsync(userId, request, cancellationToken);
            if (result.Error is not null)
            {
                return Results.BadRequest(new { error = result.Error });
            }

            return result.Created
                ? Results.Created($"/api/reminders/{result.Resolution!.Reminder.Id}", result.Resolution)
                : Results.Ok(result.Resolution);
        });

        reminders.MapPatch("/{id:guid}", async (
            Guid id, UpdateReminderRequest request, ClaimsPrincipal principal, ReminderService service,
            MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            var reminder = await service.UpdateAsync(userId, id, request, cancellationToken);
            return reminder is null ? Results.NotFound() : Results.Ok(reminder);
        });

        reminders.MapDelete("/{id:guid}", async (
            Guid id, ClaimsPrincipal principal, ReminderService service, MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
            {
                return Results.Unauthorized();
            }

            return await service.DeleteAsync(userId, id, cancellationToken) ? Results.NoContent() : Results.NotFound();
        });
    }

    /// <summary>Maps the validated Host principal to the internal app user id (null if not yet provisioned).</summary>
    private static async Task<int?> ResolveAppUserIdAsync(
        ClaimsPrincipal principal, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        var hostUserId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(hostUserId))
        {
            return null;
        }

        return await database.AppUsers.AsNoTracking()
            .Where(user => user.HostUserId == hostUserId)
            .Select(user => (int?)user.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
