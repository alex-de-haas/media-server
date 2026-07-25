using MediaServer.Api.Data;

namespace MediaServer.Api.Metadata;

/// <summary>
/// Metadata reads under <c>/api/metadata</c>, behind Host identity: a standalone search, and the detail
/// preview of a single title. Unlike the ingest re-search (which is keyed to an in-flight ingest item),
/// these are identity-only and feed operator and discovery flows — the library remap, and the preview
/// behind a recommendation, a tracked title or a search result. Reads only; they never mutate anything.
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

        group.MapGet("/{provider}/{id}", async (
            string provider,
            string id,
            MediaKind? kind,
            IMetadataProvider metadata,
            TitlePreviewService previews,
            CancellationToken cancellationToken) =>
        {
            if (!string.Equals(provider, metadata.Key, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = $"Unknown metadata provider '{provider}'." });
            }

            // The kind is required rather than inferred: TMDb's movie and tv id spaces overlap, so probing
            // one first would happily return an unrelated title whenever the ids collide.
            if (kind is not (MediaKind.Movie or MediaKind.Series))
            {
                return Results.BadRequest(new { error = "A kind of Movie or Series is required." });
            }

            var preview = await previews.GetAsync(new ProviderRef(metadata.Key, id), kind.Value, cancellationToken);
            return preview is null ? Results.NotFound() : Results.Ok(preview);
        });
    }
}

/// <summary>Body for <c>POST /api/metadata/search</c>. <see cref="Kind"/> defaults to <c>Movie</c>.</summary>
public sealed record MetadataSearchBody(string Title, int? Year, MediaKind? Kind);
