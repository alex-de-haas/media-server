using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Native;

/// <summary>
/// Serves the delta feed a native client mirrors the library from. A client holds a local copy and
/// browses from it, so a screen costs no round-trip — the biggest difference from a Jellyfin client,
/// which re-queries per screen.
///
/// See <c>docs/features/native-client-api/plan.md</c>.
/// </summary>
public sealed class NativeSyncService(MediaServerDbContext database, LibraryReadService library)
{
    /// <summary>
    /// Bounded, so a first sync against a large library is many small answers rather than one
    /// enormous one a phone has to hold in memory.
    /// </summary>
    public const int PageSize = 200;

    public async Task<NativeSyncPage> SyncAsync(string? cursor, int appUserId, CancellationToken cancellationToken)
    {
        if (!NativeSyncCursor.TryDecode(cursor, out var position))
        {
            // No cursor, or one we cannot read: start a fresh snapshot rather than guessing.
            return await SnapshotAsync(NativeSyncCursor.StartSnapshot(await HighWatermarkAsync(cancellationToken)),
                appUserId, cancellationToken);
        }

        return position.Mode switch
        {
            NativeSyncMode.Snapshot => await SnapshotAsync(position, appUserId, cancellationToken),
            _ => await DeltaAsync(position, appUserId, cancellationToken),
        };
    }

    private async Task<long> HighWatermarkAsync(CancellationToken cancellationToken) =>
        await database.ChangeLog.AsNoTracking().MaxAsync(entry => (long?)entry.Sequence, cancellationToken) ?? 0;

    private async Task<NativeSyncPage> SnapshotAsync(
        NativeSyncCursor position, int appUserId, CancellationToken cancellationToken)
    {
        // Keyset paging on the primary key: stable under concurrent inserts, and it never re-reads a
        // page the way an offset would when rows shift underneath.
        var after = Guid.TryParseExact(position.Position, "N", out var parsed) ? parsed : Guid.Empty;

        var items = await database.MediaItems.AsNoTracking()
            .Where(item => item.PublicId != null && item.RemovedAt == null && item.Id.CompareTo(after) > 0)
            .OrderBy(item => item.Id)
            .Take(PageSize)
            .ToListAsync(cancellationToken);

        var projected = await library.ProjectCardsAsync(items, appUserId, cancellationToken);

        // The snapshot is done when a page comes back short; from then on the client rides the log
        // from the watermark captured when it started.
        var next = items.Count < PageSize
            ? NativeSyncCursor.Delta(position.Watermark)
            : position with { Position = items[^1].Id.ToString("N") };

        return new NativeSyncPage(
            Items: projected,
            RemovedIds: [],
            ChangedPreferenceScopes: [],
            Cursor: next.Encode(),
            HasMore: items.Count == PageSize,
            ResetRequired: false);
    }

    private async Task<NativeSyncPage> DeltaAsync(
        NativeSyncCursor position, int appUserId, CancellationToken cancellationToken)
    {
        // Retention pruning removes the oldest rows, so a cursor that points below what survives has
        // missed changes it can never be told about. Saying so is the only honest answer; the client
        // re-snapshots. The pruner always keeps the newest row, which is what makes this check total:
        // an empty log would otherwise be indistinguishable from a fully pruned one.
        var oldest = await database.ChangeLog.AsNoTracking()
            .MinAsync(entry => (long?)entry.Sequence, cancellationToken);
        if (oldest is { } lowest && position.Watermark < lowest - 1)
        {
            return new NativeSyncPage(
                Items: [],
                RemovedIds: [],
                ChangedPreferenceScopes: [],
                Cursor: NativeSyncCursor.StartSnapshot(await HighWatermarkAsync(cancellationToken)).Encode(),
                HasMore: false,
                ResetRequired: true);
        }

        var changes = await database.ChangeLog.AsNoTracking()
            .Where(entry => entry.Sequence > position.Watermark
                && (entry.AppUserId == null || entry.AppUserId == appUserId))
            .OrderBy(entry => entry.Sequence)
            .Take(PageSize)
            .ToListAsync(cancellationToken);

        if (changes.Count == 0)
        {
            return new NativeSyncPage([], [], [], position.Encode(), HasMore: false, ResetRequired: false);
        }

        // Preferences are their own entity type and their scope is not an item id — the user's default
        // is the literal "global" — so they are split out before anything tries to read an item from
        // them. Without this they were dropped while the cursor advanced past them, and a choice made
        // on one device never reached another.
        var preferenceScopes = changes
            .Where(entry => entry.EntityType == ChangeEntityType.PlaybackPreference)
            .Select(entry => entry.EntityId)
            .Distinct()
            .ToList();

        // One row per changed item is what the client wants, not one per event: an item touched five
        // times in a page is still one fetch.
        var touched = changes
            .Where(entry => entry.EntityType != ChangeEntityType.PlaybackPreference)
            .Select(entry => Guid.TryParseExact(entry.EntityId, "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        var live = await database.MediaItems.AsNoTracking()
            .Where(item => touched.Contains(item.Id) && item.PublicId != null && item.RemovedAt == null)
            .ToListAsync(cancellationToken);

        var projected = await library.ProjectCardsAsync(live, appUserId, cancellationToken);

        // Anything the client was told about that no longer resolves to a published item is a removal:
        // it was purged, tombstoned, or unpublished. The client does not need to know which.
        var liveIds = live.Select(item => item.Id).ToHashSet();
        var removed = touched.Where(id => !liveIds.Contains(id)).Select(id => id.ToString("N")).ToList();

        var last = changes[^1].Sequence;
        return new NativeSyncPage(
            Items: projected,
            RemovedIds: removed,
            ChangedPreferenceScopes: preferenceScopes,
            Cursor: NativeSyncCursor.Delta(last).Encode(),
            HasMore: changes.Count == PageSize,
            ResetRequired: false);
    }
}

/// <summary>
/// One page of the sync stream. <paramref name="ResetRequired"/> means the client's position has been
/// pruned away and it must re-snapshot from the returned cursor; items and removals are empty then.
/// </summary>
public sealed record NativeSyncPage(
    IReadOnlyList<LibraryItemDto> Items,
    IReadOnlyList<string> RemovedIds,
    /// <summary>
    /// Preference scopes that changed: an item id, or the literal <c>global</c> for the user's default.
    /// The client re-reads them from the preferences endpoint — the payload is small and rarely
    /// changes, so carrying ids beats duplicating the shape here.
    /// </summary>
    IReadOnlyList<string> ChangedPreferenceScopes,
    string Cursor,
    bool HasMore,
    bool ResetRequired);
