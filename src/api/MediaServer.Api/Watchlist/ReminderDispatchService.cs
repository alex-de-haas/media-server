using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Watchlist;

/// <summary>What one dispatch tick did: notifications sent and reminders retired.</summary>
public sealed record ReminderDispatchReport(int Delivered, int Retired);

/// <summary>
/// Layer-2 reminder dispatch (<c>watchlist:dispatch-reminders</c>): matches every active
/// <see cref="ReleaseReminder"/> against the locally stored <see cref="TrackedRelease"/> rows and delivers
/// the ones now due as per-user Hosty Core notifications. Touches no external API, so it runs frequently
/// (~15 min) to land a reminder close to its chosen time. The <see cref="ReminderDelivery"/> ledger plus
/// the Core dedupe key guarantee exactly one notification per concrete release event; retirement of
/// finished series reminders lives here too (date-sync stays purely layer 1).
/// </summary>
public sealed class ReminderDispatchService(
    MediaServerDbContext database,
    MediaServerSettings settings,
    IHostyCoreClient core,
    TimeProvider timeProvider,
    ILogger<ReminderDispatchService> logger)
{
    public const string JobType = "watchlist:dispatch-reminders";

    public async Task<ReminderDispatchReport> DispatchAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var timeZone = timeProvider.LocalTimeZone;
        var today = WatchlistScope.LocalDay(now, timeZone);

        var reminders = await database.ReleaseReminders
            .Include(reminder => reminder.AppUser)
            .Include(reminder => reminder.TrackedTitle).ThenInclude(title => title!.Releases)
            .Include(reminder => reminder.Deliveries)
            .Where(reminder => reminder.Active)
            .ToListAsync(cancellationToken);
        if (reminders.Count == 0)
        {
            return new ReminderDispatchReport(0, 0);
        }

        // The reminder's scope/region context is its user's entry on the same title.
        var titleIds = reminders.Select(reminder => reminder.TrackedTitleId).Distinct().ToList();
        var entries = await database.WatchlistEntries.AsNoTracking()
            .Where(entry => titleIds.Contains(entry.TrackedTitleId))
            .ToListAsync(cancellationToken);

        var delivered = 0;
        var retired = 0;
        foreach (var reminder in reminders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var title = reminder.TrackedTitle!;
            var entry = entries.FirstOrDefault(candidate =>
                candidate.AppUserId == reminder.AppUserId && candidate.TrackedTitleId == reminder.TrackedTitleId);

            var due = DueReleases(reminder, title, entry, now, timeZone);
            if (due.Count > 0)
            {
                delivered += await DeliverAsync(reminder, title, due, today, cancellationToken);
                await database.SaveChangesAsync(cancellationToken); // Ledger each reminder as it lands.
            }

            if (ShouldRetire(reminder, title, entry, today, timeZone))
            {
                reminder.Active = false;
                retired++;
                await PublishAsync(
                    reminder,
                    $"{title.Title} has ended",
                    "The series is over and every tracked episode was delivered — this reminder was retired.",
                    dedupeKey: $"media-server:reminder-retired:{reminder.Id}",
                    cancellationToken);
                await database.SaveChangesAsync(cancellationToken);
            }
        }

        if (delivered > 0 || retired > 0)
        {
            logger.LogInformation("Reminder dispatch: {Delivered} notification(s) sent, {Retired} reminder(s) retired.", delivered, retired);
        }

        return new ReminderDispatchReport(delivered, retired);
    }

    /// <summary>
    /// The concrete release events this reminder should fire for right now: same title and type, the
    /// entry's effective region (movies) or monitor scope (episodes, never before the reminder existed),
    /// fire time reached, and no delivery ledgered yet. A fire time already in the past simply fires on
    /// this tick.
    /// </summary>
    private List<TrackedRelease> DueReleases(
        ReleaseReminder reminder, TrackedTitle title, WatchlistEntry? entry, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        IEnumerable<TrackedRelease> candidates;
        if (reminder.ReleaseType == ReleaseType.EpisodeAir)
        {
            // Episodes aired before the reminder was created are summarized at create time, never
            // delivered retroactively — recurring delivery starts from the next episode within scope.
            var createdDay = WatchlistScope.LocalDay(reminder.CreatedAt, timeZone);
            candidates = title.Releases.Where(release =>
                release is { Type: ReleaseType.EpisodeAir, Season: not null }
                && release.Date >= createdDay
                && entry is not null
                && WatchlistScope.Covers(entry, release.Season.Value, release.Date, timeZone));
        }
        else
        {
            var region = EffectiveRegion(entry);
            candidates = title.Releases.Where(release =>
                release.Type == reminder.ReleaseType
                && string.Equals(release.Region, region, StringComparison.OrdinalIgnoreCase));
        }

        return candidates
            .Where(release => ReminderTiming.FireAt(release.Date, reminder.LeadDays, reminder.NotifyAt, timeZone) <= now)
            .Where(release => reminder.Deliveries.All(delivery => delivery.TrackedReleaseId != release.Id))
            .OrderBy(release => release.Date).ThenBy(release => release.Season).ThenBy(release => release.Episode)
            .ToList();
    }

    /// <summary>
    /// Delivers the due releases: one notification per event, except a whole-season drop — coinciding
    /// same-season, same-date episode airs collapse into a single "Season N available" notification. The
    /// ledger still records every covered episode so nothing re-fires.
    /// </summary>
    private async Task<int> DeliverAsync(
        ReleaseReminder reminder, TrackedTitle title, List<TrackedRelease> due, DateOnly today, CancellationToken cancellationToken)
    {
        var sent = 0;
        foreach (var group in due.GroupBy(release => (release.Season, release.Date)))
        {
            var episodes = group.ToList();
            bool published;
            if (episodes.Count > 1 && reminder.ReleaseType == ReleaseType.EpisodeAir)
            {
                published = await PublishAsync(
                    reminder,
                    $"{title.Title} — Season {group.Key.Season} available",
                    $"{episodes.Count} episodes {Tense(group.Key.Date, today)} {When(group.Key.Date, today)}.",
                    dedupeKey: $"media-server:reminder:{reminder.Id}:s{group.Key.Season}:{group.Key.Date:yyyy-MM-dd}",
                    cancellationToken);
            }
            else
            {
                var release = episodes[0];
                published = await PublishAsync(
                    reminder,
                    NotificationTitle(title, release, today),
                    NotificationBody(release, today),
                    dedupeKey: $"media-server:reminder:{reminder.Id}:{release.Id}",
                    cancellationToken);
            }

            if (!published)
            {
                continue; // Core hiccup: leave the ledger empty so the next tick retries.
            }

            sent++;
            foreach (var release in episodes)
            {
                database.ReminderDeliveries.Add(new ReminderDelivery
                {
                    Id = Guid.NewGuid(),
                    ReminderId = reminder.Id,
                    TrackedReleaseId = release.Id,
                    SentAt = timeProvider.GetUtcNow(),
                });
            }
        }

        return sent;
    }

    /// <summary>
    /// A recurring series reminder retires — instead of lingering forever — once the show is officially
    /// over (`Ended`/`Canceled`), no future episode remains, and every covered aired episode was already
    /// delivered. User-initiated deactivation/removal happens through the API, not here.
    /// </summary>
    private bool ShouldRetire(
        ReleaseReminder reminder, TrackedTitle title, WatchlistEntry? entry, DateOnly today, TimeZoneInfo timeZone)
    {
        if (reminder.ReleaseType != ReleaseType.EpisodeAir || !IsOver(title.ProductionStatus))
        {
            return false;
        }

        if (title.Releases.Any(release => release.Type == ReleaseType.EpisodeAir && release.Date > today))
        {
            return false; // A final episode is still ahead.
        }

        // Anything aired, covered, and not yet delivered keeps the reminder alive for the next tick.
        var createdDay = WatchlistScope.LocalDay(reminder.CreatedAt, timeZone);
        return !title.Releases.Any(release =>
            release is { Type: ReleaseType.EpisodeAir, Season: not null }
            && release.Date >= createdDay
            && entry is not null
            && WatchlistScope.Covers(entry, release.Season.Value, release.Date, timeZone)
            && reminder.Deliveries.All(delivery => delivery.TrackedReleaseId != release.Id));
    }

    internal static bool IsOver(string? productionStatus) =>
        string.Equals(productionStatus, "Ended", StringComparison.OrdinalIgnoreCase)
        || string.Equals(productionStatus, "Canceled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(productionStatus, "Cancelled", StringComparison.OrdinalIgnoreCase);

    private string EffectiveRegion(WatchlistEntry? entry) =>
        string.IsNullOrWhiteSpace(entry?.RegionOverride) ? settings.WatchRegion : entry.RegionOverride.ToUpperInvariant();

    private static string NotificationTitle(TrackedTitle title, TrackedRelease release, DateOnly today) =>
        release.Type == ReleaseType.EpisodeAir
            ? $"{title.Title} — S{release.Season}E{release.Episode} {Tense(release.Date, today)} {When(release.Date, today)}"
            : $"{title.Title} — {TypeLabel(release.Type)} release {Tense(release.Date, today)} {When(release.Date, today)}";

    private static string NotificationBody(TrackedRelease release, DateOnly today)
    {
        var what = release.Type == ReleaseType.EpisodeAir
            ? $"Episode {release.Episode} of season {release.Season}{(release.Note is null ? "" : $" (“{release.Note}”)")}"
            : $"The {TypeLabel(release.Type).ToLowerInvariant()} release{(release.Note is null ? "" : $" ({release.Note})")}";
        var verb = release.Date <= today ? "landed" : "lands";
        return $"{what} {verb} on {release.Date:MMM d, yyyy}.";
    }

    /// <summary>"is out"/"airs" phrasing pivots on whether the date has passed.</summary>
    private static string Tense(DateOnly date, DateOnly today) => date <= today ? "released" : "releasing";

    private static string When(DateOnly date, DateOnly today)
    {
        var days = date.DayNumber - today.DayNumber;
        return days switch
        {
            0 => "today",
            1 => "tomorrow",
            > 1 => $"in {days} days ({date:MMM d})",
            -1 => "yesterday",
            _ => $"on {date:MMM d}",
        };
    }

    internal static string TypeLabel(ReleaseType type) => type switch
    {
        ReleaseType.Premiere => "Premiere",
        ReleaseType.Theatrical => "Theatrical",
        ReleaseType.Digital => "Digital",
        _ => "Episode",
    };

    /// <summary>
    /// Publishes a per-user notification (target = the user's Host id). Without Core (standalone run)
    /// there is nobody to notify — treated as delivered so reminders don't retry forever.
    /// </summary>
    private async Task<bool> PublishAsync(
        ReleaseReminder reminder, string notificationTitle, string body, string dedupeKey, CancellationToken cancellationToken)
    {
        if (!core.IsEnabled)
        {
            return true;
        }

        var target = reminder.AppUser?.HostUserId;
        if (target is null)
        {
            return false;
        }

        return await core.PublishNotificationAsync(
            CoreNotificationLevel.Info,
            notificationTitle,
            body,
            link: "/calendar",
            dedupeKey: dedupeKey,
            target: target,
            cancellationToken: cancellationToken);
    }
}
