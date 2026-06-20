using MediaServer.Api.Data;

namespace MediaServer.Api.Metadata;

/// <summary>
/// A standalone metadata search under <c>/api/metadata</c>, behind Host identity. Unlike the ingest
/// re-search (which is keyed to an in-flight ingest item), this is identity-only and feeds operator flows
/// over already-published items — notably the library remap. Search only; it never mutates anything.
/// </summary>
public static class MetadataEndpoints
{
    public static void MapMetadataEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/metadata").RequireAuthorization();

        group.MapPost("/search", async (MetadataSearchBody request, IMetadataProvider provider, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title))
            {
                return Results.BadRequest(new { error = "A title is required to search." });
            }

            var kind = request.Kind ?? MediaKind.Movie;
            var title = request.Title.Trim();
            var results = await provider.SearchAsync(new MediaQuery(kind, title, request.Year), cancellationToken);

            // The year is a hint, not a hard filter: TMDb's year-constrained search returns nothing for a
            // title whose release date doesn't match (or isn't set yet), so fall back to a yearless search.
            if (results.Count == 0 && request.Year is not null)
            {
                results = await provider.SearchAsync(new MediaQuery(kind, title, null), cancellationToken);
            }

            return Results.Ok(results);
        });
    }
}

/// <summary>Body for <c>POST /api/metadata/search</c>. <see cref="Kind"/> defaults to <c>Movie</c>.</summary>
public sealed record MetadataSearchBody(string Title, int? Year, MediaKind? Kind);
