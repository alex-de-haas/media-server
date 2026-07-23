namespace MediaServer.Api.Data;

/// <summary>
/// Per-user playback state for a single <see cref="MediaItem"/> (Jellyfin <c>UserItemData</c>): resume
/// position, watched flag, play count, and favorite. Keyed to the internal <see cref="MediaItem.Id"/>
/// (not the client-facing public id) so it survives rescans and public-id remaps — see
/// <c>docs/features/jellyfin-compatibility.md</c>. Folder rollups (season/series watched aggregates) are
/// computed on read from the descendant episode rows, never stored here.
/// </summary>
public sealed class UserItemData
{
    public Guid Id { get; set; }

    public int AppUserId { get; set; }

    public Guid MediaItemId { get; set; }

    /// <summary>Resume position; reset to zero once the item crosses the watched threshold or is marked played.</summary>
    public long PlaybackPositionTicks { get; set; }

    /// <summary>Times the item has been watched to completion.</summary>
    public int PlayCount { get; set; }

    /// <summary>
    /// When the item was last watched to completion, projected from per-play history. Distinct from
    /// <see cref="LastPlayedDate"/>, which any playback report moves and which is never populated from
    /// a provider — otherwise imported history would reshuffle Continue Watching and Next Up.
    /// </summary>
    public DateTimeOffset? LastWatchedAt { get; set; }

    /// <summary>When <see cref="Played"/> last changed, either way.</summary>
    public DateTimeOffset? WatchedStateChangedAt { get; set; }

    /// <summary>
    /// Bumped on every local change to this row. A long-running sync captures it before reading the
    /// provider and re-checks it before applying, so a play recorded while the sync was running is
    /// skipped rather than overwritten by a snapshot taken before it happened.
    /// </summary>
    public int StateRevision { get; set; }

    public bool Played { get; set; }

    public bool IsFavorite { get; set; }

    /// <summary>Last time the item was started, progressed, or marked played; orders resume/next-up.</summary>
    public DateTimeOffset? LastPlayedDate { get; set; }

    public AppUser? AppUser { get; set; }

    public MediaItem? MediaItem { get; set; }
}
