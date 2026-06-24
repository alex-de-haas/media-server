using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Collections;

/// <summary>How many movies the backfill looked at and how many were linked to a collection.</summary>
public sealed record CollectionBackfillReport(int MoviesProcessed, int MoviesLinked);

/// <summary>
/// One-off, idempotent population of <see cref="MediaItem.CollectionId"/> / <see cref="MovieCollection"/> from
/// the <c>belongs_to_collection</c> already cached in <see cref="MetadataRecord.Raw"/>. Only movies without a
/// link yet are looked at; those genuinely in no collection stay unlinked (a cheap re-parse, no writes), and
/// new/re-fetched movies are handled live by the enrich pipeline via <see cref="CollectionSyncService"/>.
/// </summary>
public sealed class CollectionBackfillService(MediaServerDbContext database, CollectionSyncService sync, ILogger<CollectionBackfillService> logger)
{
    public async Task<CollectionBackfillReport> BackfillAsync(CancellationToken cancellationToken)
    {
        // Movies not yet linked to a collection (a link is only ever set, never cleared, by a real franchise).
        var unlinkedMovieIds = database.MediaItems
            .Where(item => item.Kind == MediaKind.Movie && item.CollectionId == null)
            .Select(item => item.Id);

        // One query for every candidate record (no N+1); group by movie and parse each Raw once in memory.
        var records = await database.MetadataRecords
            .Where(record => record.Raw != null && unlinkedMovieIds.Contains(record.MediaItemId))
            .Select(record => new { record.MediaItemId, record.Provider, record.Raw })
            .ToListAsync(cancellationToken);

        var moviesProcessed = 0;
        var moviesLinked = 0;
        foreach (var group in records.GroupBy(record => record.MediaItemId))
        {
            moviesProcessed++;
            // belongs_to_collection is language-independent, so any cached record will do; prefer one whose
            // Raw actually carries the collection object.
            var chosen = group.FirstOrDefault(record => CollectionMetadata.Parse(record.Raw) is not null) ?? group.First();
            if (await sync.SyncAsync(group.Key, chosen.Provider, chosen.Raw, cancellationToken))
            {
                moviesLinked++;
            }
        }

        if (moviesLinked > 0)
        {
            logger.LogInformation(
                "Collections backfill: processed {Movies} movie(s), linked {Linked} to a collection.", moviesProcessed, moviesLinked);
        }

        return new CollectionBackfillReport(moviesProcessed, moviesLinked);
    }
}

/// <summary>Runs <see cref="CollectionBackfillService.BackfillAsync"/> once on startup to populate existing movies.</summary>
public sealed class CollectionBackfillWorker(IServiceScopeFactory scopeFactory, ILogger<CollectionBackfillWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Let migrations and the rest of startup settle before a one-shot bulk pass over cached metadata.
            await Task.Delay(InitialDelay, stoppingToken);
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<CollectionBackfillService>();
            await service.BackfillAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Collections backfill failed; it will retry on the next start.");
        }
    }
}
