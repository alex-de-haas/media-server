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
/// One caveat, and it is why this is read next to the client's buffer rather than alone: a player whose
/// buffer is full stops draining the socket, and that idleness is indistinguishable from a slow network.
/// The figure is only conclusive while the client is taking everything it can get — which, for every
/// measurement that has prompted this, it was.
///
/// Off unless <c>PLAYBACK_DIAGNOSTICS</c> is set. A film is thousands of reads a second and none of them
/// should pay for a stopwatch nobody is reading.
/// </summary>
internal sealed class RemuxStreamMeter(ILogger logger, string label)
{
    /// <summary>Often enough to see a stretch of film go bad, rare enough that a log stays readable.</summary>
    private static readonly TimeSpan Report = TimeSpan.FromSeconds(10);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _gate = new();

    private TimeSpan _reading;
    private TimeSpan _writing;
    private TimeSpan _lastEnded;
    private TimeSpan _reported;
    private long _bytes;
    private long _reads;

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
    internal TimeSpan Begin() => _clock.Elapsed;

    internal void Served(TimeSpan began, int bytes)
    {
        var ended = _clock.Elapsed;

        lock (_gate)
        {
            _reading += ended - began;

            // Before the first read there is nothing to have been waiting for: opening the files and
            // finding the header is the request's cost, not this stream's.
            if (_reads > 0)
            {
                _writing += began - _lastEnded;
            }

            _lastEnded = ended;
            _bytes += bytes;
            _reads++;

            if (ended - _reported >= Report)
            {
                _reported = ended;
                Write(ended, "");
            }
        }
    }

    /// <summary>The line that matters most: a whole response, however long the player kept it open.</summary>
    internal void Done()
    {
        lock (_gate)
        {
            if (_reads > 0)
            {
                Write(_clock.Elapsed, " (closed)");
            }
        }
    }

    private void Write(TimeSpan elapsed, string suffix)
    {
        var seconds = elapsed.TotalSeconds;
        if (seconds <= 0)
        {
            return;
        }

        logger.LogInformation(
            "Remux {Label}{Suffix}: {Megabytes:F0} MB in {Seconds:F0}s = {Mbps:F0} Mbit/s; "
            + "disk {Disk:F0}%, socket {Socket:F0}%; {Reads} reads of {Kilobytes:F0} KB.",
            label,
            suffix,
            _bytes / 1_000_000d,
            seconds,
            _bytes * 8 / seconds / 1_000_000,
            _reading.TotalSeconds / seconds * 100,
            _writing.TotalSeconds / seconds * 100,
            _reads,
            _reads == 0 ? 0 : _bytes / (double)_reads / 1000);
    }
}
