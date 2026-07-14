using MediaServer.Api.Watchlist;

namespace MediaServer.Api.Tests.Watchlist;

public class ReminderTimingTests
{
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    [Fact]
    public void Fires_at_notify_time_in_the_zone_on_a_plain_day()
    {
        var fireAt = ReminderTiming.FireAt(new DateOnly(2026, 7, 16), leadDays: 2, new TimeOnly(9, 0), NewYork);

        Assert.Equal(new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.FromHours(-4)), fireAt);
    }

    [Fact]
    public void Invalid_local_time_in_the_spring_forward_gap_shifts_past_the_gap()
    {
        // 2026-03-08 02:30 never exists in New York (02:00 jumps to 03:00).
        var fireAt = ReminderTiming.FireAt(new DateOnly(2026, 3, 8), leadDays: 0, new TimeOnly(2, 30), NewYork);

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-4)), fireAt);
    }

    [Fact]
    public void Ambiguous_local_time_in_the_fall_back_overlap_picks_the_first_occurrence()
    {
        // 2026-11-01 01:30 happens twice in New York; the first pass is still on daylight time (-4).
        var fireAt = ReminderTiming.FireAt(new DateOnly(2026, 11, 1), leadDays: 0, new TimeOnly(1, 30), NewYork);

        Assert.Equal(TimeSpan.FromHours(-4), fireAt.Offset);
        Assert.Equal(new DateTime(2026, 11, 1, 1, 30, 0), fireAt.DateTime);
    }
}
