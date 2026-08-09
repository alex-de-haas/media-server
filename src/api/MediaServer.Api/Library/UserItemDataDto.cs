namespace MediaServer.Api.Library;

/// <summary>
/// Surface-neutral per-user playback state for a media item: resume position, play count, watched flag,
/// favorite, and folder (season/series) rollups. Projected by <see cref="UserDataService"/> from the
/// domain and consumed by BOTH the internal <c>/api</c> (UI) surface and the Jellyfin provider adapter —
/// it belongs to neither. <see cref="Key"/> is the item's public id (or internal id when unpublished).
/// </summary>
public sealed record UserItemDataDto(
    string Key,
    long PlaybackPositionTicks = 0,
    int PlayCount = 0,
    bool IsFavorite = false,
    bool Played = false,
    double? PlayedPercentage = null,
    DateTimeOffset? LastPlayedDate = null,
    int? UnplayedItemCount = null,
    // Jellyfin's UserData carries the item id too; set only by the Jellyfin mapper (Infuse decodes it as
    // a required field). Left null for the internal /api surface, which keys off the item itself.
    string? ItemId = null);

/// <summary>Why a hand-logged watch was or was not recorded, so the endpoint can answer 200/400/404.</summary>
public enum LogWatchStatus
{
    Recorded,

    /// <summary>No such item — or none this instance still holds.</summary>
    ItemNotFound,

    /// <summary>A season or series. Logging one viewing for a folder is a different gesture, not this one.</summary>
    NotPlayable,

    /// <summary>An instant in the future. Nobody has watched anything then.</summary>
    FutureInstant,
}

/// <summary>The outcome of <see cref="UserDataService.LogWatchAsync"/>, with the updated state on success.</summary>
public readonly record struct LogWatchResult(LogWatchStatus Status, UserItemDataDto? Data);
