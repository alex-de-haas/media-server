using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Watchlist;

/// <summary>Outcome of a reminder create/update; <see cref="Error"/> maps to a 400.</summary>
public sealed record ReminderCreateResult(ReminderResolutionDto? Resolution, bool Created, string? Error);

/// <summary>
/// Layer-2 reminder operations behind <c>/api/reminders</c>. A reminder targets a (title, release type)
/// pair — creating one validates the type against the title's kind, ensures the title is tracked
/// (remind-implies-track), and returns the resolved state (scheduled / alreadyReleased / pending) so the
/// dialog never has to look up dates first.
/// </summary>
public sealed class ReminderService(
    MediaServerDbContext database,
    MediaServerSettings settings,
    WatchlistService watchlist,
    IWatchlistSyncQueue syncQueue,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<ReminderDto>> ListAsync(int userId, CancellationToken cancellationToken)
    {
        var reminders = await database.ReleaseReminders.AsNoTracking()
            .Include(reminder => reminder.TrackedTitle).ThenInclude(title => title!.Releases)
            .Where(reminder => reminder.AppUserId == userId)
            .OrderBy(reminder => reminder.CreatedAt)
            .ToListAsync(cancellationToken);
        if (reminders.Count == 0)
        {
            return [];
        }

        var titleIds = reminders.Select(reminder => reminder.TrackedTitleId).Distinct().ToList();
        var entries = await database.WatchlistEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == userId && titleIds.Contains(entry.TrackedTitleId))
            .ToListAsync(cancellationToken);
        var deliveredReminderIds = await database.ReminderDeliveries.AsNoTracking()
            .Where(delivery => reminders.Select(reminder => reminder.Id).Contains(delivery.ReminderId))
            .Select(delivery => delivery.ReminderId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var today = WatchlistScope.LocalDay(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone);
        return reminders
            .Select(reminder => WatchlistReads.ToReminderDto(
                reminder,
                reminder.TrackedTitle!,
                entries.FirstOrDefault(entry => entry.TrackedTitleId == reminder.TrackedTitleId),
                settings,
                today,
                timeProvider.LocalTimeZone,
                deliveredReminderIds.Contains(reminder.Id)))
            .ToList();
    }

    public async Task<ReminderCreateResult> CreateAsync(int userId, CreateReminderRequest request, CancellationToken cancellationToken)
    {
        if (request.LeadDays is < 0 or > 30)
        {
            return Invalid("Lead days must be between 0 and 30.");
        }

        var notifyAt = ParseNotifyAt(request.NotifyAt);
        if (notifyAt is null)
        {
            return Invalid("The notify time must be a valid HH:mm time of day.");
        }

        // Resolve the target title: by id, or by provider ref (creating the global title if needed).
        TrackedTitle? title;
        if (request.TrackedTitleId is { } titleId)
        {
            title = await database.TrackedTitles
                .Include(candidate => candidate.Releases)
                .FirstOrDefaultAsync(candidate => candidate.Id == titleId, cancellationToken);
            if (title is null)
            {
                return Invalid("Unknown tracked title.");
            }
        }
        else if (request.ProviderRef is { } providerRef
                 && !string.IsNullOrWhiteSpace(providerRef.Provider)
                 && !string.IsNullOrWhiteSpace(providerRef.Id))
        {
            if (request.Kind is not (MediaKind.Movie or MediaKind.Series))
            {
                return Invalid("A media kind (movie or series) is required with a provider reference.");
            }

            title = await watchlist.ResolveOrCreateTitleAsync(
                providerRef, request.Kind.Value, request.Title, request.Year, request.PosterUrl, cancellationToken);
            await database.Entry(title).Collection(candidate => candidate.Releases).LoadAsync(cancellationToken);
        }
        else
        {
            return Invalid("A tracked title id or provider reference is required.");
        }

        // Kind ↔ type validation protects the pending mechanic: a type that can never have data for the
        // kind would leave the reminder pending forever.
        if (!WatchlistReads.IsValidTypeForKind(title.Kind, request.ReleaseType))
        {
            return Invalid(title.Kind == MediaKind.Series
                ? "A series reminder must target episode airings."
                : "A movie reminder must target the premiere, theatrical, or digital release.");
        }

        var now = timeProvider.GetUtcNow();

        // Remind implies track: ensure the user's WatchlistEntry exists, and an EpisodeAir reminder turns
        // on episode tracking (default scope FutureEpisodes) since it needs EpisodeAir rows to fire.
        var entry = await database.WatchlistEntries
            .FirstOrDefaultAsync(candidate => candidate.AppUserId == userId && candidate.TrackedTitleId == title.Id, cancellationToken);
        var needsSync = title.LastRefreshedAt is null;
        if (entry is null)
        {
            entry = new WatchlistEntry
            {
                Id = Guid.NewGuid(),
                AppUserId = userId,
                TrackedTitleId = title.Id,
                MonitorScope = request.ReleaseType == ReleaseType.EpisodeAir ? SeriesMonitorScope.FutureEpisodes : null,
                CreatedAt = now,
            };
            database.WatchlistEntries.Add(entry);
            needsSync = true;
        }
        else if (request.ReleaseType == ReleaseType.EpisodeAir && entry.MonitorScope is null)
        {
            entry.MonitorScope = SeriesMonitorScope.FutureEpisodes;
            needsSync = true;
        }

        // One reminder per (user, title, type): re-creating it from any entry point converges on the same
        // reminder, updating its lead/time and re-activating it.
        var reminder = await database.ReleaseReminders
            .Include(candidate => candidate.Deliveries)
            .FirstOrDefaultAsync(candidate => candidate.AppUserId == userId
                && candidate.TrackedTitleId == title.Id
                && candidate.ReleaseType == request.ReleaseType, cancellationToken);
        var created = reminder is null;
        if (reminder is null)
        {
            reminder = new ReleaseReminder
            {
                Id = Guid.NewGuid(),
                AppUserId = userId,
                TrackedTitleId = title.Id,
                ReleaseType = request.ReleaseType,
                LeadDays = request.LeadDays,
                NotifyAt = notifyAt.Value,
                Active = true,
                CreatedAt = now,
            };
            database.ReleaseReminders.Add(reminder);
        }
        else
        {
            reminder.LeadDays = request.LeadDays;
            reminder.NotifyAt = notifyAt.Value;
            reminder.Active = true;
        }

        await database.SaveChangesAsync(cancellationToken);
        if (needsSync)
        {
            syncQueue.Enqueue(title.Id); // A pending reminder binds as soon as the sync stores a date.
        }

        var today = WatchlistScope.LocalDay(now, timeProvider.LocalTimeZone);
        var (state, date, detail) = WatchlistReads.ResolveCreateState(request.ReleaseType, title, entry, settings, today);
        var dto = WatchlistReads.ToReminderDto(
            reminder, title, entry, settings, today, timeProvider.LocalTimeZone, reminder.Deliveries.Count > 0);
        return new ReminderCreateResult(new ReminderResolutionDto(dto, state, date, detail), created, null);
    }

    public async Task<ReminderDto?> UpdateAsync(
        int userId, Guid reminderId, UpdateReminderRequest request, CancellationToken cancellationToken)
    {
        var reminder = await database.ReleaseReminders
            .Include(candidate => candidate.TrackedTitle).ThenInclude(title => title!.Releases)
            .Include(candidate => candidate.Deliveries)
            .FirstOrDefaultAsync(candidate => candidate.Id == reminderId && candidate.AppUserId == userId, cancellationToken);
        if (reminder is null)
        {
            return null;
        }

        if (request.LeadDays is { } leadDays && leadDays is >= 0 and <= 30)
        {
            reminder.LeadDays = leadDays;
        }

        if (request.NotifyAt is not null && ParseNotifyAt(request.NotifyAt) is { } notifyAt)
        {
            reminder.NotifyAt = notifyAt;
        }

        if (request.Active is { } active)
        {
            reminder.Active = active;
        }

        await database.SaveChangesAsync(cancellationToken);

        var entry = await database.WatchlistEntries.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.AppUserId == userId
                && candidate.TrackedTitleId == reminder.TrackedTitleId, cancellationToken);
        var today = WatchlistScope.LocalDay(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone);
        return WatchlistReads.ToReminderDto(
            reminder, reminder.TrackedTitle!, entry, settings, today, timeProvider.LocalTimeZone,
            reminder.Deliveries.Count > 0);
    }

    public async Task<bool> DeleteAsync(int userId, Guid reminderId, CancellationToken cancellationToken)
    {
        var reminder = await database.ReleaseReminders
            .FirstOrDefaultAsync(candidate => candidate.Id == reminderId && candidate.AppUserId == userId, cancellationToken);
        if (reminder is null)
        {
            return false;
        }

        database.ReleaseReminders.Remove(reminder);
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static ReminderCreateResult Invalid(string error) => new(null, false, error);

    /// <summary>Accepts "HH:mm" (the dialog) and "HH:mm:ss"; defaults to 09:00 when absent.</summary>
    private static TimeOnly? ParseNotifyAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new TimeOnly(9, 0);
        }

        return TimeOnly.TryParse(value.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
