using MediaServer.Api.Hosty;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;

namespace MediaServer.Api.Library;

public static class ManagementEndpoints
{
    public static void MapManagementEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/management").RequireAuthorization(AppRoles.AdminPolicy);
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ManagementValidationException error) { return Results.Problem(error.Message, statusCode: 400); }
        });
        group.MapGet("/items/{id:guid}/location", async (Guid id, MediaServerDbContext db, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().Where(i => i.Id == id && i.PublicId != null && i.RemovedAt == null)
                .Select(i => new { i.Id, Kind = i.Kind.ToString(), i.SeriesId }).SingleOrDefaultAsync(ct);
            return item == null ? Results.NotFound() : Results.Ok(item);
        });
        group.MapGet("/items", async ([AsParameters] ManagementQuery query, ManagementService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(query, ct)));
        group.MapGet("/items/{id:guid}/sources", async (Guid id, [AsParameters] ManagementQuery query, ManagementService service, CancellationToken ct) =>
            Results.Ok(await service.SourcesAsync(id, query, ct)));

        // A separate, path-free projection for viewers; it does not expose management queries.
        routes.MapGet("/api/library/availability", async ([FromQuery] Guid[] ids, ManagementService service, CancellationToken ct) =>
        {
            if (ids.Length > 100) return Results.BadRequest(new { error = "Request at most 100 titles." });
            return Results.Ok(await service.AvailabilityAsync(ids.Distinct().ToArray(), ct));
        }).RequireAuthorization();
    }
}
