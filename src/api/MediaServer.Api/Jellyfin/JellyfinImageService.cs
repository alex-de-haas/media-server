using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Jellyfin;

public sealed record ImagePayload(byte[] Content, string ContentType, string Tag);

/// <summary>
/// Serves item artwork for the Jellyfin surface. Images are provider URLs (e.g. TMDb); the first request
/// fetches and caches the binary under the app data directory, subsequent requests serve the cached copy.
/// The client addresses images by item id + image type, never by path.
/// </summary>
public sealed class JellyfinImageService(
    MediaServerDbContext database,
    JellyfinCatalogArtwork catalogArtwork,
    JellyfinCollectionService collections,
    IHttpClientFactory httpFactory,
    HostyOptions hosty)
{
    public const string HttpClientName = "jellyfin-images";

    public async Task<ImagePayload?> GetImageAsync(
        string itemPublicId, ImageType type, string? tag, int index, CancellationToken cancellationToken)
    {
        var asset = await ResolveAssetAsync(itemPublicId, type, tag, index, cancellationToken);
        if (asset is null)
        {
            // Not a media item or catalog: it may be a BoxSet (collection), whose art is the collection's own
            // remote poster/backdrop rather than a stored ImageAsset.
            return await GetCollectionImageAsync(itemPublicId, type, cancellationToken);
        }

        if (asset.LocalPath is { Length: > 0 } cached && File.Exists(cached))
        {
            var bytes = await File.ReadAllBytesAsync(cached, cancellationToken);
            return new ImagePayload(bytes, ContentTypeFor(cached), asset.Tag);
        }

        return await FetchAndCacheAsync(asset, cancellationToken);
    }

    /// <summary>
    /// Resolves the artwork to serve. A media-item id selects one of its own images by tag/index; an id
    /// that is not a media item is treated as a catalog (collection folder), which has no images of its
    /// own and instead borrows the backdrop of its latest title regardless of the requested type.
    /// </summary>
    private async Task<ImageAsset?> ResolveAssetAsync(
        string itemPublicId, ImageType type, string? tag, int index, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.PublicId == itemPublicId, cancellationToken);
        if (item is null)
        {
            return await catalogArtwork.ResolveCatalogIdAsync(itemPublicId, cancellationToken) is { } catalogId
                ? await catalogArtwork.GetLatestBackdropAsync(catalogId, cancellationToken)
                : null;
        }

        var candidates = await database.ImageAssets
            .Where(image => image.MediaItemId == item.Id && image.ImageType == type)
            .OrderBy(image => image.SortOrder)
            .ToListAsync(cancellationToken);

        return tag is { Length: > 0 }
            ? candidates.FirstOrDefault(image => image.Tag == tag)
            : candidates.Skip(index).FirstOrDefault() ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// Serves a BoxSet's artwork: the collection's own remote poster/backdrop, fetched on first request and
    /// cached to disk under a deterministic name (a collection is not a media item, so there is no
    /// <see cref="ImageAsset"/> row to track). Null when the id is not a collection or it has no such art.
    /// </summary>
    private async Task<ImagePayload?> GetCollectionImageAsync(string itemPublicId, ImageType type, CancellationToken cancellationToken)
    {
        var collection = await collections.ResolveAsync(itemPublicId, cancellationToken);
        if (collection is null)
        {
            return null;
        }

        var remote = type == ImageType.Backdrop ? collection.BackdropUrl ?? collection.PosterUrl : collection.PosterUrl;
        if (string.IsNullOrEmpty(remote))
        {
            return null;
        }

        var tag = (type == ImageType.Backdrop ? JellyfinCollectionService.BackdropTag(collection) : JellyfinCollectionService.PrimaryTag(collection))
            ?? JellyfinCollectionService.PrimaryTag(collection)
            ?? string.Empty;

        var directory = Path.Combine(hosty.AppDataDir, "images");
        var slot = type == ImageType.Backdrop ? "backdrop" : "primary";
        var path = Path.Combine(directory, $"collection-{collection.Id:N}-{slot}{ExtensionFor(remote)}");
        if (File.Exists(path))
        {
            return new ImagePayload(await File.ReadAllBytesAsync(path, cancellationToken), ContentTypeFor(path), tag);
        }

        var client = httpFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(remote, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? ContentTypeFor(path);
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Serving still works even if the cache write fails.
        }

        return new ImagePayload(bytes, contentType, tag);
    }

    private static string ExtensionFor(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && Path.GetExtension(uri.AbsolutePath) is { Length: > 0 } extension)
        {
            return extension;
        }

        return ".jpg";
    }

    private async Task<ImagePayload?> FetchAndCacheAsync(ImageAsset asset, CancellationToken cancellationToken)
    {
        var client = httpFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(asset.RemotePath, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? ContentTypeFor(asset.RemotePath);

        try
        {
            var directory = Path.Combine(hosty.AppDataDir, "images");
            Directory.CreateDirectory(directory);
            var extension = ".jpg";
            if (Uri.TryCreate(asset.RemotePath, UriKind.Absolute, out var uri))
            {
                var parsed = Path.GetExtension(uri.AbsolutePath);
                if (!string.IsNullOrEmpty(parsed))
                {
                    extension = parsed;
                }
            }

            var path = Path.Combine(directory, asset.Tag + extension);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);

            var tracked = await database.ImageAssets.FirstOrDefaultAsync(image => image.Id == asset.Id, cancellationToken);
            if (tracked is not null)
            {
                tracked.LocalPath = path;
                await database.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Serving still works even if the cache write fails.
        }

        return new ImagePayload(bytes, contentType, asset.Tag);
    }

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };

    public static bool TryParseImageType(string value, out ImageType type) =>
        Enum.TryParse(value, ignoreCase: true, out type);
}
