using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>A source used by a join cannot move or disappear before the engine releases it.</summary>
public sealed class LibraryFileBusyException() : Exception("This file is used by a join. Wait for it to finish, or cancel the join first.");

/// <summary>Serializes join admission with destructive library mutations. Persisted jobs hold the long-lived reservation.</summary>
public static class LibraryFileMutation
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        public void Dispose() => Gate.Release();
    }

    private static IQueryable<TranscodeJob> ActiveJoins(MediaServerDbContext db) => db.TranscodeJobs.Where(job =>
        job.Kind == TranscodeJobKind.Join && (job.State == TranscodeJobState.Queued || job.State == TranscodeJobState.Running ||
            job.State == TranscodeJobState.Completed && !job.OutputImported));

    public static Task<bool> HasJoinAsync(MediaServerDbContext db, Guid itemId, CancellationToken ct) =>
        ActiveJoins(db).AnyAsync(job => job.MediaItemId == itemId, ct);

    public static async Task RequireCatalogAvailableAsync(MediaServerDbContext db, Guid catalogId, CancellationToken ct)
    {
        if (await ActiveJoins(db).AnyAsync(job => job.CatalogId == catalogId, ct))
            throw new LibraryFileBusyException();
    }

    public static async Task RequireItemAvailableAsync(MediaServerDbContext db, Guid itemId, CancellationToken ct)
    {
        if (await HasJoinAsync(db, itemId, ct)) throw new LibraryFileBusyException();
    }

    public static async Task RequireSourceAvailableAsync(MediaServerDbContext db, Guid sourceId, CancellationToken ct)
    {
        if (await ActiveJoins(db).AnyAsync(job => job.MediaSourceId == sourceId || job.SecondSourceId == sourceId, ct))
            throw new LibraryFileBusyException();
    }
}
