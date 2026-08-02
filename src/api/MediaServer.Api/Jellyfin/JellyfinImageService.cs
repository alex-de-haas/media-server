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
    JellyfinPersonService people,
    IHttpClientFactory httpFactory,
    HostyOptions hosty)
{
    public const string HttpClientName = "jellyfin-images";

    /// <summary>The cache directory holding every artwork binary, item and collection alike.</summary>
    public static string CacheDirectory(HostyOptions hosty) => Path.Combine(hosty.AppDataDir, "images");

    private const string PrimarySlot = "primary";
    private const string BackdropSlot = "backdrop";

    public async Task<ImagePayload?> GetImageAsync(
        string itemPublicId, ImageType type, string? tag, int index, CancellationToken cancellationToken)
    {
        var asset = await ResolveAssetAsync(itemPublicId, type, tag, index, cancellationToken);
        if (asset is null)
        {
            // Not a media item or catalog: it may be a BoxSet (collection), whose art is the collection's own
            // remote poster/backdrop rather than a stored ImageAsset — or a person, whose photo is likewise
            // a remote URL with no ImageAsset row.
            return await GetCollectionImageAsync(itemPublicId, type, cancellationToken)
                ?? await GetPersonImageAsync(itemPublicId, type, cancellationToken);
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

        var slot = type == ImageType.Backdrop ? BackdropSlot : PrimarySlot;
        return await ServeRemoteAsync(remote, CollectionCacheName(collection.Id, slot, tag), tag, cancellationToken);
    }

    /// <summary>
    /// Serves a person's profile photo. Like collection artwork it is a remote provider URL with no
    /// <see cref="ImageAsset"/> row behind it, so it caches under its own deterministic name. A person has
    /// exactly one image — a request for anything but <see cref="ImageType.Primary"/> is answered with
    /// nothing rather than with the portrait in the wrong slot.
    /// </summary>
    private async Task<ImagePayload?> GetPersonImageAsync(string itemPublicId, ImageType type, CancellationToken cancellationToken)
    {
        if (type != ImageType.Primary)
        {
            return null;
        }

        var person = await people.ResolveAsync(itemPublicId, cancellationToken);
        if (person?.ProfileUrl is not { Length: > 0 } remote)
        {
            return null;
        }

        var tag = JellyfinPersonService.PrimaryTag(person) ?? string.Empty;
        return await ServeRemoteAsync(remote, PersonCacheName(person.Id, tag), tag, cancellationToken);
    }

    /// <summary>
    /// Serves a remote image that has no <see cref="ImageAsset"/> row to track it — collection artwork and
    /// person photos — from a cache file named after its identity, fetching it on the first request.
    /// </summary>
    private async Task<ImagePayload?> ServeRemoteAsync(
        string remote, string cacheName, string tag, CancellationToken cancellationToken)
    {
        var directory = CacheDirectory(hosty);
        var path = Path.Combine(directory, cacheName + ExtensionFor(remote));
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
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            // Write to a sibling temp file then atomically rename, so a concurrent request never reads a
            // half-written cache file.
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Serving still works even if the cache write fails; clean up a stray temp file.
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Best-effort.
            }
        }

        return new ImagePayload(bytes, contentType, tag);
    }

    /// <summary>
    /// The cache file name (extension aside) of one collection artwork slot. A collection is not a media item,
    /// so no <see cref="ImageAsset"/> row records what is on disk — the name carries it instead. The tag is part
    /// of it so a swapped poster/backdrop (new url → new tag) lands in a new file and is refetched, instead of
    /// the stale cached bytes being served forever; the superseded file is then reclaimed by
    /// <see cref="ImageCacheSweeper"/>, which recomputes the live names with <see cref="CollectionCacheNames"/>.
    /// </summary>
    private static string CollectionCacheName(Guid collectionId, string slot, string tag) =>
        $"collection-{collectionId:N}-{slot}-{tag}";

    /// <summary>
    /// Every cache file name <see cref="GetCollectionImageAsync"/> can currently write for a collection. This
    /// mirrors that method's <c>BackdropUrl ?? PosterUrl</c> fallback exactly: naming a file it would not write
    /// would pin a superseded binary as live and leak it forever.
    /// </summary>
    public static IEnumerable<string> CollectionCacheNames(MovieCollection collection)
    {
        var backdrop = JellyfinCollectionService.BackdropTag(collection);
        if (backdrop is not null)
        {
            yield return CollectionCacheName(collection.Id, BackdropSlot, backdrop);
        }

        if (JellyfinCollectionService.PrimaryTag(collection) is { } primary)
        {
            yield return CollectionCacheName(collection.Id, PrimarySlot, primary);

            // Only a collection with no backdrop of its own falls back to serving the poster in the backdrop
            // slot — under the poster's tag, so that request caches to its own file rather than reusing the
            // primary one. Once the collection gains a real backdrop this name goes dead and is reclaimed.
            if (backdrop is null)
            {
                yield return CollectionCacheName(collection.Id, BackdropSlot, primary);
            }
        }
    }

    /// <summary>
    /// The cache file name (extension aside) of a person's profile photo. Same reasoning as
    /// <see cref="CollectionCacheName"/>: no row records what is on disk, and the tag in the name makes a
    /// replaced photo land in a new file instead of serving stale bytes forever.
    /// </summary>
    private static string PersonCacheName(Guid personId, string tag) => $"person-{personId:N}-{tag}";

    /// <summary>
    /// The cache file name <see cref="GetPersonImageAsync"/> writes for a person, or nothing when the
    /// provider has no photo. <see cref="ImageCacheSweeper"/> deletes every file it cannot name as live,
    /// so a person photo missing from here would be reclaimed on the next pass and refetched forever.
    /// </summary>
    public static IEnumerable<string> PersonCacheNames(Guid personId, string? profileUrl)
    {
        if (JellyfinPersonService.PrimaryTag(personId, profileUrl) is { } tag)
        {
            yield return PersonCacheName(personId, tag);
        }
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
            var directory = CacheDirectory(hosty);
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
