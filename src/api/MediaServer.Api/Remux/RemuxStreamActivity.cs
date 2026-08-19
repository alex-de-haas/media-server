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

    /// <summary>Time since this source's previous response ended, or null when there was not one.</summary>
    internal TimeSpan? Opening(string key)
    {
        lock (_gate)
        {
            return _lastClosed.TryGetValue(key, out var closed)
                ? Stopwatch.GetElapsedTime(closed)
                : null;
        }
    }

    internal void Closed(string key)
    {
        lock (_gate)
        {
            _lastClosed[key] = Stopwatch.GetTimestamp();
        }
    }
}
