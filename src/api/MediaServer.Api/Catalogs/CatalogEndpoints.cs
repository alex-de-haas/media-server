using MediaServer.Api.Hosty;

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
    }
}
