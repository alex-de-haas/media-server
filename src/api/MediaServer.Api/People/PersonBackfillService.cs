using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.People;

/// <summary>How many items the backfill looked at and how many credits it wrote.</summary>
public sealed record PersonBackfillReport(int ItemsProcessed, int CreditsWritten);

/// <summary>
/// One-off, idempotent population of <see cref="Person"/>/<see cref="MediaItemPerson"/> from the credits
/// already cached in <see cref="MetadataRecord.Raw"/>. Only items that have metadata but no persisted
/// credits yet are touched, so re-running after the first pass is cheap and safe. New and re-fetched items
/// are handled live by the enrich pipeline via <see cref="PersonSyncService"/>.
/// </summary>
public sealed class PersonBackfillService(MediaServerDbContext database, PersonSyncService sync, ILogger<PersonBackfillService> logger)
{
    public async Task<PersonBackfillReport> BackfillAsync(CancellationToken cancellationToken)
    {
        // Items that already have credits were populated by a previous run or by enrich; skip them.
        var alreadyPopulated = database.MediaItemPersons.Select(link => link.MediaItemId);

        // One query for every candidate record (no N+1); group by item and parse each Raw once in memory.
        var records = await database.MetadataRecords
            .Where(record => record.Raw != null && !alreadyPopulated.Contains(record.MediaItemId))
            .Select(record => new { record.MediaItemId, record.Provider, record.Raw })
            .ToListAsync(cancellationToken);

        var itemsProcessed = 0;
        var creditsWritten = 0;
        foreach (var group in records.GroupBy(record => record.MediaItemId))
        {
            itemsProcessed++;
            // The credits are language-independent, so any cached record will do; prefer one whose Raw
            // actually carries a credits object.
            var chosen = group.FirstOrDefault(record => PersonCredits.Parse(record.Raw).Count > 0) ?? group.First();
            creditsWritten += await sync.SyncAsync(group.Key, chosen.Provider, chosen.Raw, cancellationToken);
        }

        if (itemsProcessed > 0)
        {
            logger.LogInformation(
                "People backfill: processed {Items} item(s), wrote {Credits} credit(s).", itemsProcessed, creditsWritten);
        }

        return new PersonBackfillReport(itemsProcessed, creditsWritten);
    }
}

/// <summary>Runs <see cref="PersonBackfillService.BackfillAsync"/> once on startup to populate existing items.</summary>
public sealed class PersonBackfillWorker(IServiceScopeFactory scopeFactory, ILogger<PersonBackfillWorker> logger)
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
            var service = scope.ServiceProvider.GetRequiredService<PersonBackfillService>();
            await service.BackfillAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "People backfill failed; it will retry on the next start.");
        }
    }
}
