namespace MediaServer.Api.Data;

/// <summary>
/// One notification that something a native client mirrors has changed, ordered by a monotonic
/// <see cref="Sequence"/>. This is what <c>/native/v1/sync</c> paginates over.
///
/// It exists instead of paginating on <c>MediaItem.UpdatedAt</c>: a timestamp watermark is an
/// invariant maintained by discipline, and a future write path that forgets to bump it hides that row
/// from every client forever, silently. Rows here are appended by the same unit of work as the
/// mutation they describe — the <c>SaveChanges</c> override for tracked writes, and explicitly inside
/// the transaction for the bulk-delete paths that bypass the change tracker.
///
/// See <c>docs/features/native-client-api/plan.md</c>.
/// </summary>
public sealed class ChangeLogEntry
{
    /// <summary>Monotonic, assigned by the database. The cursor is a position in this sequence.</summary>
    public long Sequence { get; set; }

    public ChangeEntityType EntityType { get; set; }

    /// <summary>
    /// The changed row's identity, as text so one log covers both <c>Guid</c>-keyed items and the
    /// per-user rows keyed by the item they are about.
    /// </summary>
    public required string EntityId { get; set; }

    /// <summary>
    /// The user a per-user change belongs to; null for library-wide changes. Sync filters on it, so
    /// one user's playback never shows up in another's feed.
    /// </summary>
    public int? AppUserId { get; set; }

    public ChangeKind Kind { get; set; }

    public DateTimeOffset OccurredAt { get; set; }
}

public enum ChangeEntityType
{
    MediaItem = 0,
    UserItemData = 1,
}

public enum ChangeKind
{
    /// <summary>The row exists and the client should fetch its current state.</summary>
    Upsert = 0,

    /// <summary>The row is gone. This is the case tombstones cannot cover, because a purge leaves nothing behind.</summary>
    Delete = 1,
}
