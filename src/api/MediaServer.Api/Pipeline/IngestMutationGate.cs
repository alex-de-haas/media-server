using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Pipeline;

/// <summary>
/// Serializes mutations of one download, without blocking unrelated transfers or read-only analysis.
/// Ingest callers also hold a stable ingest lock while its download association is removed by cleanup.
/// Acquire before loading mutable entities; download callers must not acquire an ingest lock afterwards.
/// </summary>
public static class IngestMutationGate
{
    private static readonly Dictionary<(bool Ingest, Guid Id), Entry> Entries = new();

    public static Task<IDisposable> EnterAsync(Guid downloadId, CancellationToken ct) =>
        EnterKeyAsync((false, downloadId), ct);

    public static async Task<IDisposable> EnterIngestAsync(MediaServerDbContext database, Guid ingestId, CancellationToken ct)
    {
        var ingest = await EnterKeyAsync((true, ingestId), ct);
        try
        {
            var downloadId = await database.IngestItems.AsNoTracking().Where(x => x.Id == ingestId)
                .Select(x => x.DownloadId).FirstOrDefaultAsync(ct);
            var download = downloadId is { } id ? await EnterAsync(id, ct) : null;
            return new IngestLease(ingest, download);
        }
        catch { ingest.Dispose(); throw; }
    }

    private static async Task<IDisposable> EnterKeyAsync((bool Ingest, Guid Id) key, CancellationToken ct)
    {
        Entry entry;
        lock (Entries)
        {
            if (!Entries.TryGetValue(key, out entry!)) Entries.Add(key, entry = new Entry());
            entry.References++;
        }
        try { await entry.Gate.WaitAsync(ct); }
        catch { DropReference(key, entry); throw; }
        return new Lease(key, entry);
    }

    private static void DropReference((bool Ingest, Guid Id) key, Entry entry)
    {
        lock (Entries)
        {
            if (--entry.References == 0)
            {
                Entries.Remove(key);
                entry.Gate.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int References;
    }

    private sealed class Lease((bool Ingest, Guid Id) key, Entry entry) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            entry.Gate.Release();
            DropReference(key, entry);
        }
    }

    private sealed class IngestLease(IDisposable ingest, IDisposable? download) : IDisposable
    {
        public void Dispose() { download?.Dispose(); ingest.Dispose(); }
    }
}
