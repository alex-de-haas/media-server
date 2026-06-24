namespace MediaServer.Api.People;

/// <summary>
/// The internal <c>/api/persons</c> surface for the UI: a person page keyed by its provider identity
/// (<c>provider/providerId</c>), the same pair the library detail's cast members carry. Returns the person's
/// details plus their filmography within the library; read-only, behind Host identity.
/// </summary>
public static class PersonEndpoints
{
    public static void MapPersonEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/persons").RequireAuthorization();

        group.MapGet("/{provider}/{providerId}", async (
            string provider,
            string providerId,
            PersonReadService people,
            CancellationToken cancellationToken) =>
        {
            var person = await people.GetAsync(provider, providerId, cancellationToken);
            return person is null ? Results.NotFound() : Results.Ok(person);
        });
    }
}
