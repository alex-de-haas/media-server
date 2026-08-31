using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Catalogs;

/// <summary>What a nightly incremental refresh looked at and touched.</summary>
/// <param name="Skipped">
/// True when the pass declined to run: no marker yet (the first night only starts watching), or the
/// provider could not answer. Neither is evidence that nothing changed.
/// </param>
public sealed record IncrementalRefreshReport(bool Skipped, int Changed, int Refreshed, int Failed);

/// <summary>
/// Refreshes the titles the provider says it edited, and only those.
/// </summary>
/// <remarks>
/// A full refresh of every catalog is a few thousand provider calls to learn that almost nothing moved,
/// which is why the manual action stays manual. The change list inverts it: one query names what the
/// provider touched anywhere, the library is intersected with that, and a normal night refreshes a
/// handful of titles or none at all.
///
/// The marker is the whole design. It advances only on a pass that actually completed, so a night the
/// provider was unreachable is retried rather than skipped; and a gap longer than the provider keeps is
/// clamped to what it can still answer, because the alternative — quietly falling back to refreshing
/// everything — is the expensive pass this exists to avoid.
/// </remarks>
public sealed class IncrementalMetadataRefreshService(
    MediaServerDbContext database,
    IMetadataChangeFeed changeFeed,
    LibraryMaintenanceService maintenance,
    TimeProvider time,
    ILogger<IncrementalMetadataRefreshService> logger)
{
    /// <summary>Paced like the catalog refresh: enrich issues several provider calls per item.</summary>
    private static readonly TimeSpan ItemDelay = TimeSpan.FromMilliseconds(250);

    public async Task<IncrementalRefreshReport> RunAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var settings = await database.AppSettings.FirstOrDefaultAsync(row => row.Id == AppSettings.SingletonId, cancellationToken);
        if (settings is null)
        {
            settings = new AppSettings { Id = AppSettings.SingletonId, UpdatedAt = now };
            database.AppSettings.Add(settings);
        }

        if (settings.MetadataChangesSyncedThrough is not { } since)
        {
            // Nothing to catch up on: the library was enriched as it was imported, and reaching backwards
            // would refresh titles on no evidence at all. Start watching from here.
            settings.MetadataChangesSyncedThrough = now;
            settings.UpdatedAt = now;
            await database.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Incremental metadata refresh: first run — following provider changes from now on.");
            return new IncrementalRefreshReport(Skipped: true, 0, 0, 0);
        }

        var earliest = now - changeFeed.MaxWindow;
        if (since < earliest)
        {
            logger.LogWarning(
                "Incremental metadata refresh: the last pass was {Since:u}, beyond the {Days}-day window the provider keeps. " +
                "Refreshing what it can still answer for; anything older needs a manual catalog refresh.",
                since, changeFeed.MaxWindow.TotalDays);
            since = earliest;
        }

        var identities = await LibraryIdentitiesAsync(cancellationToken);
        if (identities.Count == 0)
        {
            settings.MetadataChangesSyncedThrough = now;
            settings.UpdatedAt = now;
            await database.SaveChangesAsync(cancellationToken);
            return new IncrementalRefreshReport(Skipped: false, 0, 0, 0);
        }

        var changedItemIds = new List<Guid>();
        foreach (var kind in (MediaKind[])[MediaKind.Movie, MediaKind.Series])
        {
            var changed = await changeFeed.GetChangedAsync(kind, since, now, cancellationToken);
            if (changed is null)
            {
                // Leave the marker where it is: the next run re-asks for this window rather than stepping
                // over it, which is the difference between a retry and a silent hole.
                return new IncrementalRefreshReport(Skipped: true, 0, 0, 0);
            }

            foreach (var providerId in changed)
            {
                if (identities.TryGetValue((kind, providerId), out var itemIds))
                {
                    changedItemIds.AddRange(itemIds);
                }
            }
        }

        var refreshed = 0;
        var failed = 0;
        for (var index = 0; index < changedItemIds.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await maintenance.RefreshMetadataAsync(changedItemIds[index], cancellationToken))
                {
                    refreshed++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Incremental metadata refresh: failed to enrich item {Item}.", changedItemIds[index]);
                failed++;
            }

            if (index < changedItemIds.Count - 1)
            {
                await Task.Delay(ItemDelay, time, cancellationToken);
            }
        }

        settings.MetadataChangesSyncedThrough = now;
        settings.UpdatedAt = now;
        await database.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Incremental metadata refresh: {Changed} library title(s) changed since {Since:u}, {Refreshed} refreshed, {Failed} failed.",
            changedItemIds.Count, since, refreshed, failed);
        return new IncrementalRefreshReport(Skipped: false, changedItemIds.Count, refreshed, failed);
    }

    /// <summary>
    /// The library's published works, keyed by the provider identity a change list names. A title held in
    /// two places (the same film in another catalog, or a ghost beside a live copy) maps to several rows,
    /// so the value is a list rather than a single id.
    /// </summary>
    private async Task<Dictionary<(MediaKind Kind, string ProviderId), List<Guid>>> LibraryIdentitiesAsync(
        CancellationToken cancellationToken)
    {
        var works = await database.MediaItems.AsNoTracking()
            .Where(item => item.PublicId != null &&
                (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series) &&
                item.IdentityProvider == changeFeed.Key && item.IdentityProviderId != null)
            .Select(item => new { item.Id, item.Kind, ProviderId = item.IdentityProviderId! })
            .ToListAsync(cancellationToken);

        var map = new Dictionary<(MediaKind, string), List<Guid>>();
        foreach (var work in works)
        {
            if (!map.TryGetValue((work.Kind, work.ProviderId), out var ids))
            {
                map[(work.Kind, work.ProviderId)] = ids = [];
            }

            ids.Add(work.Id);
        }

        return map;
    }
}
