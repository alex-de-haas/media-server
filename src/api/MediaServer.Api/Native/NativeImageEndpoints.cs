using MediaServer.Api.Data;
using MediaServer.Api.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace MediaServer.Api.Native;

/// <summary>
/// Artwork served from this instance rather than from the metadata provider's CDN.
///
/// The detail projection carries the provider's own URLs, which is what the web UI has always used.
/// A native client is pointed here instead for two reasons: a client on the same network as the server
/// keeps working with no internet at all, and browsing a library stops being visible to TMDb. The cost
/// is our bandwidth for something a CDN does well, which is why it is a deliberate choice rather than
/// an obvious one.
///
/// Reuses <see cref="JellyfinImageService"/>, so a first request for an image nobody has fetched yet
/// still fills the cache instead of 404ing.
/// </summary>
public static class NativeImageEndpoints
{
    public static void MapNativeImageEndpoints(this RouteGroupBuilder group)
    {
        // Bearer-authenticated, unlike the media routes: the client fetches these through its own
        // networking layer, where setting a header is trivial. Only AVPlayer's self-issued ranged
        // requests need a signed URL.
        group.MapGet("/items/{id:guid}/images/{imageType}", async (
            Guid id,
            string imageType,
            string? tag,
            int? index,
            MediaServerDbContext database,
            JellyfinImageService images,
            CancellationToken cancellationToken) =>
        {
            if (!JellyfinImageService.TryParseImageType(imageType, out var type))
            {
                return Results.NotFound();
            }

            // Resolve through the item's public id, and only for items that are actually visible: an
            // unpublished or tombstoned title must not keep serving artwork.
            var publicId = await database.MediaItems.AsNoTracking()
                .Where(item => item.Id == id && item.PublicId != null && item.RemovedAt == null)
                .Select(item => item.PublicId)
                .FirstOrDefaultAsync(cancellationToken);

            if (publicId is null)
            {
                return Results.NotFound();
            }

            // Always a real item here (the id came from MediaItems), so no acting user is needed: only the
            // synthetic Recommended view's tile is per-user, and this route cannot address it.
            var payload = await images.GetImageAsync(publicId, type, tag, index ?? 0, appUserId: null, cancellationToken);
            if (payload is null)
            {
                return Results.NotFound();
            }

            // The tag is a content hash, so it is a strong ETag and artwork can be cached hard: a new
            // image means a new tag, and the URL carries it.
            return Results.File(
                payload.Content,
                payload.ContentType,
                entityTag: new EntityTagHeaderValue($"\"{payload.Tag}\""));
        })
        .RequireAuthorization()
        .Produces(StatusCodes.Status200OK, contentType: "image/jpeg")
        .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// The artwork this instance actually holds for an item, as URLs into the route above. Absent
    /// types are null, so a client never requests one that cannot exist.
    /// </summary>
    public static async Task<NativeImagesDto> BuildAsync(
        MediaServerDbContext database, Guid itemId, CancellationToken cancellationToken)
    {
        var assets = await database.ImageAssets.AsNoTracking()
            .Where(image => image.MediaItemId == itemId)
            .Select(image => new { image.ImageType, image.Tag })
            .ToListAsync(cancellationToken);

        string? UrlFor(ImageType type)
        {
            var asset = assets.FirstOrDefault(candidate => candidate.ImageType == type);
            return asset is null
                ? null
                : $"{NativeEndpoints.RoutePrefix}/items/{itemId:D}/images/{type.ToString().ToLowerInvariant()}?tag={asset.Tag}";
        }

        return new NativeImagesDto(
            Primary: UrlFor(ImageType.Primary),
            Backdrop: UrlFor(ImageType.Backdrop),
            Logo: UrlFor(ImageType.Logo));
    }
}

/// <summary>
/// Artwork URLs served by this instance. A client prefers these over the provider URLs the shared
/// detail projection carries.
/// </summary>
public sealed record NativeImagesDto(string? Primary, string? Backdrop, string? Logo);
