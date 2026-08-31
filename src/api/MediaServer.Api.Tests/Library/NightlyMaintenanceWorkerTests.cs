using MediaServer.Api.Library;

namespace MediaServer.Api.Tests.Library;

public sealed class NightlyMaintenanceWorkerTests
{
    [Fact]
    public void Waits_until_tonight_when_the_hour_is_still_ahead()
    {
        var now = new DateTimeOffset(2026, 8, 31, 21, 30, 0, TimeSpan.FromHours(2));

        Assert.Equal(TimeSpan.FromHours(5.5), NightlyMaintenanceWorker.DelayUntilNextRun(now));
    }

    [Fact]
    public void Waits_for_tomorrow_once_the_hour_has_passed()
    {
        var now = new DateTimeOffset(2026, 8, 31, 3, 15, 0, TimeSpan.FromHours(2));

        Assert.Equal(TimeSpan.FromHours(23.75), NightlyMaintenanceWorker.DelayUntilNextRun(now));
    }

    [Fact]
    public void A_start_exactly_on_the_hour_waits_a_full_day_rather_than_running_twice()
    {
        var now = new DateTimeOffset(2026, 8, 31, 3, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal(TimeSpan.FromDays(1), NightlyMaintenanceWorker.DelayUntilNextRun(now));
    }
}
