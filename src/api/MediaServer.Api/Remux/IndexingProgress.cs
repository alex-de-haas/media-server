using MediaServer.Api.Realtime;

namespace MediaServer.Api.Remux;

/// <summary>A revisioned snapshot of one file's preparation; readiness does not imply codec support.</summary>
public sealed record IndexingStatus(string State, int? Percent, long Revision);

/// <summary>Shared payload for the web and native SSE routes. No filesystem paths leave the server.</summary>
public sealed record IndexingEvent(Guid ItemId, Guid SourceId, Guid? StreamId, IndexingStatus Indexing);

/// <summary>
/// Bounded, process-local progress. The store remains the authority for ready indexes; a restart
/// rediscovers waiting files. Revisions also order snapshot responses against concurrent SSE events.
/// </summary>
public sealed class IndexingProgress(RemuxIndexStore store, SseRealtimeNotifier notifier, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private long _revision = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
    private const int Capacity = 512;

    private sealed record Entry(RemuxIndexService.Candidate Candidate, long Length, DateTime Modified,
        IndexingStatus Status, long PublishedAt);

    /// <summary>Reads the file's current status, returning null for unavailable or ineligible files.</summary>
    public IndexingStatus? Read(Guid key, string path, string? container)
    {
        if (!RemuxIndexService.IsIndexable(container)) return null;
        try
        {
            lock (_gate)
            {
                var file = new FileInfo(path);
                if (!file.Exists) return null;
                if (store.IsCurrent(key, path)) return new("ready", null, _revision);
                if (_entries.TryGetValue(key, out var entry))
                {
                    if (entry.Length == file.Length && entry.Modified == file.LastWriteTimeUtc)
                        return entry.Status;
                    // Keep the attempt until the builder reports its terminal state. A snapshot read
                    // must not swallow the cancellation/failure event other connected viewers need.
                }
                return new("waiting", null, _revision);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Files can disappear or become inaccessible between the existence check and stat.
            return null;
        }
    }

    internal void Start(RemuxIndexService.Candidate candidate)
    {
        lock (_gate)
        {
            var file = new FileInfo(candidate.AbsolutePath);
            if (_entries.Count >= Capacity)
                _entries.Remove(_entries.MinBy(pair => pair.Value.Status.Revision).Key);
            _entries[candidate.Key] = new(candidate, file.Length, file.LastWriteTimeUtc,
                new("indexing", 0, ++_revision), clock.GetTimestamp());
            Publish(_entries[candidate.Key]);
        }
    }

    internal void Report(Guid key, long position, long length)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry) || entry.Status.State != "indexing") return;
            var percent = (int)Math.Clamp(length > 0 ? (double)position / length * 100 : 0, 0, 99);
            if (percent <= entry.Status.Percent || clock.GetElapsedTime(entry.PublishedAt) < TimeSpan.FromMilliseconds(500)) return;
            Change(entry, "indexing", percent);
        }
    }

    internal bool IsUnchanged(Guid key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return false;
            var file = new FileInfo(entry.Candidate.AbsolutePath);
            return file.Exists && file.Length == entry.Length && file.LastWriteTimeUtc == entry.Modified;
        }
    }

    internal void Finish(Guid key, string state)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) return;
            Change(entry, state, null);
            if (state is "ready" or "waiting") _entries.Remove(key);
        }
    }

    private void Change(Entry entry, string state, int? percent)
    {
        entry = entry with { Status = new(state, percent, ++_revision), PublishedAt = clock.GetTimestamp() };
        _entries[entry.Candidate.Key] = entry;
        Publish(entry);
    }

    private void Publish(Entry entry)
    {
        var candidate = entry.Candidate;
        // Candidates only come from published, visible items. Legacy callers without ownership do not broadcast.
        if (candidate.ItemId != Guid.Empty)
            notifier.IndexingChanged(new(candidate.ItemId, candidate.SourceId, candidate.StreamId, entry.Status));
    }
}
