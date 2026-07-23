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

    /// <summary>
    /// Frozen provider-neutral identity (JSON) as it was when the play was recorded. Outbound delivery
    /// runs later and must describe the item as identified then, even if the library has since been
    /// rescanned, re-identified, or the item deleted.
    /// </summary>
    public string? IdentitySnapshot { get; set; }

    /// <summary>Provider this entry is linked to, when it is. One link per entry in v1.</summary>
    public string? ProviderKey { get; set; }

    /// <summary>The provider's own id for the corresponding remote entry, once resolved.</summary>
    public string? ProviderHistoryId { get; set; }

    /// <summary>
    /// True only when Media Server created the remote entry. Remote deletion is permitted for these
    /// and nothing else: a matching identity and timestamp is not evidence of ownership, and deleting
    /// on that basis would remove plays another client recorded.
    /// </summary>
    public bool ProviderEntryOwned { get; set; }

    public PlaybackHistoryLinkStatus LinkStatus { get; set; }

    public AppUser? AppUser { get; set; }

    public MediaItem? MediaItem { get; set; }
}

/// <summary>Where a play came from. Decides what may be exported and what may be deleted remotely.</summary>
public enum PlaybackHistoryOrigin
{
    /// <summary>Observed playback crossing the watched threshold. The only source of an exact local time.</summary>
    LocalPlayback,

    /// <summary>An explicit watched toggle, by the user. Timeless.</summary>
    Manual,

    /// <summary>Imported from the provider during an explicit sync.</summary>
    ProviderSync,

    /// <summary>Reconstructed from a pre-migration aggregate row. Timeless — its real times are unknowable.</summary>
    Legacy,
}

/// <summary>How far the link between a local entry and its remote counterpart got.</summary>
public enum PlaybackHistoryLinkStatus
{
    /// <summary>Not linked, and not expected to be.</summary>
    None,

    /// <summary>Outbound work is in flight; the remote id is not known yet.</summary>
    Pending,

    /// <summary>The remote id was resolved uniquely and stored.</summary>
    Resolved,

    /// <summary>
    /// The add committed but its remote id could not be pinned down — an eventually consistent read,
    /// or a concurrent write making the before/after difference ambiguous. Never reposted and never
    /// deleted remotely: guessing here means destroying someone else's history.
    /// </summary>
    Unresolved,
}
