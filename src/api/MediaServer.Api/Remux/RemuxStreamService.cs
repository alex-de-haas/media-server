using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace MediaServer.Api.Remux;

/// <summary>What a client is handed when it asks for a repackaged source.</summary>
internal sealed record RemuxStream(
    Stream Content, string ContentType, EntityTagHeaderValue ETag, DateTimeOffset LastModified);

/// <summary>Why a source cannot be served repackaged, in terms a client can act on.</summary>
internal enum RemuxRefusal
{
    /// <summary>No such source, or one this viewer must not see.</summary>
    Unknown,

    /// <summary>The index has not been built yet. It is being built; coming back later works.</summary>
    NotIndexed,

    /// <summary>Indexed, but nothing in it can be described as an MP4 track.</summary>
    NotPackageable,
}

/// <summary>
/// Serves a media source as an MP4 computed over the untouched file.
///
/// Nothing is produced here and nothing is stored: the index was built in the background, the header is
/// computed per request in about a tenth of a second, and the media comes straight out of the source. The
/// only reason a request can fail is that the walk has not happened yet.
/// </summary>
public sealed class RemuxStreamService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    RemuxIndexStore store)
{
    internal async Task<(RemuxStream? Stream, RemuxRefusal Refusal)> OpenAsync(
        Guid mediaSourceId,
        int? audioStreamIndex,
        int? subtitleStreamIndex,
        VideoSignalling signalling,
        CancellationToken cancellationToken)
    {
        if (await ResolveAsync(mediaSourceId, cancellationToken) is not { } absolute)
        {
            return (null, RemuxRefusal.Unknown);
        }

        var index = store.Load(mediaSourceId, absolute);
        if (index is null)
        {
            return (null, RemuxRefusal.NotIndexed);
        }

        var chosen = RemuxTrackChoice.Resolve(index, audioStreamIndex, subtitleStreamIndex);
        if (chosen.Count == 0)
        {
            return (null, RemuxRefusal.NotPackageable);
        }

        var source = new FileStream(
            absolute, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);

        try
        {
            var built = Mp4Synthesizer.Build(index, chosen, signalling, source);
            if (built is null)
            {
                await source.DisposeAsync();
                return (null, RemuxRefusal.NotPackageable);
            }

            var file = new FileInfo(absolute);
            // The tag covers what the answer depends on: the file, the tracks chosen and the signalling
            // asked for. A viewer switching dub gets a different body, so it must get a different tag.
            var etag = new EntityTagHeaderValue(
                $"\"{mediaSourceId:N}-{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}"
                + $"-{string.Join('.', chosen)}-{signalling}\"");

            return (
                new RemuxStream(
                    new SynthesizedMp4Stream(built.Header, source),
                    "video/mp4",
                    etag,
                    file.LastWriteTimeUtc),
                RemuxRefusal.Unknown);
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The file behind a source, subject to the same rules as every other path on this surface:
    /// unpublished and tombstoned items resolve to nothing, and the sandbox confines a stored path back
    /// to its catalog root.
    /// </summary>
    private async Task<string?> ResolveAsync(Guid mediaSourceId, CancellationToken cancellationToken)
    {
        var row = await database.MediaSources.AsNoTracking()
            .Where(source => source.Id == mediaSourceId)
            .Join(database.MediaItems.AsNoTracking(), source => source.MediaItemId, item => item.Id,
                (source, item) => new { source.Path, item.CatalogId, item.PublicId, item.RemovedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null || row.PublicId is null || row.RemovedAt is not null || row.CatalogId is null)
        {
            return null;
        }

        var catalog = await database.Catalogs.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == row.CatalogId, cancellationToken);

        return catalog is not null && sandbox.TryResolve(catalog, row.Path, out var absolute)
            && File.Exists(absolute)
            ? absolute
            : null;
    }
}
