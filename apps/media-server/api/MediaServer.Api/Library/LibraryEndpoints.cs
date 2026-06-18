using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// The internal <c>/api/library</c> surface for the UI: browse, detail, and episode listings, each
/// carrying the caller's per-user playback state. Projects the domain via <see cref="LibraryReadService"/>
/// (camelCase JSON); it never reaches into the Jellyfin surface.
/// </summary>
public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/library").RequireAuthorization();

        group.MapGet("/", async (
            Guid? catalogId,
            string? kind,
            ClaimsPrincipal principal,
            LibraryReadService library,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            var items = await library.ListAsync(catalogId, ParseKind(kind), appUserId, cancellationToken);
            return Results.Ok(items);
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            LibraryReadService library,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            var detail = await library.GetDetailAsync(id, appUserId, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        group.MapGet("/{id:guid}/episodes", async (
            Guid id,
            Guid? seasonId,
            ClaimsPrincipal principal,
            LibraryReadService library,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            var episodes = await library.GetEpisodesAsync(id, seasonId, appUserId, cancellationToken);
            return Results.Ok(episodes);
        });

        // Home rails.
        group.MapGet("/recent", async (
            int? limit, ClaimsPrincipal principal, LibraryReadService library, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            return Results.Ok(await library.GetRecentAsync(limit ?? 20, appUserId, cancellationToken));
        });

        group.MapGet("/resume", async (
            int? limit, ClaimsPrincipal principal, LibraryReadService library, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            return appUserId is { } userId
                ? Results.Ok(await library.GetResumeAsync(userId, limit ?? 20, cancellationToken))
                : Results.Ok(Array.Empty<LibraryRailItemDto>());
        });

        group.MapGet("/nextup", async (
            int? limit, ClaimsPrincipal principal, LibraryReadService library, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            return appUserId is { } userId
                ? Results.Ok(await library.GetNextUpAsync(userId, limit ?? 20, cancellationToken))
                : Results.Ok(Array.Empty<LibraryRailItemDto>());
        });

        // Per-user playback-state mutations (return the updated user data).
        group.MapPost("/{id:guid}/played", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken) =>
            SetPlayedAsync(id, played: true, principal, userData, database, cancellationToken));
        group.MapDelete("/{id:guid}/played", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken) =>
            SetPlayedAsync(id, played: false, principal, userData, database, cancellationToken));
        group.MapPost("/{id:guid}/favorite", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken) =>
            SetFavoriteAsync(id, favorite: true, principal, userData, database, cancellationToken));
        group.MapDelete("/{id:guid}/favorite", (Guid id, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken) =>
            SetFavoriteAsync(id, favorite: false, principal, userData, database, cancellationToken));

        // Delete a published item (admin only). `deleteFiles=true` also removes the library/ hardlinks.
        group.MapDelete("/{id:guid}", async (Guid id, bool? deleteFiles, LibraryDeleteService deleteService, CancellationToken cancellationToken) =>
        {
            var deleted = await deleteService.DeleteAsync(id, deleteFiles ?? false, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization(AppRoles.AdminPolicy);
    }

    private static MediaKind? ParseKind(string? kind) =>
        Enum.TryParse<MediaKind>(kind, ignoreCase: true, out var parsed) ? parsed : null;

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

    private static async Task<IResult> SetPlayedAsync(
        Guid id, bool played, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var data = await userData.SetPlayedAsync(userId, id, played, null, cancellationToken);
        return data is null ? Results.NotFound() : Results.Ok(data);
    }

    private static async Task<IResult> SetFavoriteAsync(
        Guid id, bool favorite, ClaimsPrincipal principal, UserDataService userData, MediaServerDbContext database, CancellationToken cancellationToken)
    {
        if (await ResolveAppUserIdAsync(principal, database, cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var data = await userData.SetFavoriteAsync(userId, id, favorite, cancellationToken);
        return data is null ? Results.NotFound() : Results.Ok(data);
    }
}
