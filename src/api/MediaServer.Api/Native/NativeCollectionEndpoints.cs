using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MediaServer.Api.Collections;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Library;
using Microsoft.Net.Http.Headers;

namespace MediaServer.Api.Native;

/// <summary>Authenticated collection browsing with artwork served by this instance.</summary>
public static class NativeCollectionEndpoints
{
    /// <summary>Maps collection reads onto the shared franchise read model.</summary>
    public static void MapNativeCollectionEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/collections", async (CollectionReadService collections, CancellationToken ct) =>
            Results.Ok((await collections.ListAsync(ct)).Select(Project).ToArray()))
            .WithName("ListNativeCollections").RequireAuthorization()
            .Produces<NativeCollectionSummary[]>();

        group.MapGet("/collections/{id:guid}", async (
            Guid id, ClaimsPrincipal principal, MediaServerDbContext database,
            CollectionReadService collections, CancellationToken ct) =>
        {
            if (await principal.ResolveAppUserIdAsync(database, ct) is not { } userId)
                return Results.Unauthorized();
            var detail = await collections.GetAsync(id, userId, ct);
            return detail is null || detail.Items.Count < CollectionMetadata.MinOwnedMovies
                ? Results.NotFound() : Results.Ok(Project(detail));
        }).WithName("GetNativeCollection").RequireAuthorization()
          .Produces<NativeCollectionDetail>().Produces(StatusCodes.Status404NotFound);

        group.MapGet("/collections/{id:guid}/images/{imageType}", async (
            Guid id, string imageType, CollectionReadService collections,
            JellyfinImageService images, CancellationToken ct) =>
        {
            if (!JellyfinImageService.TryParseImageType(imageType, out var type)
                || type is not (ImageType.Primary or ImageType.Backdrop))
                return Results.NotFound();
            var detail = await collections.GetAsync(id, null, ct);
            if (detail is null || detail.Items.Count < CollectionMetadata.MinOwnedMovies)
                return Results.NotFound();

            // Prefer the franchise's cached artwork. An artless franchise can borrow a member poster.
            var payload = await images.GetImageAsync(JellyfinIds.Collection(id), type, null, 0, null, ct);
            if (payload is null && type == ImageType.Primary)
            {
                var member = detail.Items.FirstOrDefault(item => item.PosterUrl != null);
                if (member?.PublicId is { } publicId)
                    payload = await images.GetImageAsync(publicId, type, null, 0, null, ct);
            }
            return payload is null ? Results.NotFound() : Results.File(payload.Content, payload.ContentType,
                entityTag: new EntityTagHeaderValue($"\"{payload.Tag}\""));
        }).WithName("GetNativeCollectionImage").RequireAuthorization()
          .Produces<Stream>(StatusCodes.Status200OK, contentType: "image/jpeg",
              additionalContentTypes: ["image/png", "image/webp", "image/gif"])
          .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>Replaces provider artwork URLs with versioned native URLs.</summary>
    public static NativeCollectionSummary Project(CollectionSummaryDto collection) =>
        new(collection.Id, collection.Name, ImageUrl(collection.Id, "primary", collection.PosterUrl), collection.ItemCount);

    /// <summary>Keeps member IDs and user data while removing provider artwork URLs.</summary>
    public static NativeCollectionDetail Project(CollectionDetailDto collection) =>
        new(collection.Id, collection.Name,
            ImageUrl(collection.Id, "primary", collection.PosterUrl),
            ImageUrl(collection.Id, "backdrop", collection.BackdropUrl),
            collection.Items.Select(item => item with
            {
                PosterUrl = item.PosterUrl is null ? null : $"{NativeEndpoints.RoutePrefix}/items/{item.Id:D}/images/primary"
            }).ToArray());

    private static string? ImageUrl(Guid id, string type, string? source) => source is null ? null
        : $"{NativeEndpoints.RoutePrefix}/collections/{id:D}/images/{type}?tag="
          + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
}

/// <summary>A franchise tile using native image URLs.</summary>
public sealed record NativeCollectionSummary(Guid Id, string Name, string? PosterUrl, int ItemCount);

/// <summary>A franchise and its owned movies in release order.</summary>
public sealed record NativeCollectionDetail(
    Guid Id, string Name, string? PosterUrl, string? BackdropUrl, IReadOnlyList<LibraryItemDto> Items);
