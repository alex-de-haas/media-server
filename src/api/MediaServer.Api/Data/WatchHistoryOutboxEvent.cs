namespace MediaServer.Api.Data;

/// <summary>
/// One piece of outbound intent, written in the same transaction as the local change that caused it.
/// </summary>
/// <remarks>
/// The point of the outbox is that a provider being slow, rate-limiting, or down never blocks
/// playback or a watched toggle: the local change commits, and a worker delivers later. Because the
/// worker runs after the fact, the event carries its own frozen identity rather than reading the
/// library again.
/// </remarks>
public sealed class WatchHistoryOutboxEvent
{
    public Guid Id { get; set; }

    public Guid ConnectionId { get; set; }

    public int AppUserId { get; set; }

    public Guid MediaItemId { get; set; }

    /// <summary>The history entry this event was raised for, when there is one.</summary>
    public Guid? HistoryEntryId { get; set; }

    public WatchHistoryOutboxOperation Operation { get; set; }

    /// <summary>Frozen provider-neutral identity (JSON), as at the time of the local change.</summary>
    public string? IdentitySnapshot { get; set; }

    /// <summary>Exact time to record, for <see cref="WatchHistoryOutboxOperation.AddExactWatch"/>.</summary>
    public DateTimeOffset? OccurredAt { get; set; }

    /// <summary>
    /// A bounded list of provider history ids (JSON), captured when the event was staged or during
    /// its first attempt. What it means depends on the operation:
    /// <list type="bullet">
    /// <item>for <see cref="WatchHistoryOutboxOperation.EnsureTimelessWatched"/>, the ids present
    /// <b>before</b> the add, so the new one is the set difference afterwards;</item>
    /// <item>for <see cref="WatchHistoryOutboxOperation.RemoveOwnedTimelessEntries"/>, the ids to
    /// remove — captured before the local entries are deleted, because after that there is nothing
    /// left to read them from.</item>
    /// </list>
    /// Persisted either way so a crash cannot leave the worker unable to finish what it started.
    /// </summary>
    public string? RemoteIdSnapshot { get; set; }

    /// <summary>
    /// Derived from connection, item, operation, local state revision and session key. Makes a
    /// duplicate enqueue a no-op, which matters because Trakt does not deduplicate history by item
    /// and timestamp — a retried add would show up as a second viewing.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public WatchHistoryOutboxStatus Status { get; set; }

    public int Attempts { get; set; }

    /// <summary>Held by the worker currently delivering this event; expires so a crash cannot wedge it.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    public DateTimeOffset? NextAttemptAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Sanitized, bounded failure detail. Never contains credentials or media titles.</summary>
    public string? LastError { get; set; }

    public WatchHistoryProviderConnection? Connection { get; set; }
}

/// <summary>What the worker should do at the provider.</summary>
public enum WatchHistoryOutboxOperation
{
    /// <summary>
    /// Ensure the item is marked watched with no claimed time — adding at most one "unknown" entry,
    /// and only when the provider holds no history for it at all.
    /// </summary>
    EnsureTimelessWatched,

    /// <summary>
    /// Remove only the timeless entries Media Server created and whose remote ids it resolved. Never
    /// the provider's whole-item removal, which would take exact plays with it.
    /// </summary>
    RemoveOwnedTimelessEntries,

    /// <summary>Record a play at a proven exact time.</summary>
    AddExactWatch,
}

/// <summary>Delivery state of one outbox event.</summary>
public enum WatchHistoryOutboxStatus
{
    Pending,

    /// <summary>Claimed by a worker; see <see cref="WatchHistoryOutboxEvent.LeaseUntil"/>.</summary>
    Leased,

    Completed,

    /// <summary>Failed in a way retrying cannot fix. Surfaced in Settings rather than retried forever.</summary>
    Terminal,
}
