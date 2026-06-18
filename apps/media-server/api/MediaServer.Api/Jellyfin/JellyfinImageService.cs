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
public sealed class JellyfinImageService(MediaServerDbContext database, IHttpClientFactory httpFactory, HostyOptions hosty)
{
    public const string HttpClientName = "jellyfin-images";

    public async Task<ImagePayload?> GetImageAsync(
        string itemPublicId, ImageType type, string? tag, int index, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.PublicId == itemPublicId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var candidates = await database.ImageAssets
            .Where(image => image.MediaItemId == item.Id && image.ImageType == type)
            .OrderBy(image => image.SortOrder)
            .ToListAsync(cancellationToken);

        var asset = tag is { Length: > 0 }
            ? candidates.FirstOrDefault(image => image.Tag == tag)
            : candidates.Skip(index).FirstOrDefault() ?? candidates.FirstOrDefault();
        if (asset is null)
        {
            return null;
        }

        if (asset.LocalPath is { Length: > 0 } cached && File.Exists(cached))
        {
            var bytes = await File.ReadAllBytesAsync(cached, cancellationToken);
            return new ImagePayload(bytes, ContentTypeFor(cached), asset.Tag);
        }

        return await FetchAndCacheAsync(asset, cancellationToken);
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
