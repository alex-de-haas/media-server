using MediaServer.Api.Catalogs;
using Microsoft.Net.Http.Headers;

namespace MediaServer.Api.Jellyfin.Streaming;

public sealed record ResolvedStream(
    string AbsolutePath,
    string ContentType,
    EntityTagHeaderValue ETag,
    DateTimeOffset LastModified,
    long Length);

/// <summary>
/// Resolves a published item + media source to a concrete on-disk file for Direct Play, re-validating
/// that the file is confined to the catalog root (no traversal/symlink escape). Streaming addresses
/// media by item id, never by client-supplied path, so authorization cannot be bypassed.
/// </summary>
public sealed class JellyfinStreamResolver(JellyfinLibraryService library, ICatalogPathSandbox sandbox)
{
    public async Task<ResolvedStream?> ResolveAsync(string itemPublicId, string? mediaSourceId, CancellationToken cancellationToken)
    {
        var resolved = await library.ResolvePlayableAsync(itemPublicId, mediaSourceId, cancellationToken);
        if (resolved is null)
        {
            return null;
        }

        var (_, source, catalog) = resolved.Value;

        // MediaSource.Path is catalog-relative; the sandbox confines it back to the root.
        if (!sandbox.TryResolve(catalog, source.Path, out var absolute) || !File.Exists(absolute))
        {
            return null;
        }

        var info = new FileInfo(absolute);
        var contentType = DirectPlay.ContentType(Path.GetExtension(absolute));
        var etag = new EntityTagHeaderValue($"\"{source.Id:N}-{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        return new ResolvedStream(absolute, contentType, etag, info.LastWriteTimeUtc, info.Length);
    }
}

/// <summary>
/// Builds the file result for a resolved stream. The framework's file result performs the range/
/// conditional handling (<c>206</c>, <c>Range</c>/<c>If-Range</c>, <c>HEAD</c>, <c>Accept-Ranges</c>,
/// <c>Content-Range</c>/<c>Content-Length</c>) and disposes the stream — no whole-file buffering.
/// </summary>
public static class JellyfinStreamResults
{
    public static IResult File(ResolvedStream resolved)
    {
        var stream = new FileStream(
            resolved.AbsolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return Results.File(
            stream,
            contentType: resolved.ContentType,
            lastModified: resolved.LastModified,
            entityTag: resolved.ETag,
            enableRangeProcessing: true);
    }
}
