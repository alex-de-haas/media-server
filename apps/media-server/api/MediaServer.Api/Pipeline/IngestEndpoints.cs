namespace MediaServer.Api.Pipeline;

/// <summary>Internal pipeline/review endpoints under <c>/api/ingest</c>, behind Host identity.</summary>
public static class IngestEndpoints
{
    public static void MapIngestEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/ingest").RequireAuthorization();

        group.MapGet("/", async (IngestService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        group.MapGet("/{id:guid}", async (Guid id, IngestService service, CancellationToken cancellationToken) =>
        {
            var item = await service.GetAsync(id, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapPost("/{id:guid}/retry", async (Guid id, IngestService service, CancellationToken cancellationToken) =>
            await service.RetryAsync(id, cancellationToken) ? Results.Accepted() : Results.NotFound());

        group.MapPost("/{id:guid}/match", async (Guid id, MatchRequest request, IngestService service, CancellationToken cancellationToken) =>
            await service.MatchAsync(id, request, cancellationToken) ? Results.Accepted() : Results.NotFound());
    }
}
