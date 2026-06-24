using MediaServer.Api.Hosty;

namespace MediaServer.Api.Torrents;

/// <summary>Internal torrent endpoints under <c>/api/torrents</c>, behind Host identity.</summary>
public static class TorrentEndpoints
{
    public static void MapTorrentEndpoints(this IEndpointRouteBuilder routes)
    {
        // Engine-wide VPN tunnel status (null when the engine reports none, or downloading is disabled). The
        // web seeds this on mount, then keeps it live from the `vpnStatusChanged` SSE event.
        routes.MapGet("/api/vpn", (ITorrentEngine engine) => Results.Ok(engine.GetVpnStatus())).RequireAuthorization();

        var group = routes.MapGroup("/api/torrents").RequireAuthorization();

        group.MapGet("/", async (TorrentService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        group.MapPost("/add", async (AddTorrentRequest request, TorrentService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var download = await service.AddAsync(request, cancellationToken);
                return Results.Created($"/api/torrents/{download.Id}", download);
            }
            catch (TorrentRequestException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/{id:guid}/pause", async (Guid id, TorrentService service, CancellationToken cancellationToken) =>
            await service.PauseAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/{id:guid}/resume", async (Guid id, TorrentService service, CancellationToken cancellationToken) =>
            await service.ResumeAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/{id:guid}/stop-seeding", async (Guid id, TorrentService service, CancellationToken cancellationToken) =>
            await service.StopSeedingAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        // Destructive: can purge produced library items + files, so it is admin-only (matching library delete).
        group.MapDelete("/{id:guid}", async (Guid id, bool? deleteFiles, DownloadDeletionService deletion, CancellationToken cancellationToken) =>
            await deletion.DeleteAsync(id, deleteFiles ?? false, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(AppRoles.AdminPolicy);
    }
}
