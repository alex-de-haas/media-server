using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Jobs;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Watchlist;

/// <summary>How many tracked titles a date-sync pass looked at and how they fared.</summary>
public sealed record WatchlistSyncReport(int Total, int Synced, int Skipped, int Failed);

/// <summary>
/// Layer-1 date sync (<c>watchlist:refresh-dates</c>): refreshes every <see cref="TrackedTitle"/>'s typed
/// release dates from the provider into local <see cref="TrackedRelease"/> rows. The only loop that calls
/// TMDb, so it stays a plain fixed cadence, paced under the provider rate limit. Purely global layer 1 —
/// reminder semantics (retirement, delivery) live in the dispatch loop. See
/// <c>docs/features/release-tracking.md</c>.
/// </summary>
public sealed class WatchlistSyncService(
    MediaServerDbContext database,
    IServiceScopeFactory scopeFactory,
    IReleaseScheduleProvider provider,
    MediaServerSettings settings,
    JobService jobs,
    TimeProvider timeProvider,
    ILogger<WatchlistSyncService> logger)
{
    public const string JobType = "watchlist:refresh-dates";

    /// <summary>Episode air dates are kept only for this rolling horizon around today (app timezone).</summary>
    public static readonly TimeSpan HorizonPast = TimeSpan.FromDays(7);
    public static readonly TimeSpan HorizonFuture = TimeSpan.FromDays(90);

    // The sync issues 1..N provider calls per title (title-level + opt-in seasons), so pace titles to
    // stay well under TMDb's rate limit rather than fetching in a tight loop.
    private static readonly TimeSpan TitleDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Runs a full pass over the stale titles (skipping settled ones), reporting progress on
    /// <paramref name="job"/> when given. Each title syncs in its own DI scope so the change tracker
    /// stays bounded.
    /// </summary>
    public async Task<WatchlistSyncReport> SyncAllAsync(Job? job, CancellationToken cancellationToken)
    {
        var titleIds = await ListStaleTitleIdsAsync(cancellationToken);

        var total = titleIds.Count;
        var synced = 0;
        var skipped = 0;
        var failed = 0;
        var lastPercent = 0;

        for (var index = 0; index < total; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (await SyncOneInScopeAsync(titleIds[index], force: false, cancellationToken))
            {
                case SyncOutcome.Synced:
                    synced++;
                    break;
                case SyncOutcome.Skipped:
                    skipped++;
                    break;
                case SyncOutcome.Failed:
                    failed++;
                    break;
            }

            if (job is not null)
            {
                var percent = (int)((index + 1) * 100L / total);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    await jobs.ProgressAsync(job, percent, cancellationToken);
                }
            }

            if (index < total - 1)
            {
                await Task.Delay(TitleDelay, cancellationToken);
            }
        }

        if (total > 0)
        {
            logger.LogInformation(
                "Watchlist date-sync: {Synced}/{Total} synced, {Skipped} skipped, {Failed} failed.",
                synced, total, skipped, failed);
        }

        return new WatchlistSyncReport(total, synced, skipped, failed);
    }

    /// <summary>Titles due in a periodic pass: never synced, or last synced long enough ago that the 24h
    /// cadence (minus restart jitter) has elapsed. A restart therefore doesn't re-hit the provider for
    /// titles refreshed a few hours earlier.</summary>
    public async Task<List<Guid>> ListStaleTitleIdsAsync(CancellationToken cancellationToken)
    {
        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromHours(20);
        return await database.TrackedTitles.AsNoTracking()
            .Where(title => title.LastRefreshedAt == null || title.LastRefreshedAt < cutoff)
            .OrderBy(title => title.LastRefreshedAt)
            .Select(title => title.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Syncs one title in a fresh DI scope; used by the worker for on-demand (forced) syncs too.</summary>
    public async Task<SyncOutcome> SyncOneInScopeAsync(Guid trackedTitleId, bool force, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<WatchlistSyncService>();
            return await service.SyncTitleAsync(trackedTitleId, force, cancellationToken);
        }
        // Catch any per-title failure — including a provider timeout, which surfaces as a (Task)Canceled
        // with our token un-cancelled — but let a genuine caller-requested cancellation stop the run.
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Watchlist date-sync failed for title {Title}.", trackedTitleId);
            return SyncOutcome.Failed;
        }
    }

    /// <summary>
    /// Refreshes one title: upserts its <see cref="TrackedRelease"/> rows (recording
    /// <see cref="TrackedRelease.PreviousDate"/> when a date moved), refreshes the display/status
    /// snapshot, and prunes aged episode rows. <paramref name="force"/> bypasses the settled-title skip
    /// (add / manual refresh).
    /// </summary>
    public async Task<SyncOutcome> SyncTitleAsync(Guid trackedTitleId, bool force, CancellationToken cancellationToken)
    {
        var title = await database.TrackedTitles
            .Include(candidate => candidate.Releases)
            .Include(candidate => candidate.Entries)
            .FirstOrDefaultAsync(candidate => candidate.Id == trackedTitleId, cancellationToken);
        if (title is null)
        {
            return SyncOutcome.Skipped; // Removed between selection and sync.
        }

        var today = WatchlistScope.LocalDay(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone);
        if (!force && IsSettled(title, today))
        {
            return SyncOutcome.Skipped;
        }

        var outcome = title.Kind == MediaKind.Movie
            ? await SyncMovieAsync(title, cancellationToken)
            : await SyncSeriesAsync(title, today, cancellationToken);

        if (outcome == SyncOutcome.Synced)
        {
            title.LastRefreshedAt = timeProvider.GetUtcNow();
            title.UpdatedAt = title.LastRefreshedAt.Value;
            await database.SaveChangesAsync(cancellationToken);
        }

        return outcome;
    }

    /// <summary>
    /// A settled title has nothing left to learn on a periodic pass: a movie past all its dates (digital
    /// date known and gone) with a Released status, or an Ended/Canceled series with no future episode.
    /// A forced sync (add / manual refresh / region change) always bypasses this.
    /// </summary>
    internal static bool IsSettled(TrackedTitle title, DateOnly today)
    {
        if (title.Kind == MediaKind.Movie)
        {
            // Without a known digital date the movie may still gain one — keep syncing.
            return string.Equals(title.ProductionStatus, "Released", StringComparison.OrdinalIgnoreCase)
                && title.Releases.Any(release => release.Type == ReleaseType.Digital)
                && title.Releases.All(release => release.Date < today);
        }

        var over = string.Equals(title.ProductionStatus, "Ended", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title.ProductionStatus, "Canceled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(title.ProductionStatus, "Cancelled", StringComparison.OrdinalIgnoreCase);
        return over && title.Releases.All(release => release.Date <= today);
    }

    private async Task<SyncOutcome> SyncMovieAsync(TrackedTitle title, CancellationToken cancellationToken)
    {
        // Regions to keep dates for: the app watch region plus every entry's override.
        var regions = title.Entries
            .Select(entry => entry.RegionOverride)
            .Where(region => !string.IsNullOrWhiteSpace(region))
            .Select(region => region!.ToUpperInvariant())
            .Append(settings.WatchRegion)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var schedule = await provider.GetMovieScheduleAsync(title.IdentityProviderId, regions, cancellationToken);
        if (schedule is null)
        {
            return SyncOutcome.Failed; // Provider hiccup or unknown id: keep existing rows, retry next pass.
        }

        ApplySnapshot(title, schedule.Title, schedule.Year, schedule.PosterUrl, schedule.Status);

        var now = timeProvider.GetUtcNow();
        var existing = title.Releases.Where(release => release.Region is not null).ToList();
        foreach (var date in schedule.Dates)
        {
            var row = existing.FirstOrDefault(candidate =>
                string.Equals(candidate.Region, date.Region, StringComparison.Ordinal) && candidate.Type == date.Type);
            if (row is null)
            {
                // Via the DbSet, not the navigation: an entity with a pre-set Guid key discovered through
                // a tracked navigation would be classified Modified (an UPDATE), not Added.
                database.TrackedReleases.Add(NewRelease(title.Id, date.Region, date.Type, date.RawType, null, null, date.Date, date.Note, now));
                continue;
            }

            UpdateRelease(row, date.Date, date.RawType, date.Note, now);
        }

        // A (region, type) the provider no longer reports — or a region no entry watches anymore — is
        // dropped, unless a delivery references it (keeping the once-per-event ledger intact if the
        // date ever reappears). Movie rows are never age-pruned.
        var desired = schedule.Dates.Select(date => (date.Region, date.Type)).ToHashSet();
        var stale = existing.Where(release => !desired.Contains((release.Region!, release.Type))).ToList();
        await RemoveUnlessDeliveredAsync(stale, cancellationToken);

        return SyncOutcome.Synced;
    }

    private async Task<SyncOutcome> SyncSeriesAsync(TrackedTitle title, DateOnly today, CancellationToken cancellationToken)
    {
        var schedule = await provider.GetSeriesScheduleAsync(title.IdentityProviderId, cancellationToken);
        if (schedule is null)
        {
            return SyncOutcome.Failed;
        }

        ApplySnapshot(title, schedule.Title, schedule.Year, schedule.PosterUrl, schedule.Status);
        title.LastAiredSeason = schedule.LastEpisode?.Season;
        title.LastAiredEpisode = schedule.LastEpisode?.Episode;
        title.LastAiredDate = schedule.LastEpisode?.AirDate;

        var horizonStart = today.AddDays(-(int)HorizonPast.TotalDays);
        var horizonEnd = today.AddDays((int)HorizonFuture.TotalDays);

        // The title-level next/last episode come free with the /tv/{id} call, so every tracked series
        // shows when it returns even with episode tracking off.
        var episodes = new Dictionary<(int Season, int Episode), EpisodeAirDate>();
        foreach (var episode in new[] { schedule.NextEpisode, schedule.LastEpisode })
        {
            if (episode is not null)
            {
                episodes[(episode.Season, episode.Episode)] = episode;
            }
        }

        // Opt-in per-episode enumeration: only for the union of seasons monitored by entries that turned
        // episode tracking on, paced like the title fetches.
        foreach (var season in MonitoredSeasons(title, schedule))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TitleDelay, cancellationToken);
            foreach (var episode in await provider.GetSeasonEpisodesAsync(title.IdentityProviderId, season, cancellationToken))
            {
                episodes[(episode.Season, episode.Episode)] = episode;
            }
        }

        var now = timeProvider.GetUtcNow();
        var desired = episodes.Values.Where(episode => episode.AirDate >= horizonStart && episode.AirDate <= horizonEnd).ToList();
        var existing = title.Releases.Where(release => release.Type == ReleaseType.EpisodeAir).ToList();
        foreach (var episode in desired)
        {
            var row = existing.FirstOrDefault(candidate =>
                candidate.Season == episode.Season && candidate.Episode == episode.Episode);
            if (row is null)
            {
                // Via the DbSet, not the navigation — see SyncMovieAsync.
                database.TrackedReleases.Add(NewRelease(
                    title.Id, region: null, ReleaseType.EpisodeAir, rawType: null,
                    episode.Season, episode.Episode, episode.AirDate, episode.Name, now));
                continue;
            }

            UpdateRelease(row, episode.AirDate, rawType: null, episode.Name, now);
        }

        // Horizon pruning: rows aging out (or dropped by the provider / by a scope change) go away —
        // unless they back an unfired delivery a still-active reminder needs.
        var desiredKeys = desired.Select(episode => (episode.Season, episode.Episode)).ToHashSet();
        var stale = existing
            .Where(release => !desiredKeys.Contains((release.Season ?? -1, release.Episode ?? -1)))
            .ToList();
        await PruneEpisodeRowsAsync(title, stale, cancellationToken);

        return SyncOutcome.Synced;
    }

    private IEnumerable<int> MonitoredSeasons(TrackedTitle title, SeriesReleaseSchedule schedule)
    {
        var seasons = new SortedSet<int>();
        // "Current season and later" for the future-episodes scope: enumeration only ever needs seasons
        // that can still contain horizon-window episodes.
        var currentSeason = schedule.NextEpisode?.Season ?? schedule.LastEpisode?.Season;

        foreach (var entry in title.Entries)
        {
            switch (entry.MonitorScope)
            {
                case SeriesMonitorScope.WholeShow:
                    seasons.UnionWith(schedule.Seasons);
                    break;
                case SeriesMonitorScope.Seasons when entry.MonitoredSeasons is not null:
                    seasons.UnionWith(schedule.Seasons.Intersect(entry.MonitoredSeasons));
                    break;
                case SeriesMonitorScope.FutureEpisodes when currentSeason is not null:
                    seasons.UnionWith(schedule.Seasons.Where(season => season >= currentSeason));
                    break;
            }
        }

        return seasons;
    }

    /// <summary>
    /// Deletes aged/undesired episode rows, sparing any that back an unfired delivery: an active
    /// <see cref="ReleaseType.EpisodeAir"/> reminder whose entry scope covers the episode (and which
    /// existed before the episode aired — pre-existing episodes are never delivered retroactively) and
    /// that has no <see cref="ReminderDelivery"/> for the row yet.
    /// </summary>
    private async Task PruneEpisodeRowsAsync(TrackedTitle title, List<TrackedRelease> stale, CancellationToken cancellationToken)
    {
        if (stale.Count == 0)
        {
            return;
        }

        var reminders = await database.ReleaseReminders.AsNoTracking()
            .Where(reminder => reminder.TrackedTitleId == title.Id && reminder.Active && reminder.ReleaseType == ReleaseType.EpisodeAir)
            .ToListAsync(cancellationToken);
        var staleIds = stale.Select(release => release.Id).ToList();
        var delivered = await database.ReminderDeliveries.AsNoTracking()
            .Where(delivery => staleIds.Contains(delivery.TrackedReleaseId))
            .Select(delivery => new { delivery.ReminderId, delivery.TrackedReleaseId })
            .ToListAsync(cancellationToken);
        var timeZone = timeProvider.LocalTimeZone;

        foreach (var release in stale)
        {
            var spared = release is { Season: not null, Episode: not null } && reminders.Any(reminder =>
                release.Date >= WatchlistScope.LocalDay(reminder.CreatedAt, timeZone)
                && !delivered.Any(delivery => delivery.ReminderId == reminder.Id && delivery.TrackedReleaseId == release.Id)
                && title.Entries.Any(entry =>
                    entry.AppUserId == reminder.AppUserId
                    && WatchlistScope.Covers(entry, release.Season.Value, release.Date, timeZone)));
            if (!spared)
            {
                database.TrackedReleases.Remove(release);
            }
        }
    }

    private async Task RemoveUnlessDeliveredAsync(List<TrackedRelease> stale, CancellationToken cancellationToken)
    {
        if (stale.Count == 0)
        {
            return;
        }

        var staleIds = stale.Select(release => release.Id).ToList();
        var referenced = await database.ReminderDeliveries.AsNoTracking()
            .Where(delivery => staleIds.Contains(delivery.TrackedReleaseId))
            .Select(delivery => delivery.TrackedReleaseId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var release in stale.Where(release => !referenced.Contains(release.Id)))
        {
            database.TrackedReleases.Remove(release);
        }
    }

    private static void ApplySnapshot(TrackedTitle title, string? name, int? year, string? posterUrl, string? status)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            title.Title = name;
        }

        title.Year = year ?? title.Year;
        title.PosterUrl = posterUrl ?? title.PosterUrl;
        title.ProductionStatus = status ?? title.ProductionStatus;
    }

    private static TrackedRelease NewRelease(
        Guid titleId, string? region, ReleaseType type, int? rawType, int? season, int? episode,
        DateOnly date, string? note, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        TrackedTitleId = titleId,
        Region = region,
        Type = type,
        RawType = rawType,
        Season = season,
        Episode = episode,
        Date = date,
        Note = note,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private static void UpdateRelease(TrackedRelease row, DateOnly date, int? rawType, string? note, DateTimeOffset now)
    {
        if (row.Date != date)
        {
            // A moved date: keep the prior value so the UI can show "moved from …" and reminders follow.
            row.PreviousDate = row.Date;
            row.Date = date;
            row.UpdatedAt = now;
        }

        if (row.RawType != rawType || row.Note != note)
        {
            row.RawType = rawType;
            row.Note = note;
            row.UpdatedAt = now;
        }
    }

    public enum SyncOutcome
    {
        Synced,
        Skipped,
        Failed,
    }
}
