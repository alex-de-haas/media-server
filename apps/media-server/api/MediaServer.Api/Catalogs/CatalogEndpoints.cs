namespace MediaServer.Api.Catalogs;

/// <summary>Internal catalog management endpoints under <c>/api/catalogs</c>, behind Host identity.</summary>
public static class CatalogEndpoints
{
    public static void MapCatalogEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/catalogs").RequireAuthorization();

        group.MapGet("/", async (CatalogService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

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
        });

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
        });

        group.MapDelete("/{id:guid}", async (Guid id, CatalogService service, CancellationToken cancellationToken) =>
        {
            var deleted = await service.DeleteAsync(id, cancellationToken);
            return deleted ? Results.NoContent() : Results.NotFound();
        });
    }
}
