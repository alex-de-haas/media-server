namespace MediaServer.Api.Data;

/// <summary>
/// Per-user playback state for a single <see cref="MediaItem"/> (Jellyfin <c>UserItemData</c>): resume
/// position, watched flag, play count, and favorite. Keyed to the internal <see cref="MediaItem.Id"/>
/// (not the client-facing public id) so it survives rescans and public-id remaps — see
/// <c>docs/planning/jellyfin-compatibility.md</c>. Folder rollups (season/series watched aggregates) are
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

    public bool Played { get; set; }

    public bool IsFavorite { get; set; }

    /// <summary>Last time the item was started, progressed, or marked played; orders resume/next-up.</summary>
    public DateTimeOffset? LastPlayedDate { get; set; }

    public AppUser? AppUser { get; set; }

    public MediaItem? MediaItem { get; set; }
}
