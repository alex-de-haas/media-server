namespace MediaServer.Api.Data;

/// <summary>
/// One explicit "Sync with Trakt" job: first a read-only preview, then, if the user applies it, the
/// reconciliation itself.
/// </summary>
/// <remarks>
/// Sync is the only inbound path and is always user-triggered. It can still overwrite local aggregate
/// counters and resume points, so the preview is mandatory and the captured state revisions below are
/// what stop a play recorded *during* a long sync from being trampled by the snapshot that was read
/// before it happened.
/// </remarks>
public sealed class WatchHistorySyncRun
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    public Guid ConnectionId { get; set; }

    /// <summary>Selected catalog ids and media kinds (JSON). Null means every accessible catalog.</summary>
    public string? Scope { get; set; }

    public WatchHistorySyncStatus Status { get; set; }

    /// <summary>Aggregate preview counts (JSON): both sides, one side only, ambiguous, and so on.</summary>
    public string? Counts { get; set; }

    /// <summary>Bounded sample of issues to show the user (JSON) — never the whole list.</summary>
    public string? Issues { get; set; }

    /// <summary>
    /// Per-item <see cref="UserItemData.StateRevision"/> captured when the run started (JSON). Apply
    /// skips any row whose revision moved, so a local completion during the run survives instead of
    /// being overwritten by a snapshot taken before it.
    /// </summary>
    public string? CapturedRevisions { get; set; }

    /// <summary>True when outbound work was still pending at preview time; apply is blocked until it drains.</summary>
    public bool HasPendingOutboundWork { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>A preview goes stale: applying an old snapshot would act on a library that has moved on.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Sanitized, bounded failure detail.</summary>
    public string? LastError { get; set; }

    public WatchHistoryProviderConnection? Connection { get; set; }

    public AppUser? AppUser { get; set; }
}

/// <summary>Lifecycle of a sync run.</summary>
public enum WatchHistorySyncStatus
{
    /// <summary>Preview being computed.</summary>
    Previewing,

    /// <summary>Preview ready, waiting for the user to apply or discard it.</summary>
    Previewed,

    /// <summary>Applying.</summary>
    Applying,

    Completed,

    Failed,

    /// <summary>The preview expired or the user walked away.</summary>
    Abandoned,
}
