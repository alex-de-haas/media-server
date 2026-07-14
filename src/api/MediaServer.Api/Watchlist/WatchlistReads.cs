using MediaServer.Api.Configuration;
using MediaServer.Api.Data;

namespace MediaServer.Api.Watchlist;

/// <summary>
/// Shared read-side projection rules for the watchlist surface: which release rows an entry actually
/// sees (region for movies, monitor scope — or the title-level next/last episode when tracking is off —
/// for series), and how a reminder resolves to a display state / create-time resolution.
/// </summary>
internal static class WatchlistReads
{
    public static string EffectiveRegion(WatchlistEntry? entry, MediaServerSettings settings) =>
        string.IsNullOrWhiteSpace(entry?.RegionOverride) ? settings.WatchRegion : entry.RegionOverride.ToUpperInvariant();

    /// <summary>The release rows this entry surfaces (calendar + next-date resolution).</summary>
    public static IEnumerable<TrackedRelease> VisibleReleases(
        TrackedTitle title, WatchlistEntry entry, string region, TimeZoneInfo timeZone) =>
        title.Kind == MediaKind.Movie
            ? title.Releases.Where(release =>
                release.Region is not null && string.Equals(release.Region, region, StringComparison.OrdinalIgnoreCase))
            : VisibleEpisodes(title, entry, timeZone);

    /// <summary>
    /// Episode rows the entry sees: its monitor scope when episode tracking is on; with tracking off,
    /// only the title-level next/last episode (the "when it returns" signal) — never rows another user's
    /// scope happened to materialize.
    /// </summary>
    public static IEnumerable<TrackedRelease> VisibleEpisodes(TrackedTitle title, WatchlistEntry entry, TimeZoneInfo timeZone)
    {
        var episodes = title.Releases.Where(release => release is { Type: ReleaseType.EpisodeAir, Season: not null });
        if (entry.MonitorScope is not null)
        {
            return episodes.Where(release => WatchlistScope.Covers(entry, release.Season!.Value, release.Date, timeZone));
        }

        return episodes.Where(release =>
            (release.Season == title.NextAirSeason && release.Episode == title.NextAirEpisode)
            || (release.Season == title.LastAiredSeason && release.Episode == title.LastAiredEpisode));
    }

    public static NextReleaseDto? NextRelease(TrackedTitle title, WatchlistEntry entry, string region, DateOnly today, TimeZoneInfo timeZone)
    {
        var next = VisibleReleases(title, entry, region, timeZone)
            .Where(release => release.Date >= today)
            .OrderBy(release => release.Date).ThenBy(release => release.Season).ThenBy(release => release.Episode)
            .FirstOrDefault();
        if (next is not null)
        {
            return new NextReleaseDto(next.Type, next.Date, next.Season, next.Episode);
        }

        // A series between seasons may know its return date only at the title level.
        return title.Kind == MediaKind.Series && title.NextAirDate is { } airDate && airDate >= today
            ? new NextReleaseDto(ReleaseType.EpisodeAir, airDate, title.NextAirSeason, title.NextAirEpisode)
            : null;
    }

    public static ReminderDto ToReminderDto(
        ReleaseReminder reminder, TrackedTitle title, WatchlistEntry? entry, MediaServerSettings settings,
        DateOnly today, TimeZoneInfo timeZone, bool hasDelivery)
    {
        var (state, date) = PillState(reminder, title, entry, settings, today, timeZone, hasDelivery);
        return new ReminderDto(
            reminder.Id,
            reminder.TrackedTitleId,
            title.Title,
            title.PosterUrl,
            title.Kind,
            reminder.ReleaseType,
            reminder.LeadDays,
            reminder.NotifyAt.ToString("HH\\:mm"),
            reminder.Active,
            state,
            date);
    }

    /// <summary>Drawer pill: scheduled (movie, date ahead) / recurring (series, still airing or returning)
    /// / released (nothing left ahead) / pending (no date known yet).</summary>
    public static (string State, DateOnly? Date) PillState(
        ReleaseReminder reminder, TrackedTitle title, WatchlistEntry? entry, MediaServerSettings settings,
        DateOnly today, TimeZoneInfo timeZone, bool hasDelivery)
    {
        if (reminder.ReleaseType == ReleaseType.EpisodeAir)
        {
            var nextDate = title.NextAirDate is { } airDate && airDate >= today ? airDate : NextCoveredEpisodeDate(title, entry, today, timeZone);
            if (nextDate is not null)
            {
                return ("recurring", nextDate);
            }

            if (ReminderDispatchService.IsOver(title.ProductionStatus) || !reminder.Active)
            {
                return ("released", title.LastAiredDate);
            }

            // No date ahead but the show isn't over: recurring while it has aired before, pending otherwise.
            return title.LastAiredDate is null ? ("pending", null) : ("recurring", null);
        }

        var rows = title.Releases
            .Where(release => release.Type == reminder.ReleaseType
                && string.Equals(release.Region, EffectiveRegion(entry, settings), StringComparison.OrdinalIgnoreCase))
            .ToList();
        var future = rows.Where(release => release.Date >= today).OrderBy(release => release.Date).FirstOrDefault();
        if (future is not null && !hasDelivery)
        {
            return ("scheduled", future.Date);
        }

        var past = rows.Where(release => release.Date < today).OrderByDescending(release => release.Date).FirstOrDefault();
        return past is not null || hasDelivery
            ? ("released", past?.Date ?? future?.Date)
            : ("pending", null);
    }

    private static DateOnly? NextCoveredEpisodeDate(TrackedTitle title, WatchlistEntry? entry, DateOnly today, TimeZoneInfo timeZone)
    {
        if (entry is null)
        {
            return null;
        }

        return VisibleEpisodes(title, entry, timeZone)
            .Where(release => release.Date >= today)
            .OrderBy(release => release.Date)
            .Select(release => (DateOnly?)release.Date)
            .FirstOrDefault();
    }

    /// <summary>
    /// The create-time resolution: scheduled with the known future date; alreadyReleased with the passed
    /// date; pending while no date of that type is known. Series resolve from the title-level
    /// next/last-episode snapshot (never from prunable episode rows), summarizing pre-existing aired
    /// episodes instead of delivering them retroactively.
    /// </summary>
    public static (string State, DateOnly? Date, string? Detail) ResolveCreateState(
        ReleaseType type, TrackedTitle title, WatchlistEntry? entry, MediaServerSettings settings, DateOnly today)
    {
        if (type == ReleaseType.EpisodeAir)
        {
            var airedDetail = title.LastAiredDate is not null
                ? $"Already airing — up to S{title.LastAiredSeason}E{title.LastAiredEpisode} ({title.LastAiredDate:MMM d, yyyy})."
                : null;

            if (title.NextAirDate is { } nextDate && nextDate >= today)
            {
                return (ReminderResolutionDto.Scheduled, nextDate, airedDetail);
            }

            if (title.LastAiredDate is not null)
            {
                return ReminderDispatchService.IsOver(title.ProductionStatus)
                    ? (ReminderResolutionDto.AlreadyReleased, title.LastAiredDate, airedDetail)
                    : (ReminderResolutionDto.Pending, null, airedDetail);
            }

            return (ReminderResolutionDto.Pending, null, null);
        }

        var region = EffectiveRegion(entry, settings);
        var rows = title.Releases
            .Where(release => release.Type == type && string.Equals(release.Region, region, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var future = rows.Where(release => release.Date >= today).OrderBy(release => release.Date).FirstOrDefault();
        if (future is not null)
        {
            return (ReminderResolutionDto.Scheduled, future.Date, null);
        }

        var past = rows.OrderByDescending(release => release.Date).FirstOrDefault();
        return past is not null
            ? (ReminderResolutionDto.AlreadyReleased, past.Date, null)
            : (ReminderResolutionDto.Pending, null, null);
    }

    /// <summary>Per-kind valid reminder types: movie → Premiere/Theatrical/Digital, series → EpisodeAir.</summary>
    public static bool IsValidTypeForKind(MediaKind kind, ReleaseType type) => kind switch
    {
        MediaKind.Movie => type is ReleaseType.Premiere or ReleaseType.Theatrical or ReleaseType.Digital,
        MediaKind.Series => type is ReleaseType.EpisodeAir,
        _ => false,
    };
}
