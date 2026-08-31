using System.Diagnostics;

namespace MediaServer.Api.Remux;

/// <summary>
/// How long a source sat with nobody reading it, between one response and the next.
///
/// The meter can only see time it is alive for, and the first production log made that a problem: every
/// line said the server had served its megabytes in a tenth of a second at several hundred megabits, and
/// none of them could say what happened in between. A film that needs 50 Mbit/s, served in bursts of 500,
/// is idle nine tenths of the time — and that idleness, not any rate, is then the whole story.
///
/// Keyed by source rather than by response, because that is the thing being watched. Entries are a
/// timestamp each and there is one per film being played, so nothing here needs evicting.
/// </summary>
internal sealed class RemuxStreamActivity
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _lastClosed = [];
    private int _open;

    /// <summary>
    /// How many responses for this source are open right now.
    ///
    /// A television stops asking for anything and never starts again, and restarting the *server* — not
    /// merely the film — brings it back for a while, which points at something that accumulates and is
    /// then swept away. Connections are the candidate: a client has a limit per host, and a player
    /// holding every one of them in a half-finished response has nothing left to ask with.
    ///
    /// If this climbs and stays climbed while the picture is frozen, that is the answer. If it sits at
    /// one or two throughout, the whole family is ruled out.
    /// </summary>
    internal int Open
    {
        get
        {
            lock (_gate)
            {
                return _open;
            }
        }
    }

    /// <summary>Time since this source's previous response ended, or null when there was not one.</summary>
    internal TimeSpan? Opening(string key)
    {
        lock (_gate)
        {
            _open++;
            return _lastClosed.TryGetValue(key, out var closed)
                ? Stopwatch.GetElapsedTime(closed)
                : null;
        }
    }

    internal void Closed(string key)
    {
        lock (_gate)
        {
            _open = Math.Max(0, _open - 1);
            _lastClosed[key] = Stopwatch.GetTimestamp();
        }
    }
}
