using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Metadata;

/// <summary>
/// Projects tags for metadata records that predate the tag table, once, in the background.
/// </summary>
/// <remarks>
/// <para>
/// Without this, a library only becomes searchable by genre or keyword as its titles happen to be
/// re-enriched — which for a settled library is never. The schema migration cannot do it: keywords
/// live inside a raw JSON payload whose shape differs between movies and series, and reading it is
/// .NET work, not SQL.
/// </para>
/// <para>
/// Finds its own work rather than tracking a marker: a record with no tag rows is exactly what needs
/// doing, so an interrupted run resumes and a completed one costs a single count. That also covers
/// the case a marker would miss — a record restored from a backup taken before the backfill.
/// </para>
/// <para>
/// A record whose genres and keywords are both empty would be re-examined on every start, since it
/// produces no rows to notice it by. It is written to as a genre tag with an empty marker value —
/// see <see cref="EmptyMarker"/> — so "already done" stays cheap to establish.
/// </para>
/// </remarks>
public sealed class MetadataTagBackfillWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<MetadataTagBackfillWorker> logger,
    /// <summary>Records handled per batch, so a large library does not load into memory at once.</summary>
    int batchSize = 200) : BackgroundService
{

    /// <summary>
    /// Written for a record that projects to nothing, so it is not mistaken for unprocessed forever.
    /// Never matches a caller's filter: a search term is trimmed and a blank one is not a filter.
    /// </summary>
    internal const string EmptyMarker = "";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-backfill is fine: the next start picks up the records still untagged.
        }
        catch (Exception exception)
        {
            // A failed backfill degrades search for old titles. It must not stop the server, and the
            // next start will try again.
            logger.LogError(exception, "Backfilling metadata tags failed.");
        }
    }

    /// <summary>The backfill itself. Internal so it can be driven directly rather than through the host.</summary>
    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var processed = 0;
        var cursor = Guid.Empty;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var sync = scope.ServiceProvider.GetRequiredService<MetadataTagSync>();

            var pending = await database.MetadataRecords
                .AsNoTracking()
                .Where(record => record.Id.CompareTo(cursor) > 0
                    && !database.MetadataTags.Any(tag => tag.MetadataRecordId == record.Id))
                .OrderBy(record => record.Id)
                .Take(batchSize)
                .Join(
                    database.MediaItems.AsNoTracking(),
                    record => record.MediaItemId,
                    item => item.Id,
                    (record, item) => new { Record = record, item.Kind })
                .ToListAsync(cancellationToken);
            if (pending.Count == 0)
            {
                break;
            }

            foreach (var entry in pending)
            {
                var tags = sync.TagsFor(entry.Record, entry.Kind).ToList();
                database.MetadataTags.AddRange(tags.Count > 0
                    ? tags
                    : [new MetadataTag
                    {
                        Id = Guid.NewGuid(),
                        MetadataRecordId = entry.Record.Id,
                        Kind = MetadataTagKind.Genre,
                        Value = EmptyMarker,
                    }]);
            }

            await database.SaveChangesAsync(cancellationToken);
            processed += pending.Count;
            // Max rather than the last row: the join is free to return the batch in any order, and a
            // cursor taken from whatever landed last would step over the records behind it.
            var advanced = pending.Max(entry => entry.Record.Id);
            if (advanced.CompareTo(cursor) <= 0)
            {
                // Unreachable while the cursor is the batch's maximum, and stated anyway: every way this
                // walk has gone wrong so far ended in a loop that spins rather than one that stops, and a
                // background service burning a core forever is a worse failure than an incomplete index.
                logger.LogError("Metadata tag backfill stopped: the cursor did not advance past {Cursor}.", cursor);
                break;
            }

            cursor = advanced;
        }

        if (processed > 0)
        {
            logger.LogInformation("Backfilled metadata tags for {Count} record(s).", processed);
        }

        return processed;
    }
}
