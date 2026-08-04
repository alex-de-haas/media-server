using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;

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
            var appUserId = await principal.ResolveAppUserIdAsync(database, cancellationToken);
            var detail = await collections.GetAsync(id, appUserId, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });
    }
}
