namespace MediaServer.Api.Data;

/// <summary>
/// One play of one item — the local per-play source of truth that <see cref="UserItemData"/>'s
/// aggregate counters are projected from.
/// </summary>
/// <remarks>
/// Exact entries are deliberately **not** deduplicated by (item, timestamp): two real plays can share
/// a timestamp at our precision. For locally observed playback the client session id is the uniqueness
/// rule instead — one session yields one entry, which is what stops a rewind past the watched
/// threshold from recording a second play. At most one timeless entry is kept per user and item,
/// because "watched, time unknown" says nothing more the second time.
/// </remarks>
public sealed class PlaybackHistoryEntry
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    public Guid MediaItemId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the play happened, or null for a timeless "watched, time unknown" mark — all a manual
    /// toggle or a pre-migration aggregate row can honestly claim.
    /// </summary>
    public DateTimeOffset? WatchedAt { get; set; }

    public PlaybackHistoryOrigin Origin { get; set; }

    /// <summary>
    /// The client <c>PlaySessionId</c> that produced this entry, for <see cref="Origin"/>
    /// <see cref="PlaybackHistoryOrigin.LocalPlayback"/>. Null for every other origin.
    /// </summary>
    public string? PlaySessionId { get; set; }

    public AppUser? AppUser { get; set; }

    public MediaItem? MediaItem { get; set; }
}

/// <summary>Where a recorded play came from.</summary>
public enum PlaybackHistoryOrigin
{
    /// <summary>Observed playback crossing the watched threshold. The only source of an exact local time.</summary>
    LocalPlayback,

    /// <summary>
    /// The user said so. Timeless when it came from the watched toggle, which claims no time; dated
    /// when they logged a viewing at an instant of their choosing. Nothing keys on this value alone —
    /// the queries that read it pair it with a null <see cref="PlaybackHistoryEntry.WatchedAt"/>, so a
    /// logged play is invisible to the toggle's own bookkeeping.
    /// </summary>
    Manual,

    /// <summary>Imported history. The stored origin is retained for existing entries.</summary>
    ProviderSync,

    /// <summary>Reconstructed from a pre-migration aggregate row. Timeless — its real times are unknowable.</summary>
    Legacy,
}
