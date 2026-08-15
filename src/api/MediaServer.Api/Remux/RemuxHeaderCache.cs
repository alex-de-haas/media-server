namespace MediaServer.Api.Remux;

/// <summary>
/// Keeps synthesised MP4 headers, because building one is not the cheap part it was assumed to be.
///
/// The index made playback cheap in the way it was designed to: reading a 12 MB index costs 80 ms and
/// laying out the sample tables another 96 ms. What nobody counted was that the synthesiser still opens
/// the <em>film</em>. Subtitle text is rewritten rather than referenced, so every cue is read from the
/// source; an E-AC-3 track is probed at sixty-four places to confirm its frame size does not vary. A
/// film with nine subtitle tracks costs some eighteen thousand scattered reads across thirty gigabytes —
/// and it paid them again on every byte-range request, of which a player makes one after another for as
/// long as it is playing. On a spinning disk that is not slow, it is stopped.
///
/// The header is a pure function of the source, the tracks chosen and the signalling asked for, so it
/// can simply be kept. The key is the same string the ETag is built from — it already carries every
/// file's length and modification time, so a replaced dub or an edited subtitle file lands on a
/// different entry rather than a stale one.
///
/// This does not make the *first* request cheap; only every one after it. Moving the subtitle text and
/// the audio frame size into the index, which is built in the background precisely so that playback
/// waits for nothing, is the repair for that — see <c>docs/features/remux-streaming/plan.md</c>.
/// </summary>
internal sealed class RemuxHeaderCache(ILogger<RemuxHeaderCache> logger, long budget = RemuxHeaderCache.DefaultBudget)
{
    /// <summary>
    /// Enough for a few dozen films. A header is a couple of megabytes for a heavily-tracked title, and
    /// the machines this runs on have gigabytes to spare — but "no limit" is how a cache becomes a leak.
    /// </summary>
    internal const long DefaultBudget = 512L * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];
    private long _held;
    private long _tick;

    private sealed record Entry(Mp4Synthesizer.Result Result, long Bytes)
    {
        public long Used { get; set; }
    }

    internal Mp4Synthesizer.Result? Get(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return null;
            }

            entry.Used = ++_tick;
            return entry.Result;
        }
    }

    internal void Put(string key, Mp4Synthesizer.Result result)
    {
        var bytes = result.Header.LongLength + result.Wrappers.Sum(wrapper => wrapper.LongLength);

        lock (_gate)
        {
            if (_entries.ContainsKey(key))
            {
                return;
            }

            _entries[key] = new Entry(result, bytes) { Used = ++_tick };
            _held += bytes;
            Evict();
        }
    }

    /// <summary>Least recently used first, which for this is least recently watched.</summary>
    private void Evict()
    {
        while (_held > budget && _entries.Count > 1)
        {
            var oldest = _entries.MinBy(entry => entry.Value.Used);
            _entries.Remove(oldest.Key);
            _held -= oldest.Value.Bytes;
            logger.LogDebug("Evicted a cached remux header; {Held} bytes held.", _held);
        }
    }
}
