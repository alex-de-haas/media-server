namespace MediaServer.Api.Watchlist;

/// <summary>
/// Resolves the moment a reminder fires for a release date: <c>(Date − LeadDays)</c> at <c>NotifyAt</c> in
/// the app timezone. Shared by the dispatch loop and the reminder-create resolved-state response.
/// </summary>
public static class ReminderTiming
{
    public static DateTimeOffset FireAt(DateOnly releaseDate, int leadDays, TimeOnly notifyAt, TimeZoneInfo timeZone)
    {
        var local = releaseDate.AddDays(-leadDays).ToDateTime(notifyAt, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }
}
