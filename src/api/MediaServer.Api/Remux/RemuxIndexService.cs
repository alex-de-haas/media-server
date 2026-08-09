using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Remux;

/// <summary>
/// Decides which sources want an index, builds them, and clears away the ones nothing points at any more.
///
/// The walk gets through about 105 MB of source per second off the spinning disk in production — roughly
/// three minutes for a 20 GB film. That is a traversal rate over the file's length, not a device
/// benchmark: payloads are seeked past, and the bytes actually read are not counted. Either way it is far
/// too long to run on a playback request:
/// <see cref="RemuxIndexWorker"/> drives it in the background, and a viewer who presses play either finds an
/// index waiting or is told the source is not ready yet.
/// </summary>
public sealed class RemuxIndexService(
    MediaServerDbContext database,
    ICatalogPathSandbox sandbox,
    RemuxIndexStore store,
    ILogger<RemuxIndexService> logger)
{
    /// <summary>
    /// Containers whose samples an MP4 can reference. Only Matroska for now: an MP4 source is already
    /// playable as it stands and has nothing to gain, and the rest of the list would each need their own
    /// walker.
    /// </summary>
    private static readonly HashSet<string> Indexable =
        new(StringComparer.OrdinalIgnoreCase) { "mkv", "webm", "mka" };

    /// <summary>Whether a container is one this can walk at all, which decides "not yet" from "never".</summary>
    internal static bool IsIndexable(string? container) => container is not null && Indexable.Contains(container);

    /// <summary>
    /// Something that wants an index, keyed by whatever owns it: a media source, or the stream row of a
    /// sidecar file. Both are Guids and the store does not care which, so an external dub is indexed the
    /// same way its video is.
    /// </summary>
    internal sealed record Candidate(Guid Key, string AbsolutePath);

    /// <summary>
    /// Sources that ought to have an index and do not — visible, present on disk, and either never built
    /// or built against a file that has since changed.
    /// </summary>
    internal async Task<IReadOnlyList<Candidate>> PendingAsync(int limit, CancellationToken cancellationToken)
    {
        var pending = new List<Candidate>();
        foreach (var candidate in await IndexableAsync(cancellationToken))
        {
            if (store.IsCurrent(candidate.Key, candidate.AbsolutePath))
            {
                continue;
            }

            pending.Add(candidate);
            if (pending.Count >= limit)
            {
                break;
            }
        }

        return pending;
    }

    /// <summary>Walks one source and stores the result. Returns false when there is nothing to walk.</summary>
    internal async Task<bool> BuildAsync(Candidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            var started = TimeProvider.System.GetTimestamp();
            MatroskaIndex index;
            await using (var stream = new FileStream(
                candidate.AbsolutePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, useAsync: false))
            {
                index = await Task.Run(() => MatroskaIndexer.Build(stream, cancellationToken), cancellationToken);
            }

            if (index.Tracks.Count == 0)
            {
                logger.LogDebug("No tracks in {Path}; not indexing it.", candidate.AbsolutePath);
                return false;
            }

            store.Save(candidate.Key, candidate.AbsolutePath, index);

            // Everything needed to answer the two questions this log exists for: whether the walk is bound
            // by the disk it reads (bytes and rate, which the elapsed time alone could not say), and which
            // codecs are being passed over (named, so the next reader does not have to infer them from a
            // sample count the way the first one had to).
            var elapsed = TimeProvider.System.GetElapsedTime(started);
            var walked = index.Tracks.Where(RemuxCodecs.WantsSamples).ToList();
            logger.LogInformation(
                "Indexed {Path} in {Elapsed:F1}s: {Bytes} bytes at {Rate:F0} MB/s, "
                + "{Walked}/{Tracks} tracks, {Samples} samples; skipped {Skipped}.",
                candidate.AbsolutePath,
                elapsed.TotalSeconds,
                index.SourceLength,
                index.SourceLength / Math.Max(0.001, elapsed.TotalSeconds) / (1024 * 1024),
                walked.Count,
                index.Tracks.Count,
                walked.Sum(track => track.Samples.Count),
                Skipped(index));
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A file that vanished or is being written to is a normal thing to meet mid-scan; the next
            // pass picks it up.
            logger.LogDebug(exception, "Could not index {Path}.", candidate.AbsolutePath);
            return false;
        }
    }

    /// <summary>
    /// The codecs whose samples were passed over, counted by codec — <c>A_TRUEHD x1, A_DTS x3</c>. This is
    /// the difference between knowing a file has fifty tracks and knowing what they are.
    /// </summary>
    private static string Skipped(MatroskaIndex index)
    {
        var groups = index.Tracks
            .Where(track => !RemuxCodecs.WantsSamples(track))
            .GroupBy(track => string.IsNullOrEmpty(track.CodecId) ? "unnamed" : track.CodecId)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key} x{group.Count()}")
            .ToList();

        return groups.Count > 0 ? string.Join(", ", groups) : "nothing";
    }

    /// <summary>
    /// Deletes index files whose media source is gone. Indexes live outside the database, so nothing else
    /// removes them when a title is deleted, and a library that churns would otherwise accumulate them.
    /// </summary>
    internal async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        // Both a source and a sidecar stream can own an index, so both keep theirs alive.
        var live = await database.MediaSources.AsNoTracking()
            .Select(source => source.Id)
            .ToHashSetAsync(cancellationToken);
        foreach (var streamId in await database.MediaStreams.AsNoTracking()
                     .Where(stream => stream.IsExternal)
                     .Select(stream => stream.Id)
                     .ToListAsync(cancellationToken))
        {
            live.Add(streamId);
        }

        var removed = 0;
        foreach (var stored in store.Stored())
        {
            if (live.Contains(stored))
            {
                continue;
            }

            store.Delete(stored);
            removed++;
        }

        if (removed > 0)
        {
            logger.LogInformation("Removed {Count} orphaned remux indexes.", removed);
        }

        return removed;
    }

    private async Task<List<Candidate>> IndexableAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<Candidate>();
        var catalogs = await database.Catalogs.AsNoTracking()
            .ToDictionaryAsync(catalog => catalog.Id, cancellationToken);

        // Unpublished and tombstoned items are invisible everywhere else on the native surface, and an
        // index for one would be work nobody can reach.
        var rows = await database.MediaSources.AsNoTracking()
            .Join(database.MediaItems.AsNoTracking(), source => source.MediaItemId, item => item.Id,
                (source, item) => new
                {
                    source.Id,
                    source.Container,
                    source.Path,
                    item.CatalogId,
                    item.PublicId,
                    item.RemovedAt,
                })
            .Where(row => row.PublicId != null && row.RemovedAt == null && row.CatalogId != null)
            .ToListAsync(cancellationToken);

        var visible = new HashSet<Guid>();
        foreach (var row in rows)
        {
            visible.Add(row.Id);
            if (!Indexable.Contains(row.Container) ||
                !catalogs.TryGetValue(row.CatalogId!.Value, out var catalog) ||
                !sandbox.TryResolve(catalog, row.Path, out var absolute) ||
                !File.Exists(absolute))
            {
                continue;
            }

            candidates.Add(new Candidate(row.Id, absolute));
        }

        // Sidecar dubs. An external audio track is a second Matroska file, and playing one means
        // referencing its samples beside the video's — which needs an index of its own, built here rather
        // than on the request that wants it.
        var catalogById = rows
            .Where(row => row.CatalogId is not null)
            .ToDictionary(row => row.Id, row => row.CatalogId!.Value);

        var sidecars = await database.MediaStreams.AsNoTracking()
            .Where(stream => stream.IsExternal
                && stream.StreamType == StreamType.Audio
                && stream.ExternalPath != null)
            .Select(stream => new { stream.Id, stream.MediaSourceId, stream.ExternalPath })
            .ToListAsync(cancellationToken);

        foreach (var sidecar in sidecars)
        {
            if (!visible.Contains(sidecar.MediaSourceId)
                || !catalogById.TryGetValue(sidecar.MediaSourceId, out var catalogId)
                || !catalogs.TryGetValue(catalogId, out var catalog)
                || !IsIndexable(Path.GetExtension(sidecar.ExternalPath).TrimStart('.'))
                || !sandbox.TryResolve(catalog, sidecar.ExternalPath!, out var absolute)
                || !File.Exists(absolute))
            {
                continue;
            }

            candidates.Add(new Candidate(sidecar.Id, absolute));
        }

        return candidates;
    }
}
