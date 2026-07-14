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

        if (timeZone.IsInvalidTime(local))
        {
            // Spring-forward gap (e.g. 02:30 on a day the clock jumps 02:00→03:00): shift past the gap
            // so the constructed instant is a real local time.
            var gap = timeZone.GetUtcOffset(local.AddDays(1)) - timeZone.GetUtcOffset(local.AddDays(-1));
            local = local.Add(gap);
        }

        // Fall-back overlap (the hour that occurs twice): GetUtcOffset would pick the second occurrence
        // (standard time); prefer the first so the reminder fires at the earliest matching wall clock.
        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
