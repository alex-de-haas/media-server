namespace MediaServer.Api.Torrents;

/// <summary>Internal torrent endpoints under <c>/api/torrents</c>, behind Host identity.</summary>
public static class TorrentEndpoints
{
    public static void MapTorrentEndpoints(this IEndpointRouteBuilder routes)
    {
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

        group.MapDelete("/{id:guid}", async (Guid id, bool? deleteFiles, TorrentService service, CancellationToken cancellationToken) =>
            await service.RemoveAsync(id, deleteFiles ?? false, cancellationToken) ? Results.NoContent() : Results.NotFound());
    }
}
