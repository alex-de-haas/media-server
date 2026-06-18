using MediaServer.Api.Data;
using MediaServer.Api.Jellyfin.Streaming;
using Microsoft.Net.Http.Headers;

namespace MediaServer.Api.Jellyfin.Endpoints;

/// <summary>Artwork, playback negotiation, and direct (range) streaming.</summary>
internal static class JellyfinMediaEndpoints
{
    public static void MapJellyfinMediaEndpoints(this IEndpointRouteBuilder routes)
    {
        var secured = routes.MapGroup(string.Empty).RequireJellyfin();

        // ---- Artwork ----
        secured.MapMethods("/Items/{itemId}/Images/{imageType}", ["GET", "HEAD"], ServeImageAsync);
        secured.MapMethods("/Items/{itemId}/Images/{imageType}/{imageIndex:int}", ["GET", "HEAD"], ServeImageAsync);

        // ---- Playback negotiation ----
        secured.MapGet("/Items/{itemId}/PlaybackInfo", (string itemId, HttpRequest request, JellyfinLibraryService library, CancellationToken cancellationToken) =>
            BuildPlaybackInfoAsync(itemId, request.Query["MediaSourceId"], library, cancellationToken));

        secured.MapPost("/Items/{itemId}/PlaybackInfo", (string itemId, PlaybackInfoRequest? body, HttpRequest request, JellyfinLibraryService library, CancellationToken cancellationToken) =>
            BuildPlaybackInfoAsync(itemId, body?.MediaSourceId ?? request.Query["MediaSourceId"], library, cancellationToken));

        // ---- Direct streaming ----
        secured.MapMethods("/Videos/{itemId}/stream", ["GET", "HEAD"], StreamAsync);
        secured.MapMethods("/Videos/{itemId}/stream.{container}", ["GET", "HEAD"], StreamAsync);
    }

    private static async Task<IResult> ServeImageAsync(
        HttpRequest request, string itemId, string imageType, int? imageIndex, JellyfinImageService images, CancellationToken cancellationToken)
    {
        if (!JellyfinImageService.TryParseImageType(imageType, out var type))
        {
            return Results.NotFound();
        }

        var tag = request.Query["tag"].ToString();
        var payload = await images.GetImageAsync(
            itemId, type, string.IsNullOrEmpty(tag) ? null : tag, imageIndex ?? 0, cancellationToken);
        if (payload is null)
        {
            return Results.NotFound();
        }

        request.HttpContext.Response.Headers.CacheControl = "public, max-age=86400";
        return Results.File(
            payload.Content,
            contentType: payload.ContentType,
            entityTag: new EntityTagHeaderValue($"\"{payload.Tag}\""),
            enableRangeProcessing: false);
    }

    private static async Task<IResult> BuildPlaybackInfoAsync(
        string itemId, string? mediaSourceId, JellyfinLibraryService library, CancellationToken cancellationToken)
    {
        var item = await library.GetItemAsync(itemId, includeMediaSources: true, cancellationToken);
        var sources = item?.MediaSources ?? [];

        if (!string.IsNullOrEmpty(mediaSourceId))
        {
            sources = sources.Where(source => source.Id == mediaSourceId).ToList();
        }

        var playSessionId = Guid.NewGuid().ToString("N");
        var error = sources.Count == 0 ? "NoCompatibleStream" : null;
        return JellyfinJson.Ok(new PlaybackInfoResponse(sources, playSessionId, error));
    }

    private static async Task<IResult> StreamAsync(
        string itemId, HttpRequest request, JellyfinStreamResolver resolver, CancellationToken cancellationToken)
    {
        var resolved = await resolver.ResolveAsync(itemId, request.Query["MediaSourceId"], cancellationToken);
        return resolved is null ? Results.NotFound() : JellyfinStreamResults.File(resolved);
    }
}
