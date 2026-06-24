using System.Security.Claims;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Collections;

/// <summary>
/// The internal <c>/api/library/collections</c> surface for the UI: the franchise grid and a single
/// collection's member movies, each carrying the caller's per-user playback state. Read-only, behind Host
/// identity; projects the domain via <see cref="CollectionReadService"/> (camelCase JSON).
/// </summary>
public static class CollectionEndpoints
{
    public static void MapCollectionEndpoints(this IEndpointRouteBuilder routes)
    {
        // Nested under /api/library; the "collections" literal never collides with the library group's
        // /{id:guid} route because the guid constraint rejects it.
        var group = routes.MapGroup("/api/library/collections").RequireAuthorization();

        group.MapGet("/", async (
            CollectionReadService collections,
            CancellationToken cancellationToken) =>
        {
            return Results.Ok(await collections.ListAsync(cancellationToken));
        });

        group.MapGet("/{id:guid}", async (
            Guid id,
            ClaimsPrincipal principal,
            CollectionReadService collections,
            MediaServerDbContext database,
            CancellationToken cancellationToken) =>
        {
            var appUserId = await ResolveAppUserIdAsync(principal, database, cancellationToken);
            var detail = await collections.GetAsync(id, appUserId, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
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
