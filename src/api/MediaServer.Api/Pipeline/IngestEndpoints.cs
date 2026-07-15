using MediaServer.Api.Data;
using MediaServer.Api.Hosty;

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

        group.MapPost("/{id:guid}/search", async (Guid id, MetadataSearchRequest request, IngestService service, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new { error = "A title is required to re-search." });
            }

            var candidates = await service.SearchAsync(id, request, cancellationToken);
            return candidates is null ? Results.NotFound() : Results.Ok(candidates);
        });

        // Match source files to one confirmed identity (the series for episodes, the movie otherwise) —
        // single and bulk share the endpoint since a batch never mixes titles. Files that were auto-matched
        // may be re-matched here while the item is still in review.
        group.MapPost("/{id:guid}/match", async (Guid id, MatchRequest request, IngestService service, CancellationToken cancellationToken) =>
        {
            if (request.Files is not { Count: > 0 })
            {
                return Results.BadRequest(new { error = "At least one source file is required to match." });
            }

            if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.ProviderId) || string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new { error = "An identity (provider, id, title) is required to match." });
            }

            return await service.MatchAsync(id, request, cancellationToken) switch
            {
                MatchOutcome.NotFound => Results.NotFound(),
                MatchOutcome.FileNotFound => Results.NotFound(new { error = "One or more source files were not found on this ingest." }),
                MatchOutcome.AlreadyOrganized => Results.Conflict(new { error = "This item has already been identified — remap it from its library page instead." }),
                _ => Results.Accepted(),
            };
        });

        // Skip unmatchable files (creditless OP/EDs and other extras absent from the provider) so the rest
        // of the batch can proceed without them. Skipped files are never imported.
        group.MapPost("/{id:guid}/skip", async (Guid id, SkipRequest? request, IngestService service, CancellationToken cancellationToken) =>
        {
            if (request?.SourceFileIds is not { Count: > 0 })
            {
                return Results.BadRequest(new { error = "At least one source file id is required to skip." });
            }

            return await service.SkipAsync(id, request, cancellationToken) switch
            {
                SkipOutcome.NotFound => Results.NotFound(),
                SkipOutcome.FileNotFound => Results.NotFound(new { error = "One or more source files were not found on this ingest." }),
                SkipOutcome.AlreadyOrganized => Results.Conflict(new { error = "This item has already been identified — remap it from its library page instead." }),
                _ => Results.Accepted(),
            };
        });

        // Attach files to a series as playable extras (creditless OP/EDs, PVs, …) — the keep-it alternative
        // to /skip for content the provider has no identity for. Single and bulk share one endpoint.
        group.MapPost("/{id:guid}/extras", async (Guid id, AssignExtrasRequest request, IngestService service, CancellationToken cancellationToken) =>
        {
            if (request.SourceFileIds is not { Count: > 0 })
            {
                return Results.BadRequest(new { error = "At least one source file id is required." });
            }

            if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.ProviderId) || string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new { error = "A series identity (provider, id, title) is required." });
            }

            return await service.AssignExtrasAsync(id, request, cancellationToken) switch
            {
                AssignExtrasOutcome.NotFound => Results.NotFound(),
                AssignExtrasOutcome.FileNotFound => Results.NotFound(new { error = "One or more source files were not found on this ingest." }),
                AssignExtrasOutcome.MovieCatalog => Results.BadRequest(new { error = "Extras attach to a series; this is a movie catalog." }),
                AssignExtrasOutcome.AlreadyOrganized => Results.Conflict(new { error = "This item has already been identified — remap it from its library page instead." }),
                _ => Results.Accepted(),
            };
        });

        // Pin a target identity before/while an item downloads so Identify resolves straight to it (never
        // routing to review). Rejected with 409 once the item has already been identified — use library remap.
        group.MapPost("/{id:guid}/pin", async (Guid id, PinIdentityRequest request, IngestService service, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.ProviderId) || string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new { error = "provider, providerId and title are required to pin an identity." });
            }

            // A pin targets a movie or a whole series; Season/Episode/Video aren't valid item-level identities.
            if (request.Kind is not (MediaKind.Movie or MediaKind.Series))
            {
                return Results.BadRequest(new { error = "kind must be Movie or Series." });
            }

            return await service.PinAsync(id, request, cancellationToken) switch
            {
                PinOutcome.NotFound => Results.NotFound(),
                PinOutcome.AlreadyIdentified => Results.Conflict(new { error = "This item has already been identified — edit it from its library page instead." }),
                PinOutcome.InvalidKind => Results.BadRequest(new { error = "The pinned kind must match the catalog type (Movie for a movie catalog, Series for a series/anime catalog)." }),
                _ => Results.Accepted(),
            };
        });

        group.MapDelete("/{id:guid}/pin", async (Guid id, IngestService service, CancellationToken cancellationToken) =>
            await service.UnpinAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        // Operator safety valve: remove a single pipeline tracking row (e.g. an orphaned/stuck entry).
        // Admin-only and destructive-by-intent, though it only deletes the ingest row itself.
        group.MapDelete("/{id:guid}", async (Guid id, IngestService service, CancellationToken cancellationToken) =>
            await service.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(AppRoles.AdminPolicy);

        // Bulk companion to the single delete: clears every published row from the Done tab in one action.
        group.MapDelete("/done", async (IngestService service, CancellationToken cancellationToken) =>
            Results.Ok(new { removed = await service.DeleteCompletedAsync(cancellationToken) }))
            .RequireAuthorization(AppRoles.AdminPolicy);
    }
}
