using System.Security.Claims;
using MediaServer.Api.Data;
using MediaServer.Api.Groups;
using MediaServer.Api.Hosty;

namespace MediaServer.Api.Native;

/// <summary>Read-only groups for first-party clients; configuration lives in web Settings.</summary>
public static class NativeGroupEndpoints
{
    public static void MapNativeGroupEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/groups", async (GroupService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(ct))).WithName("ListNativeGroups").RequireAuthorization().Produces<GroupSummaryDto[]>();
        group.MapGet("/groups/{id:guid}", async (Guid id, int? limit, int? offset, ClaimsPrincipal principal,
            MediaServerDbContext db, GroupService service, CancellationToken ct) =>
        {
            if (await principal.ResolveAppUserIdAsync(db, ct) is not { } userId) return Results.Unauthorized();
            var detail = await service.GetAsync(id, userId, limit ?? 60, offset ?? 0, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail with
            {
                Items = detail.Items.Select(i => i with
                { PosterUrl = i.PosterUrl is null ? null : $"{NativeEndpoints.RoutePrefix}/items/{i.Id:D}/images/primary" }).ToArray()
            });
        }).WithName("GetNativeGroup").RequireAuthorization().Produces<GroupDetailDto>().Produces(StatusCodes.Status404NotFound);
    }
}
