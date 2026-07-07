using MediaServer.Api.Hosty;

namespace MediaServer.Api.Transcoding;

/// <summary>Internal transcode endpoints under <c>/api/transcode</c>, behind Host identity.</summary>
public static class TranscodeEndpoints
{
    public static void MapTranscodeEndpoints(this IEndpointRouteBuilder routes)
    {
        // Transcoding is an admin operation (re-encodes library files, consumes host resources, exposes
        // input/output paths), so the whole surface is admin-only — matching the UI gating.
        var group = routes.MapGroup("/api/transcode").RequireAuthorization(AppRoles.AdminPolicy);

        group.MapGet("/", async (TranscodeService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        group.MapPost("/", async (CreateTranscodeRequest request, TranscodeService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var job = await service.CreateAsync(request, cancellationToken);
                return Results.Created($"/api/transcode/{job.Id}", job);
            }
            catch (TranscodeConflictException exception)
            {
                // Concurrent state (the movie is mid-move), not a bad request — 409 like the move-locking surface.
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (TranscodeRequestException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        group.MapPost("/{id:guid}/cancel", async (Guid id, TranscodeService service, CancellationToken cancellationToken) =>
            await service.CancelAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/{id:guid}", async (Guid id, bool? deleteOutput, TranscodeService service, CancellationToken cancellationToken) =>
            await service.RemoveAsync(id, deleteOutput ?? false, cancellationToken) ? Results.NoContent() : Results.NotFound());
    }
}
