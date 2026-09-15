using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;

namespace MediaServer.Api.Groups;

/// <summary>Group browsing and settings, with the same read/admin boundary as catalogs.</summary>
public static class GroupEndpoints
{
    public static void MapGroupEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/groups").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (GroupValidationException error) { return Results.Problem(error.Message, statusCode: 400); }
        });
        group.MapGet("/", async (GroupService service, CancellationToken ct) => Results.Ok(await service.ListAsync(ct)));
        group.MapGet("/{id:guid}", async (Guid id, int? limit, int? offset, ClaimsPrincipal principal,
            MediaServerDbContext db, GroupService service, CancellationToken ct) =>
        {
            var detail = await service.GetAsync(id, await principal.ResolveAppUserIdAsync(db, ct), limit ?? 60, offset ?? 0, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });
        group.MapGet("/{id:guid}/definition", async (Guid id, GroupService service, CancellationToken ct) =>
        {
            var definition = await service.DefinitionAsync(id, ct);
            return definition is null ? Results.NotFound() : Results.Ok(definition);
        }).RequireAuthorization(AppRoles.AdminPolicy);
        group.MapGet("/options", async (string catalogType, string? search, GroupService service, CancellationToken ct) =>
            Results.Ok(await service.OptionsAsync(catalogType, search, ct))).RequireAuthorization(AppRoles.AdminPolicy);
        group.MapGet("/candidates", async (string catalogType, string? title, int? limit, int? offset, ClaimsPrincipal principal,
            MediaServerDbContext db, GroupService service, CancellationToken ct) =>
            Results.Ok(await service.CandidatesAsync(catalogType, title, await principal.ResolveAppUserIdAsync(db, ct), limit ?? 30, offset ?? 0, ct)))
            .RequireAuthorization(AppRoles.AdminPolicy);
        group.MapPost("/preview", async (SaveGroupRequest request, int? limit, int? offset, ClaimsPrincipal principal,
            MediaServerDbContext db, GroupService service, CancellationToken ct) =>
            Results.Ok(await service.PreviewAsync(request, await principal.ResolveAppUserIdAsync(db, ct), limit ?? 12, offset ?? 0, ct)))
            .RequireAuthorization(AppRoles.AdminPolicy);
        group.MapPost("/", async (SaveGroupRequest request, GroupService service, CancellationToken ct) =>
        {
            var saved = await service.SaveAsync(null, request, ct);
            return Results.Created($"/api/groups/{saved!.Id}", saved);
        }).RequireAuthorization(AppRoles.AdminPolicy);
        group.MapPut("/{id:guid}", async (Guid id, SaveGroupRequest request, GroupService service, CancellationToken ct) =>
        {
            var saved = await service.SaveAsync(id, request, ct);
            return saved is null ? Results.NotFound() : Results.Ok(saved);
        }).RequireAuthorization(AppRoles.AdminPolicy);
        group.MapDelete("/{id:guid}", async (Guid id, GroupService service, CancellationToken ct) =>
            await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound()).RequireAuthorization(AppRoles.AdminPolicy);
    }
}
