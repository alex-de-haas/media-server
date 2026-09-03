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

        // Same contract for DHT health: seeded on mount, then kept live by `dhtStatusChanged`. Null when
        // downloading is disabled or the engine predates /dht — the UI hides the indicator either way.
        routes.MapGet("/api/dht", (ITorrentEngine engine) => Results.Ok(engine.GetDhtStatus())).RequireAuthorization();

        // The engine's OpenVPN profiles (torrent-engine 0.8.0+; null when it has none to report) and the switch.
        // Admin-only: where every download's traffic exits is operator configuration, not a user action.
        routes.MapGet("/api/vpn/profiles", async (ITorrentEngine engine, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await engine.GetVpnProfilesAsync(cancellationToken));
            }
            catch (EngineRequestException exception)
            {
                return Results.Problem(exception.Message, statusCode: (int)exception.StatusCode);
            }
        }).RequireAuthorization(AppRoles.AdminPolicy);

        routes.MapPut("/api/vpn/profile", async (SelectVpnProfileRequest request, ITorrentEngine engine, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Id))
            {
                return Results.Problem("A VPN profile id is required.", statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                // 202, like the engine: it records the choice and switches in the background; the web sees the
                // switch through `vpnStatusChanged` (pendingProfile, then the new profile).
                return Results.Accepted(value: await engine.SelectVpnProfileAsync(request.Id.Trim(), cancellationToken));
            }
            catch (EngineRequestException exception)
            {
                return Results.Problem(exception.Message, statusCode: (int)exception.StatusCode);
            }
        }).RequireAuthorization(AppRoles.AdminPolicy);

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
