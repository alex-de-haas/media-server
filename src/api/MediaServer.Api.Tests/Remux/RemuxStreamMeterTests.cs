using MediaServer.Api.Remux;
using Microsoft.Extensions.Logging;

namespace MediaServer.Api.Tests.Remux;

/// <summary>
/// The meter that says where a slow response's time went.
///
/// Its lines decide whether the next repair belongs to this server's read path or to the path to the
/// television, and those want opposite work. Every figure in them is a ratio of durations, so the clock
/// is stated rather than slept through: a test that waits for real time measures the build machine's
/// mood and not this arithmetic.
/// </summary>
public sealed class RemuxStreamMeterTests
{
    private sealed class Recorder : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, error));
    }

    private sealed class StatedClock
    {
        public TimeSpan Now { get; private set; }

        public TimeSpan At(double seconds)
        {
            Now = TimeSpan.FromSeconds(seconds);
            return Now;
        }
    }

    [Fact]
    public void A_periodic_line_describes_its_own_ten_seconds_and_not_the_response_so_far()
    {
        // The cadence exists to show a bad stretch. Averaged over everything that came before, a stall
        // half an hour into a film moves the running mean by nothing and reports the same figure as the
        // half hour of health before it — which is the one answer that would send the diagnosis wrong.
        var log = new Recorder();
        var clock = new StatedClock();
        var meter = new RemuxStreamMeter(log, "film", clock: () => clock.Now);

        meter.Served(clock.At(0), 62_500_000);
        meter.Served(clock.At(10), 62_500_000);      // closes a fast ten seconds: 125 MB
        meter.Served(clock.At(20), 1_250_000);       // closes a starved one: 1.25 MB

        Assert.Equal(2, log.Lines.Count);
        Assert.Contains("= 100 Mbit/s", log.Lines[0]);
        Assert.Contains("= 1 Mbit/s", log.Lines[1]);
    }

    [Fact]
    public void The_stretch_after_the_last_read_is_socket_time_like_any_other()
    {
        // A response the player takes in one go has no gap *between* reads at all. Counting only those
        // gaps reports "socket 0%" for it — the most misleading answer this meter could give, since a
        // player stalled on the last chunk is exactly the case being hunted.
        var log = new Recorder();
        var clock = new StatedClock();
        var meter = new RemuxStreamMeter(log, "film", clock: () => clock.Now);

        var began = clock.At(0);
        clock.At(1);                                  // one second getting it off the disk
        meter.Served(began, 1_000_000);

        clock.At(5);                                  // four more waiting for the wire
        meter.Done();

        Assert.Single(log.Lines);
        Assert.Contains("disk 20%, socket 80%", log.Lines[0]);
    }

    [Fact]
    public void The_closing_line_is_the_whole_response_and_not_the_last_interval()
    {
        var log = new Recorder();
        var clock = new StatedClock();
        var meter = new RemuxStreamMeter(log, "film", clock: () => clock.Now);

        meter.Served(clock.At(0), 10_000_000);
        meter.Served(clock.At(10), 10_000_000);
        meter.Served(clock.At(20), 10_000_000);
        meter.Done();

        Assert.Equal(3, log.Lines.Count);
        Assert.Contains("closed", log.Lines[2]);
        Assert.Contains("30.0 MB", log.Lines[2]);
        Assert.Equal((30_000_000, 3), meter.Totals);
    }

    [Fact]
    public void A_response_is_reported_once_however_often_it_is_disposed()
    {
        // A stream is disposed by whoever finishes with it, and more than one thing does. Reporting
        // twice put a second line in the log whose socket share counted the closing interval again —
        // overstating the very figure the diagnostic exists to establish.
        var log = new Recorder();
        var clock = new StatedClock();
        var meter = new RemuxStreamMeter(log, "film", clock: () => clock.Now);

        meter.Served(clock.At(0), 1_000_000);
        clock.At(2);
        meter.Done();
        clock.At(9);
        meter.Done();

        Assert.Single(log.Lines);
    }

    [Fact]
    public void The_gap_since_the_previous_response_is_reported_with_the_next_one()
    {
        // A server that serves its megabytes in a tenth of a second and then waits is not a fast server
        // — it is an idle one, and only the gap says so.
        var log = new Recorder();
        var activity = new RemuxStreamActivity();
        var clock = new StatedClock();

        var first = new RemuxStreamMeter(log, "film", activity, () => clock.Now);
        var began = clock.At(0);
        clock.At(0.5);
        first.Served(began, 1_000_000);
        first.Done();

        var second = new RemuxStreamMeter(log, "film", activity, () => clock.Now);
        began = clock.At(1);
        clock.At(1.5);
        second.Served(began, 1_000_000);
        second.Done();

        Assert.Equal(2, log.Lines.Count);
        Assert.Contains("idle nothing before it", log.Lines[0]);
        Assert.Contains("idle ", log.Lines[1]);
        Assert.DoesNotContain("idle nothing", log.Lines[1]);
    }

    [Fact]
    public void The_range_a_response_read_is_reported_with_it()
    {
        // A player fetching ten times what it keeps is either asking for the same bytes twice or
        // reading far ahead and discarding, and those want opposite repairs. Only the ranges tell
        // them apart, so they have to be right.
        var log = new Recorder();
        var clock = new StatedClock();
        var meter = new RemuxStreamMeter(log, "film", clock: () => clock.Now);

        meter.Served(clock.At(0), 1_000, at: 5_000);
        meter.Served(clock.At(1), 1_000, at: 6_000);
        clock.At(2);
        meter.Done();

        Assert.Single(log.Lines);
        Assert.Contains("bytes 5000-7000", log.Lines[0]);
    }

    [Fact]
    public void A_periodic_line_names_the_range_of_its_own_window()
    {
        // Printing the whole response's span beside one window's bytes would make consecutive windows
        // appear to cover all the same ground — which reads as re-fetching, and re-fetching is the
        // conclusion these numbers exist to reach honestly rather than by accident.
        var log = new Recorder();
        var clock = new StatedClock();
        var meter = new RemuxStreamMeter(log, "film", clock: () => clock.Now);

        meter.Served(clock.At(0), 1_000, at: 0);
        meter.Served(clock.At(10), 1_000, at: 1_000);      // closes the first window
        meter.Served(clock.At(20), 1_000, at: 500_000);    // closes the second, somewhere else entirely
        clock.At(21);
        meter.Done();

        Assert.Equal(3, log.Lines.Count);
        Assert.Contains("bytes 0-2000", log.Lines[0]);
        Assert.Contains("bytes 500000-501000", log.Lines[1]);
        Assert.Contains("bytes 0-501000", log.Lines[2]);   // the closing line is the whole response
    }

    [Fact]
    public void A_response_whose_offsets_nobody_stated_says_so_rather_than_inventing_one()
    {
        var log = new Recorder();
        var clock = new StatedClock();
        var meter = new RemuxStreamMeter(log, "film", clock: () => clock.Now);

        meter.Served(clock.At(0), 1_000);
        clock.At(1);
        meter.Done();

        Assert.Contains("bytes unknown", log.Lines[0]);
    }

    [Fact]
    public void A_response_that_never_read_anything_says_nothing()
    {
        var log = new Recorder();
        var meter = new RemuxStreamMeter(log, "film", clock: () => TimeSpan.FromSeconds(5));

        meter.Done();

        Assert.Empty(log.Lines);
    }
}
