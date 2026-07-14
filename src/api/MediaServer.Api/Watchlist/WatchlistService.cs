using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Watchlist;

/// <summary>Outcome + payload of a watchlist mutation; <see cref="Error"/> maps to a 400.</summary>
public sealed record WatchlistAddResult(WatchlistItemDto? Item, bool Created, string? Error);

/// <summary>
/// Layer-1 tracking operations behind <c>/api/watchlist</c>: per-user entries over deduped global
/// <see cref="TrackedTitle"/>s, the calendar read, and the per-title forced refresh. Every operation is
/// scoped to the acting <see cref="AppUser"/>.
/// </summary>
public sealed class WatchlistService(
    MediaServerDbContext database,
    MediaServerSettings settings,
    IWatchlistSyncQueue syncQueue,
    WatchlistLibraryLinker libraryLinker,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<WatchlistItemDto>> ListAsync(int userId, CancellationToken cancellationToken)
    {
        var entries = await database.WatchlistEntries.AsNoTracking()
            .Include(entry => entry.TrackedTitle).ThenInclude(title => title!.Releases)
            .Where(entry => entry.AppUserId == userId)
            .OrderBy(entry => entry.CreatedAt)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return [];
        }

        var titleIds = entries.Select(entry => entry.TrackedTitleId).ToList();
        var reminders = await database.ReleaseReminders.AsNoTracking()
            .Where(reminder => reminder.AppUserId == userId && titleIds.Contains(reminder.TrackedTitleId))
            .ToListAsync(cancellationToken);
        var deliveredReminderIds = await database.ReminderDeliveries.AsNoTracking()
            .Where(delivery => reminders.Select(reminder => reminder.Id).Contains(delivery.ReminderId))
            .Select(delivery => delivery.ReminderId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var today = WatchlistScope.LocalDay(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone);
        var items = new List<WatchlistItemDto>(entries.Count);
        foreach (var entry in entries)
        {
            items.Add(ToItemDto(
                entry,
                entry.TrackedTitle!,
                reminders.Where(reminder => reminder.TrackedTitleId == entry.TrackedTitleId).ToList(),
                deliveredReminderIds,
                await libraryLinker.ComputeGapAsync(entry.TrackedTitle!, cancellationToken),
                today));
        }

        return items;
    }

    public async Task<WatchlistAddResult> AddAsync(int userId, AddWatchlistRequest request, CancellationToken cancellationToken)
    {
        if (Validate(request) is { } error)
        {
            return new WatchlistAddResult(null, false, error);
        }

        var now = timeProvider.GetUtcNow();
        var title = await ResolveOrCreateTitleAsync(
            request.ProviderRef, request.Kind, request.Title, request.Year, request.PosterUrl, cancellationToken);

        var entry = await database.WatchlistEntries
            .FirstOrDefaultAsync(candidate => candidate.AppUserId == userId && candidate.TrackedTitleId == title.Id, cancellationToken);
        var created = entry is null;
        if (entry is null)
        {
            entry = new WatchlistEntry
            {
                Id = Guid.NewGuid(),
                AppUserId = userId,
                TrackedTitleId = title.Id,
                MonitorScope = request.Kind == MediaKind.Series ? request.MonitorScope : null,
                MonitoredSeasons = request.MonitorScope == SeriesMonitorScope.Seasons ? request.MonitoredSeasons : null,
                RegionOverride = NormalizeRegion(request.RegionOverride),
                Note = request.Note,
                CreatedAt = now,
            };
            database.WatchlistEntries.Add(entry);
            await database.SaveChangesAsync(cancellationToken);

            // A newly added title syncs immediately so its dates appear at once.
            syncQueue.Enqueue(title.Id);
        }

        return new WatchlistAddResult(await ProjectOneAsync(entry, cancellationToken), created, null);
    }

    /// <summary>Updates scope/region/note; returns null when the entry isn't the user's. A scope or
    /// region change re-queues the sync so newly monitored seasons / regions materialize promptly.</summary>
    public async Task<WatchlistItemDto?> UpdateAsync(
        int userId, Guid entryId, UpdateWatchlistRequest request, CancellationToken cancellationToken)
    {
        var entry = await database.WatchlistEntries
            .Include(candidate => candidate.TrackedTitle)
            .FirstOrDefaultAsync(candidate => candidate.Id == entryId && candidate.AppUserId == userId, cancellationToken);
        if (entry is null)
        {
            return null;
        }

        var resync = false;
        if (request.SetMonitorScope == true && entry.TrackedTitle!.Kind == MediaKind.Series)
        {
            entry.MonitorScope = request.MonitorScope;
            entry.MonitoredSeasons = request.MonitorScope == SeriesMonitorScope.Seasons ? request.MonitoredSeasons : null;
            resync = true;
        }

        if (request.SetRegionOverride == true)
        {
            entry.RegionOverride = NormalizeRegion(request.RegionOverride);
            resync = true;
        }

        if (request.SetNote == true)
        {
            entry.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note;
        }

        await database.SaveChangesAsync(cancellationToken);
        if (resync)
        {
            syncQueue.Enqueue(entry.TrackedTitleId);
        }

        return await ProjectOneAsync(entry, cancellationToken);
    }

    /// <summary>
    /// Stops tracking: deletes the entry and the user's reminders on that title. A title nobody tracks
    /// anymore — and that isn't in the library — is cleaned up (its releases cascade).
    /// </summary>
    public async Task<bool> RemoveAsync(int userId, Guid entryId, CancellationToken cancellationToken)
    {
        var entry = await database.WatchlistEntries
            .FirstOrDefaultAsync(candidate => candidate.Id == entryId && candidate.AppUserId == userId, cancellationToken);
        if (entry is null)
        {
            return false;
        }

        var reminders = await database.ReleaseReminders
            .Where(reminder => reminder.AppUserId == userId && reminder.TrackedTitleId == entry.TrackedTitleId)
            .ToListAsync(cancellationToken);
        database.ReleaseReminders.RemoveRange(reminders);
        database.WatchlistEntries.Remove(entry);
        await database.SaveChangesAsync(cancellationToken);

        var orphaned = await database.TrackedTitles
            .FirstOrDefaultAsync(title => title.Id == entry.TrackedTitleId
                && title.MediaItemId == null
                && !database.WatchlistEntries.Any(other => other.TrackedTitleId == title.Id), cancellationToken);
        if (orphaned is not null)
        {
            database.TrackedTitles.Remove(orphaned);
            await database.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>Dated release events across the user's tracked titles within <c>[from, to]</c>.</summary>
    public async Task<IReadOnlyList<CalendarEventDto>> CalendarAsync(
        int userId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var entries = await database.WatchlistEntries.AsNoTracking()
            .Include(entry => entry.TrackedTitle).ThenInclude(title => title!.Releases)
            .Where(entry => entry.AppUserId == userId)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return [];
        }

        var reminderKeys = (await database.ReleaseReminders.AsNoTracking()
                .Where(reminder => reminder.AppUserId == userId && reminder.Active)
                .Select(reminder => new { reminder.TrackedTitleId, reminder.ReleaseType })
                .ToListAsync(cancellationToken))
            .Select(reminder => (reminder.TrackedTitleId, reminder.ReleaseType))
            .ToHashSet();

        var timeZone = timeProvider.LocalTimeZone;
        var events = new List<CalendarEventDto>();
        foreach (var entry in entries)
        {
            var title = entry.TrackedTitle!;
            var region = WatchlistReads.EffectiveRegion(entry, settings);
            foreach (var release in WatchlistReads.VisibleReleases(title, entry, region, timeZone)
                         .Where(release => release.Date >= from && release.Date <= to))
            {
                var hasReminder = reminderKeys.Contains((title.Id, release.Type));
                events.Add(new CalendarEventDto(
                    release.Id,
                    entry.Id,
                    title.Id,
                    title.Kind,
                    title.Title,
                    title.PosterUrl,
                    release.Type,
                    release.Date,
                    release.PreviousDate,
                    release.Season,
                    release.Episode,
                    release.Note,
                    hasReminder,
                    title.MediaItemId is not null));
            }
        }

        return events
            .OrderBy(item => item.Date).ThenBy(item => item.Title)
            .ThenBy(item => item.Season).ThenBy(item => item.Episode)
            .ToList();
    }

    /// <summary>Queues an immediate forced date-sync for the entry's title. False when not the user's entry.</summary>
    public async Task<bool> RefreshAsync(int userId, Guid entryId, CancellationToken cancellationToken)
    {
        var titleId = await database.WatchlistEntries.AsNoTracking()
            .Where(entry => entry.Id == entryId && entry.AppUserId == userId)
            .Select(entry => (Guid?)entry.TrackedTitleId)
            .FirstOrDefaultAsync(cancellationToken);
        if (titleId is null)
        {
            return false;
        }

        syncQueue.Enqueue(titleId.Value);
        return true;
    }

    /// <summary>Resolve-or-create the global tracked title by canonical identity (deduped across users).</summary>
    public async Task<TrackedTitle> ResolveOrCreateTitleAsync(
        ProviderRefBody providerRef, MediaKind kind, string? title, int? year, string? posterUrl,
        CancellationToken cancellationToken)
    {
        var provider = providerRef.Provider.Trim().ToLowerInvariant();
        var providerId = providerRef.Id.Trim();
        var existing = await database.TrackedTitles.FirstOrDefaultAsync(
            candidate => candidate.IdentityProvider == provider && candidate.IdentityProviderId == providerId,
            cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = timeProvider.GetUtcNow();
        var tracked = new TrackedTitle
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            IdentityProvider = provider,
            IdentityProviderId = providerId,
            Providers = new Dictionary<string, string> { [provider] = providerId },
            Title = title?.Trim() ?? string.Empty,
            Year = year,
            PosterUrl = posterUrl,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Wishlist-vs-library: the canonical identity may already be published — link right on add.
        await libraryLinker.TryLinkAsync(tracked, cancellationToken);
        database.TrackedTitles.Add(tracked);
        await database.SaveChangesAsync(cancellationToken);
        return tracked;
    }

    private async Task<WatchlistItemDto> ProjectOneAsync(WatchlistEntry entry, CancellationToken cancellationToken)
    {
        var title = await database.TrackedTitles.AsNoTracking()
            .Include(candidate => candidate.Releases)
            .FirstAsync(candidate => candidate.Id == entry.TrackedTitleId, cancellationToken);
        var reminders = await database.ReleaseReminders.AsNoTracking()
            .Where(reminder => reminder.AppUserId == entry.AppUserId && reminder.TrackedTitleId == entry.TrackedTitleId)
            .ToListAsync(cancellationToken);
        var deliveredReminderIds = await database.ReminderDeliveries.AsNoTracking()
            .Where(delivery => reminders.Select(reminder => reminder.Id).Contains(delivery.ReminderId))
            .Select(delivery => delivery.ReminderId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var today = WatchlistScope.LocalDay(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone);
        var gap = await libraryLinker.ComputeGapAsync(title, cancellationToken);
        return ToItemDto(entry, title, reminders, deliveredReminderIds, gap, today);
    }

    private WatchlistItemDto ToItemDto(
        WatchlistEntry entry,
        TrackedTitle title,
        List<ReleaseReminder> reminders,
        List<Guid> deliveredReminderIds,
        LibraryGapDto? libraryGap,
        DateOnly today)
    {
        var timeZone = timeProvider.LocalTimeZone;
        var region = WatchlistReads.EffectiveRegion(entry, settings);
        var hasDates = WatchlistReads.VisibleReleases(title, entry, region, timeZone).Any()
            || (title.Kind == MediaKind.Series && (title.NextAirDate is not null || title.LastAiredDate is not null));

        return new WatchlistItemDto(
            entry.Id,
            title.Id,
            title.Kind,
            title.Title,
            title.Year,
            title.PosterUrl,
            title.IdentityProvider,
            title.IdentityProviderId,
            title.ProductionStatus,
            title.MediaItemId is not null,
            title.MediaItemId,
            entry.MonitorScope,
            entry.MonitoredSeasons,
            entry.RegionOverride,
            entry.Note,
            WatchlistReads.NextRelease(title, entry, region, today, timeZone),
            hasDates,
            libraryGap,
            reminders
                .Select(reminder => WatchlistReads.ToReminderDto(
                    reminder, title, entry, settings, today, timeZone, deliveredReminderIds.Contains(reminder.Id)))
                .ToList());
    }

    private static string? Validate(AddWatchlistRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProviderRef?.Provider) || string.IsNullOrWhiteSpace(request.ProviderRef?.Id))
        {
            return "A provider reference (provider + id) is required.";
        }

        if (request.Kind is not (MediaKind.Movie or MediaKind.Series))
        {
            return "Only a movie or series can be tracked.";
        }

        if (request.Kind == MediaKind.Movie && request.MonitorScope is not null)
        {
            return "Episode monitoring applies only to series.";
        }

        if (request.MonitorScope == SeriesMonitorScope.Seasons && request.MonitoredSeasons is not { Count: > 0 })
        {
            return "Choose at least one season to monitor.";
        }

        if (request.RegionOverride is { Length: > 0 } region && !IsRegion(region))
        {
            return "The region override must be a 2-letter ISO-3166-1 code.";
        }

        return null;
    }

    internal static bool IsRegion(string value) =>
        value.Trim().Length == 2 && value.Trim().All(char.IsAsciiLetter);

    private static string? NormalizeRegion(string? region) =>
        string.IsNullOrWhiteSpace(region) || !IsRegion(region) ? null : region.Trim().ToUpperInvariant();
}
