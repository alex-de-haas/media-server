using MediaServer.Api.Hosty;
using MediaServer.Api.Library;

namespace MediaServer.Api.Catalogs;

/// <summary>
/// Internal catalog management endpoints under <c>/api/catalogs</c>. Reads are open to any Host user
/// (the add-torrent flow needs the catalog list); writes require the admin role.
/// </summary>
public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/catalogs").RequireAuthorization();

        group.MapGet("/", async (CatalogService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        // The catalog-root mounts a catalog may live under, for the UI's mount picker.
        group.MapGet("/mounts", (CatalogService service) => Results.Ok(service.ListMounts()));

        // Per-volume storage usage (free space + each catalog's footprint) for the storage bars.
        group.MapGet("/usage", async (CatalogService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListUsageAsync(cancellationToken)));

        group.MapGet("/{id:guid}", async (Guid id, CatalogService service, CancellationToken cancellationToken) =>
        {
            var catalog = await service.GetAsync(id, cancellationToken);
            return catalog is null ? Results.NotFound() : Results.Ok(catalog);
        });

        group.MapPost("/", async (CreateCatalogRequest request, CatalogService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var catalog = await service.CreateAsync(request, cancellationToken);
                return Results.Created($"/api/catalogs/{catalog.Id}", catalog);
            }
            catch (CatalogValidationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Re-points a catalog at a catalog-root mount. Needed when a mount is renamed or its volume moves,
        // and for any catalog created before roots were anchored to mount labels whose stored path this
        // runtime cannot see (see CatalogAnchorService).
        group.MapPost("/{id:guid}/anchor", async (
            Guid id,
            AnchorCatalogRequest request,
            CatalogAnchorService anchors,
            CatalogService service,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var catalog = await anchors.AnchorAsync(id, request.MountLabel, request.RelativePath, cancellationToken);
                return catalog is null
                    ? Results.NotFound()
                    : Results.Ok(await service.GetAsync(id, cancellationToken));
            }
            catch (CatalogValidationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (CatalogInUseException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).RequireAuthorization(AppRoles.AdminPolicy);

        group.MapPatch("/{id:guid}", async (Guid id, UpdateCatalogRequest request, CatalogService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var catalog = await service.UpdateAsync(id, request, cancellationToken);
                return catalog is null ? Results.NotFound() : Results.Ok(catalog);
            }
            catch (CatalogValidationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).RequireAuthorization(AppRoles.AdminPolicy);

        group.MapDelete("/{id:guid}", async (Guid id, CatalogService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var deleted = await service.DeleteAsync(id, cancellationToken);
                return deleted ? Results.NoContent() : Results.NotFound();
            }
            catch (CatalogInUseException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
            }
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // Sync one catalog with its disk: ingest orphan media files from the identify stage, and remove
        // the library rows whose files are gone (as tombstones where a user's history is at stake).
        group.MapPost("/{id:guid}/scan", async (Guid id, CatalogScanService scan, CancellationToken cancellationToken) =>
        {
            var report = await scan.ScanAsync(id, cancellationToken);
            return report is null ? Results.NotFound() : Results.Ok(report);
        }).RequireAuthorization(AppRoles.AdminPolicy);

        // The same, for every catalog at once — the Catalogs page's global action, and what the nightly
        // job runs. Sequential: one catalog's disk at a time is plenty, and it paces the pipeline.
        group.MapPost("/scan", async (CatalogScanService scan, CancellationToken cancellationToken) =>
            Results.Ok(await scan.ScanAllAsync(cancellationToken)))
            .RequireAuthorization(AppRoles.AdminPolicy);

        // Refresh every catalog's metadata (admin only). Queued the same way as one catalog's, so they run
        // one at a time; catalogs already refreshing are left to the run they are in.
        group.MapPost("/refresh-metadata", async (CatalogRefreshCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Accepted("/api/catalogs/refresh-metadata/active",
                new { started = await coordinator.RequestAllAsync(cancellationToken) }))
            .RequireAuthorization(AppRoles.AdminPolicy);

        // The catalogs with a metadata refresh currently in flight (job id + progress), for the UI.
        group.MapGet("/refresh-metadata/active", async (CatalogRefreshCoordinator coordinator, CancellationToken cancellationToken) =>
            Results.Ok(await coordinator.ListActiveAsync(cancellationToken)))
            .RequireAuthorization(AppRoles.AdminPolicy);

        // Re-fetch provider metadata + images for every identified item in the catalog (admin only). Runs in
        // the background as an observable job; progress streams over /api/events. 409 if one is already running.
        group.MapPost("/{id:guid}/refresh-metadata", async (Guid id, CatalogRefreshCoordinator coordinator, CancellationToken cancellationToken) =>
        {
            var result = await coordinator.RequestAsync(id, cancellationToken);
            return result.Status switch
            {
                CatalogRefreshRequestStatus.Started => Results.Accepted($"/api/catalogs/{id}/refresh-metadata", new { jobId = result.JobId }),
                CatalogRefreshRequestStatus.AlreadyRunning => Results.Conflict(new { error = "A metadata refresh is already running for this catalog." }),
                _ => Results.NotFound(),
            };
        }).RequireAuthorization(AppRoles.AdminPolicy);
    }
}
