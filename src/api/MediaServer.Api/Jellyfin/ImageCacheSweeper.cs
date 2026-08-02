using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Jellyfin;

/// <summary>What one sweep pass looked at and reclaimed.</summary>
public sealed record ImageCacheSweepReport(int FilesScanned, int FilesDeleted, long BytesReclaimed);

/// <summary>
/// Reclaims artwork binaries the cache no longer has a use for. <see cref="JellyfinImageService"/> writes every
/// image under <c>{AppDataDir}/images</c>, but no delete path erased them: library delete, catalog delete, remap
/// and move-merge all drop the <see cref="ImageAsset"/> rows and leave the files behind, so the app data
/// directory only ever grew.
///
/// This runs as a periodic pass over the directory rather than inline in each of those paths, because:
/// <list type="bullet">
/// <item>installs that already leaked files need a pass that isn't tied to a delete happening;</item>
/// <item>the same <see cref="ImageAsset.Tag"/> — the provider's image hash — is shared by rows of different
/// items, so a file is dead only once the <em>last</em> referencing row is gone. That is a question about the
/// whole table, not about the rows being deleted;</item>
/// <item>a catalog purge deliberately never materializes its item ids (a catalog is unbounded), so it has no
/// tag list to work from;</item>
/// <item>collection artwork has no row at all, so no row deletion could ever reclaim a superseded poster.</item>
/// </list>
/// Reclaiming late is harmless and so is reclaiming too eagerly: a missing cache file is not an error state,
/// <see cref="JellyfinImageService"/> simply refetches it on the next request.
/// </summary>
public sealed class ImageCacheSweeper(
    MediaServerDbContext database,
    HostyOptions hosty,
    ILogger<ImageCacheSweeper> logger)
{
    /// <summary>
    /// Files written more recently than this are left alone. A first-time fetch commits its row before the
    /// binary lands and renames a temp file into place, so without a grace window a sweep running at the same
    /// moment could delete a download that is still in flight.
    /// </summary>
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    public async Task<ImageCacheSweepReport> SweepAsync(CancellationToken cancellationToken)
    {
        var directory = JellyfinImageService.CacheDirectory(hosty);
        if (!Directory.Exists(directory))
        {
            return new ImageCacheSweepReport(0, 0, 0);
        }

        var live = await LiveNamesAsync(cancellationToken);
        var cutoff = DateTime.UtcNow - MinimumAge;
        var scanned = 0;
        var deleted = 0;
        var reclaimed = 0L;

        foreach (var path in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;

            // Both naming schemes put the whole identity in the file name, so the name minus its extension is
            // what the live set is keyed by. A "{name}.{guid}.tmp" leftover from a failed write stems to
            // "{name}.{guid}", matches nothing, and is reclaimed as well — the age guard keeps live ones safe.
            if (live.Contains(Path.GetFileNameWithoutExtension(path)))
            {
                continue;
            }

            long size;
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.LastWriteTimeUtc > cutoff)
                {
                    continue;
                }

                size = file.Length;
                file.Delete();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A locked or already-deleted file is not worth failing the pass over; the next one retries.
                logger.LogDebug(exception, "Image cache sweep could not delete '{Path}'.", path);
                continue;
            }

            deleted++;
            reclaimed += size;
        }

        if (deleted > 0)
        {
            logger.LogInformation(
                "Image cache sweep reclaimed {Count} orphaned file(s) ({Bytes} bytes) of {Scanned} scanned.",
                deleted, reclaimed, scanned);
        }

        return new ImageCacheSweepReport(scanned, deleted, reclaimed);
    }

    /// <summary>The cache file names (extension aside) that something in the database still points at.</summary>
    private async Task<HashSet<string>> LiveNamesAsync(CancellationToken cancellationToken)
    {
        // Item artwork caches as "{Tag}{extension}", so the file name is the tag. Querying the distinct tags
        // answers "does any row still reference this file" for shared tags too, without loading the table.
        var tags = await database.ImageAssets.AsNoTracking()
            .Select(image => image.Tag)
            .Distinct()
            .ToListAsync(cancellationToken);

        // Case-insensitive: tags are lowercase hex, but the cache also lives on case-insensitive filesystems.
        var live = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);

        // Collection artwork has no ImageAsset row, so its live names are recomputed from the collections.
        var collections = await database.MovieCollections.AsNoTracking()
            .Where(collection => collection.PosterUrl != null || collection.BackdropUrl != null)
            .ToListAsync(cancellationToken);
        foreach (var name in collections.SelectMany(JellyfinImageService.CollectionCacheNames))
        {
            live.Add(name);
        }

        // Person photos have no row either. Unlike collections there are thousands of them, so this loads
        // only the columns the name is derived from.
        var people = await database.Persons.AsNoTracking()
            .Where(person => person.ProfileUrl != null)
            .Select(person => new { person.Id, person.ProfileUrl })
            .ToListAsync(cancellationToken);
        foreach (var person in people)
        {
            foreach (var name in JellyfinImageService.PersonCacheNames(person.Id, person.ProfileUrl))
            {
                live.Add(name);
            }
        }

        return live;
    }
}

/// <summary>Runs <see cref="ImageCacheSweeper.SweepAsync"/> on a timer so the artwork cache stays bounded.</summary>
public sealed class ImageCacheSweepWorker(IServiceScopeFactory scopeFactory, ILogger<ImageCacheSweepWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    /// <summary>Offset from <see cref="Library.LibraryScanWorker"/>'s delay so the housekeeping passes don't collide.</summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            await RunOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var sweeper = scope.ServiceProvider.GetRequiredService<ImageCacheSweeper>();
            await sweeper.SweepAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Scheduled image cache sweep failed.");
        }
    }
}
