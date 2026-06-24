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
        var pending = await database.MetadataRecords
            .Where(record => !alreadyPopulated.Contains(record.MediaItemId))
            .Select(record => record.MediaItemId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var creditsWritten = 0;
        foreach (var mediaItemId in pending)
        {
            // The credits are language-independent, so any cached record will do; prefer one whose Raw
            // actually carries a credits object.
            var records = await database.MetadataRecords
                .Where(record => record.MediaItemId == mediaItemId && record.Raw != null)
                .Select(record => new { record.Provider, record.Raw })
                .ToListAsync(cancellationToken);

            var chosen = records.FirstOrDefault(record => PersonCredits.Parse(record.Raw).Count > 0)
                         ?? records.FirstOrDefault();
            if (chosen is null)
            {
                continue;
            }

            creditsWritten += await sync.SyncAsync(mediaItemId, chosen.Provider, chosen.Raw, cancellationToken);
        }

        if (pending.Count > 0)
        {
            logger.LogInformation(
                "People backfill: processed {Items} item(s), wrote {Credits} credit(s).", pending.Count, creditsWritten);
        }

        return new PersonBackfillReport(pending.Count, creditsWritten);
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
