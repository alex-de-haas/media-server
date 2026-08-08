using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Remux;

/// <summary>
/// Decides which sources want an index, builds them, and clears away the ones nothing points at any more.
///
/// The walk costs half a minute on a feature film, which is why nothing here runs on a playback request:
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
        new(StringComparer.OrdinalIgnoreCase) { "mkv", "webm" };

    /// <summary>Whether a container is one this can walk at all, which decides "not yet" from "never".</summary>
    internal static bool IsIndexable(string? container) => container is not null && Indexable.Contains(container);

    internal sealed record Candidate(Guid MediaSourceId, string AbsolutePath);

    /// <summary>
    /// Sources that ought to have an index and do not — visible, present on disk, and either never built
    /// or built against a file that has since changed.
    /// </summary>
    internal async Task<IReadOnlyList<Candidate>> PendingAsync(int limit, CancellationToken cancellationToken)
    {
        var pending = new List<Candidate>();
        foreach (var candidate in await IndexableAsync(cancellationToken))
        {
            if (store.IsCurrent(candidate.MediaSourceId, candidate.AbsolutePath))
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

            store.Save(candidate.MediaSourceId, candidate.AbsolutePath, index);
            logger.LogInformation(
                "Indexed {Path} in {Elapsed:F1}s: {Tracks} tracks, {Samples} samples.",
                candidate.AbsolutePath,
                TimeProvider.System.GetElapsedTime(started).TotalSeconds,
                index.Tracks.Count,
                index.Tracks.Sum(track => track.Samples.Count));
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
    /// Deletes index files whose media source is gone. Indexes live outside the database, so nothing else
    /// removes them when a title is deleted, and a library that churns would otherwise accumulate them.
    /// </summary>
    internal async Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        var live = await database.MediaSources.AsNoTracking()
            .Select(source => source.Id)
            .ToHashSetAsync(cancellationToken);

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

        var catalogs = await database.Catalogs.AsNoTracking().ToDictionaryAsync(catalog => catalog.Id, cancellationToken);
        var candidates = new List<Candidate>();

        foreach (var row in rows)
        {
            if (!Indexable.Contains(row.Container) ||
                !catalogs.TryGetValue(row.CatalogId!.Value, out var catalog) ||
                !sandbox.TryResolve(catalog, row.Path, out var absolute) ||
                !File.Exists(absolute))
            {
                continue;
            }

            candidates.Add(new Candidate(row.Id, absolute));
        }

        return candidates;
    }
}
