using System.Diagnostics;

namespace MediaServer.Api.Remux;

/// <summary>
/// Where a response's time actually went: getting bytes off the disk, or getting them onto the wire.
///
/// The client's own overlay can say the picture is arriving with no margin to spare. It cannot say which
/// half of this server is the reason, and the two want opposite repairs — a read path that seeks too much
/// is ours to fix, and a path to the television that cannot carry the film is not. The split here is
/// exact and costs a stopwatch: time spent inside a read of the synthesised stream is the disk, and time
/// between one read and the next is the framework writing what it just got to the socket.
///
/// **Periodic lines describe their own ten seconds, not the response so far.** The cadence exists to show
/// a bad stretch, and a lifetime average cannot: a five-second stall half an hour into a film moves the
/// running mean by nothing at all and rounds away to the same figure as the half hour before it.
///
/// One caveat, which is why this is read next to the client's buffer rather than alone: a player whose
/// buffer is full stops draining the socket, and that idleness is indistinguishable from a slow network.
/// The figure is only conclusive while the client is taking everything it can get — which, for every
/// measurement that has prompted this, it was.
///
/// Off unless <c>PLAYBACK_DIAGNOSTICS</c> is set. A film is thousands of reads a second and none of them
/// should pay for a stopwatch nobody is reading.
/// </summary>
/// <param name="activity">
/// Where the gaps between one response and the next are remembered. The meter is alive only while a
/// response is, so without this it can report a server that is fast and never that it is idle — and a
/// film delivered in bursts of half a gigabit is exactly a server that is idle.
/// </param>
/// <param name="clock">
/// Elapsed time since the response began. The real one by default; a stated one in tests, because every
/// figure here is a ratio of durations and a test that has to sleep to produce them measures the build
/// machine's mood rather than this arithmetic.
/// </param>
internal sealed class RemuxStreamMeter(
    ILogger logger,
    string label,
    RemuxStreamActivity? activity = null,
    Func<long, long, string>? whose = null,
    Func<TimeSpan>? clock = null)
{
    /// <summary>Often enough to see a stretch of film go bad, rare enough that a log stays readable.</summary>
    private static readonly TimeSpan Report = TimeSpan.FromSeconds(10);

    private static Func<TimeSpan> Started()
    {
        var watch = Stopwatch.StartNew();
        return () => watch.Elapsed;
    }

    private readonly Func<TimeSpan> _now = clock ?? Started();
    private readonly TimeSpan? _idle = activity?.Opening(label);
    private readonly Lock _gate = new();

    /// <summary>Disposal is allowed to happen twice; reporting a response twice is not.</summary>
    private bool _done;

    // The whole response, which is what the closing line is about.
    private TimeSpan _reading;
    private TimeSpan _writing;
    private long _bytes;
    private long _reads;

    // Since the last periodic line, which is what each periodic line is about.
    private TimeSpan _sinceReading;
    private TimeSpan _sinceWriting;
    private long _sinceBytes;
    private long _sinceReads;

    private TimeSpan _lastEnded;
    private TimeSpan _reported;

    // Where in the output this response actually read. A player that fetches ten times what it keeps is
    // either asking for the same bytes twice or reading far ahead and throwing it away, and those want
    // opposite repairs — the ranges are the only thing that tells them apart.
    //
    // Kept twice, for the same reason the byte counts are. A periodic line describes its own ten
    // seconds, and printing the whole response's span beside one window's bytes would make consecutive
    // windows appear to cover all the same ground — which reads as re-fetching, the very conclusion
    // these numbers exist to reach honestly.
    private long _from = -1;
    private long _to;
    private long _sinceFrom = -1;
    private long _sinceTo;

    /// <summary>
    /// What has been served so far. Exposed so a test can hold the meter to the stream it is measuring:
    /// a meter that misses reads, or counts one twice, reports a rate that is fiction — and the whole
    /// point of it is to be believed over a guess.
    /// </summary>
    internal (long Bytes, long Reads) Totals
    {
        get
        {
            lock (_gate)
            {
                return (_bytes, _reads);
            }
        }
    }

    /// <summary>Noted before a read and handed back to <see cref="Served"/> when it returns.</summary>
    internal TimeSpan Begin() => _now();

    internal void Served(TimeSpan began, int bytes) => Served(began, bytes, at: -1);

    /// <param name="at">Where in the output the read started, or -1 when the caller does not say.</param>
    internal void Served(TimeSpan began, int bytes, long at)
    {
        var ended = _now();

        lock (_gate)
        {
            if (at >= 0)
            {
                if (_from < 0)
                {
                    _from = at;
                }

                if (_sinceFrom < 0)
                {
                    _sinceFrom = at;
                }

                _to = Math.Max(_to, at + bytes);
                _sinceTo = Math.Max(_sinceTo, at + bytes);
            }

            var read = ended - began;
            _reading += read;
            _sinceReading += read;

            // Before the first read there is nothing to have been waiting for: opening the files and
            // finding the header is the request's cost, not this stream's.
            if (_reads > 0)
            {
                var wrote = began - _lastEnded;
                _writing += wrote;
                _sinceWriting += wrote;
            }

            _lastEnded = ended;
            _bytes += bytes;
            _sinceBytes += bytes;
            _reads++;
            _sinceReads++;

            if (ended - _reported >= Report)
            {
                Write("last 10s", _sinceBytes, ended - _reported, _sinceReading, _sinceWriting,
                    _sinceReads, _sinceFrom, _sinceTo);
                _reported = ended;
                _sinceReading = TimeSpan.Zero;
                _sinceWriting = TimeSpan.Zero;
                _sinceBytes = 0;
                _sinceReads = 0;
                _sinceFrom = -1;
                _sinceTo = 0;
            }
        }
    }

    /// <summary>The line that matters most: a whole response, however long the player kept it open.</summary>
    internal void Done()
    {
        lock (_gate)
        {
            // A stream is disposed by whoever finishes with it, and more than one thing does. Without
            // this every response was logged twice — the second line adding the closing interval to the
            // socket share a second time, so the figure the whole diagnostic exists for was overstated.
            if (_done || _reads == 0)
            {
                return;
            }

            _done = true;
            activity?.Closed(label);

            var elapsed = _now();

            // The stretch after the last read is socket time like any other, and it is the only stretch
            // there is for a response the player took in one go. Without it such a response reports
            // "socket 0%" — the most misleading answer this meter could give.
            _writing += elapsed - _lastEnded;

            Write("closed", _bytes, elapsed, _reading, _writing, _reads, _from, _to);
        }
    }

    private void Write(
        string window, long bytes, TimeSpan elapsed, TimeSpan reading, TimeSpan writing, long reads,
        long from, long to)
    {
        var seconds = elapsed.TotalSeconds;
        if (seconds <= 0 || reads == 0)
        {
            return;
        }

        logger.LogInformation(
            "Remux {Label} {Window}: {Megabytes:F1} MB in {Seconds:F2}s = {Mbps:F0} Mbit/s; "
            + "disk {Disk:F0}%, socket {Socket:F0}%; {Reads} reads of {Kilobytes:F0} KB; "
            + "idle {Idle} before it; bytes {Range}; carrying {Whose}.",
            label,
            window,
            bytes / 1_000_000d,
            seconds,
            bytes * 8 / seconds / 1_000_000,
            reading.TotalSeconds / seconds * 100,
            writing.TotalSeconds / seconds * 100,
            reads,
            bytes / (double)reads / 1000,
            _idle is { } idle ? $"{idle.TotalMilliseconds:F0} ms" : "nothing",
            from < 0 ? "unknown" : $"{from}-{to}",
            from < 0 || whose is null ? "not asked" : whose(from, to));
    }
}
