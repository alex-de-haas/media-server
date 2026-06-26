using MediaServer.Api.Hosty;

namespace MediaServer.Api.Transcoding;

/// <summary>Internal transcode endpoints under <c>/api/transcode</c>, behind Host identity.</summary>
public static class TranscodeEndpoints
{
    public static void MapTranscodeEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/transcode").RequireAuthorization();

        group.MapGet("/", async (TranscodeService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        group.MapPost("/", async (CreateTranscodeRequest request, TranscodeService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var job = await service.CreateAsync(request, cancellationToken);
                return Results.Created($"/api/transcode/{job.Id}", job);
            }
            catch (TranscodeRequestException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/{id:guid}/cancel", async (Guid id, TranscodeService service, CancellationToken cancellationToken) =>
            await service.CancelAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        // Destructive: can delete the produced output file, so it is admin-only (matching library delete).
        group.MapDelete("/{id:guid}", async (Guid id, bool? deleteOutput, TranscodeService service, CancellationToken cancellationToken) =>
            await service.RemoveAsync(id, deleteOutput ?? false, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(AppRoles.AdminPolicy);
    }
}
