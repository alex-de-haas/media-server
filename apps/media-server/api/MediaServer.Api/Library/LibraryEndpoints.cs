using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

public sealed record LibraryItemResponse(
    Guid Id,
    string? PublicId,
    Guid CatalogId,
    string Kind,
    string Title,
    int? Year,
    string? LibraryPath,
    string? PosterUrl);

/// <summary>
/// Read-only listing of published library items for the UI. The database is the source of truth; this
/// just projects it (the Jellyfin-facing surface lands in M2).
/// </summary>
public static class LibraryEndpoints
{
    public static void MapLibraryEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/library").RequireAuthorization();

        group.MapGet("/", async (Guid? catalogId, MediaServerDbContext database, CancellationToken cancellationToken) =>
        {
            // Top-level browsable items: published movies and series (episodes/seasons nest under series).
            var query = database.MediaItems.AsNoTracking()
                .Where(item => item.PublicId != null && (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series));

            if (catalogId is { } id)
            {
                query = query.Where(item => item.CatalogId == id);
            }

            var items = await query.OrderBy(item => item.Title).ToListAsync(cancellationToken);
            var itemIds = items.Select(item => item.Id).ToList();

            var posters = await database.ImageAssets.AsNoTracking()
                .Where(image => itemIds.Contains(image.MediaItemId) && image.ImageType == ImageType.Primary)
                .GroupBy(image => image.MediaItemId)
                .Select(group => new { MediaItemId = group.Key, Url = group.OrderBy(image => image.SortOrder).Select(image => image.RemotePath).First() })
                .ToListAsync(cancellationToken);
            var posterByItem = posters.ToDictionary(poster => poster.MediaItemId, poster => poster.Url);

            var response = items.Select(item => new LibraryItemResponse(
                item.Id,
                item.PublicId,
                item.CatalogId,
                item.Kind.ToString(),
                item.Title,
                item.Year,
                item.LibraryPath,
                posterByItem.GetValueOrDefault(item.Id))).ToList();

            return Results.Ok(response);
        });
    }
}
