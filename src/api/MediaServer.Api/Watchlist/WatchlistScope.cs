using MediaServer.Api.Data;

namespace MediaServer.Api.Watchlist;

/// <summary>
/// Shared evaluation of a series entry's <see cref="SeriesMonitorScope"/> against a concrete episode,
/// used by the date-sync loop (which episode rows exist / are spared from pruning), the dispatch loop
/// (which episodes a reminder fires for), and the calendar read.
/// </summary>
public static class WatchlistScope
{
    /// <summary>
    /// Whether the entry's episode tracking covers the given episode. <c>null</c> scope = tracking off —
    /// covers nothing. <see cref="SeriesMonitorScope.FutureEpisodes"/> covers episodes airing on/after the
    /// day tracking was created (<paramref name="entryCreatedDay"/> resolved in the app timezone).
    /// </summary>
    public static bool Covers(
        SeriesMonitorScope? scope,
        IReadOnlyCollection<int>? monitoredSeasons,
        DateOnly entryCreatedDay,
        int season,
        DateOnly airDate) => scope switch
    {
        SeriesMonitorScope.WholeShow => true,
        SeriesMonitorScope.Seasons => monitoredSeasons?.Contains(season) == true,
        SeriesMonitorScope.FutureEpisodes => airDate >= entryCreatedDay,
        _ => false,
    };

    public static bool Covers(WatchlistEntry entry, int season, DateOnly airDate, TimeZoneInfo timeZone) =>
        Covers(entry.MonitorScope, entry.MonitoredSeasons, LocalDay(entry.CreatedAt, timeZone), season, airDate);

    /// <summary>The calendar day of an instant in the app timezone.</summary>
    public static DateOnly LocalDay(DateTimeOffset instant, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);
}
