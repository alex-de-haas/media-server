using MediaServer.Api.Hosty;
using MediaServer.Api.Probe;

namespace MediaServer.Api.Transcoding;

/// <summary>Internal transcode endpoints under <c>/api/transcode</c>, behind Host identity.</summary>
public static class TranscodeEndpoints
{
    public static void MapTranscodeEndpoints(this IEndpointRouteBuilder routes)
    {
        // Transcoding is an admin operation (re-encodes library files, consumes host resources, exposes
        // input/output paths), so the whole surface is admin-only — matching the UI gating.
        var group = routes.MapGroup("/api/transcode").RequireAuthorization(AppRoles.AdminPolicy);

        // Whether the engine dependency is attached at all. The Media tab itself stays available without it
        // — it lists a title's versions and picks which one plays, neither of which needs an engine — but
        // the conversion controls have nothing to talk to, so the UI hides them rather than offering an
        // action that can only fail.
        group.MapGet("/availability", (ITranscodeEngine engine) =>
            Results.Ok(new { available = engine is not DisabledTranscodeEngine }));

        // Every language tag a track edit may carry — the canonical forms plus the spellings that fold onto
        // them, because a client validating against the canonical set alone would refuse values this service
        // accepts. Served rather than duplicated in the web bundle: the two copies would drift, and the half
        // that drifts is the one that lets an operator submit a value this service then refuses — after they
        // have filled in the whole dialog.
        group.MapGet("/languages", () => Results.Ok(LanguageTags.Accepted));

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

        // Writing a version's own tracks out as files beside it — the inverse of the merge the endpoint above
        // composes. Its own route rather than a mode of that one: it shares no field with a conversion, and
        // folding two disjoint request shapes into one body would make every field on both conditionally
        // valid.
        group.MapPost("/extract", async (CreateExtractionRequest request, TrackExtractionService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var job = await service.CreateAsync(request, cancellationToken);
                return Results.Created($"/api/transcode/{job.Id}", job);
            }
            catch (TranscodeConflictException exception)
            {
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
