using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Jellyfin.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace MediaServer.Api.Native;

/// <summary>
/// Turns a media source or one of its sidecar tracks into a file on disk, confined to the catalog
/// root. Mirrors <see cref="JellyfinStreamResolver"/> for the native surface; kept out of the endpoint
/// so the rules — visibility, externality, containment — can be asserted directly.
/// </summary>
public sealed class NativeMediaResolver(MediaServerDbContext database, ICatalogPathSandbox sandbox)
{
    /// <summary>The playable file behind a media source, or null when it must not be served.</summary>
    public async Task<ResolvedStream?> ResolveSourceAsync(Guid mediaSourceId, CancellationToken cancellationToken)
    {
        var row = await database.MediaSources.AsNoTracking()
            .Where(source => source.Id == mediaSourceId)
            .Join(database.MediaItems.AsNoTracking(), source => source.MediaItemId, item => item.Id,
                (source, item) => new { source.Path, item.CatalogId, item.PublicId, item.RemovedAt })
            .FirstOrDefaultAsync(cancellationToken);

        // Unpublished and tombstoned items are unreachable everywhere else on this surface, and a
        // signed URL must not outlive the item's visibility either.
        if (row is null || row.PublicId is null || row.RemovedAt is not null)
        {
            return null;
        }

        return await ResolveFileAsync(row.CatalogId, row.Path, mediaSourceId, cancellationToken);
    }

    /// <summary>
    /// A sidecar track of that source: an external dub or subtitle beside the video. Refuses an
    /// embedded track — those live inside the container and have no file of their own to serve.
    /// </summary>
    public async Task<ResolvedStream?> ResolveSidecarAsync(
        Guid mediaSourceId, Guid streamId, CancellationToken cancellationToken)
    {
        var stream = await database.MediaStreams.AsNoTracking()
            .Where(candidate => candidate.Id == streamId && candidate.MediaSourceId == mediaSourceId)
            .Select(candidate => new { candidate.IsExternal, candidate.ExternalPath })
            .FirstOrDefaultAsync(cancellationToken);

        if (stream is null || !stream.IsExternal || string.IsNullOrWhiteSpace(stream.ExternalPath))
        {
            return null;
        }

        var owner = await database.MediaSources.AsNoTracking()
            .Where(source => source.Id == mediaSourceId)
            .Join(database.MediaItems.AsNoTracking(), source => source.MediaItemId, item => item.Id,
                (source, item) => new { item.CatalogId, item.PublicId, item.RemovedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (owner is null || owner.PublicId is null || owner.RemovedAt is not null)
        {
            return null;
        }

        return await ResolveFileAsync(owner.CatalogId, stream.ExternalPath, streamId, cancellationToken);
    }

    private async Task<ResolvedStream?> ResolveFileAsync(
        Guid? catalogId, string relativePath, Guid etagSeed, CancellationToken cancellationToken)
    {
        if (catalogId is not { } id)
        {
            return null;
        }

        var catalog = await database.Catalogs.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id, cancellationToken);

        // The stored path is catalog-relative; the sandbox is what confines it back to the root, so a
        // row pointing outside resolves to nothing rather than to a file.
        if (catalog is null || !sandbox.TryResolve(catalog, relativePath, out var absolute) || !File.Exists(absolute))
        {
            return null;
        }

        var info = new FileInfo(absolute);
        var etag = new EntityTagHeaderValue($"\"{etagSeed:N}-{info.Length:x}-{info.LastWriteTimeUtc.Ticks:x}\"");
        return new ResolvedStream(
            absolute,
            NativeContentTypes.For(Path.GetExtension(absolute)),
            etag,
            info.LastWriteTimeUtc,
            info.Length);
    }
}
