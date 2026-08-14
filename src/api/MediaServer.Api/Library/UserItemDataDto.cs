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
    // The user's own 1-5 star verdict, or null when unrated. Deliberately NOT called `Rating`: this DTO
    // is also the Jellyfin surface's UserData, where `Rating` is a 0-10 double, so emitting a 4 there
    // would claim "four out of ten" to a client reading Jellyfin's schema. Under this name no client
    // misreads it, and the real mapping can be decided once a client is known to want one.
    int? UserRating = null,
    // Jellyfin's UserData carries the item id too; set only by the Jellyfin mapper (Infuse decodes it as
    // a required field). Left null for the internal /api surface, which keys off the item itself.
    string? ItemId = null);

/// <summary>The bounds of the star scale, in one place so the endpoint, the engine and the UI agree.</summary>
public static class UserRatingScale
{
    public const int Min = 1;

    public const int Max = 5;

    public static bool IsValid(int rating) => rating is >= Min and <= Max;
}

/// <summary>Why a rating was or was not stored, so the endpoint can answer 200/400/404.</summary>
public enum SetRatingStatus
{
    Applied,

    /// <summary>No such item — or none this instance still holds.</summary>
    ItemNotFound,

    /// <summary>A season, an episode or an extra. A rating is a verdict on a work.</summary>
    NotRatable,

    /// <summary>Outside 1–5. Silently clamping would store a number the viewer did not give.</summary>
    OutOfRange,
}

/// <summary>The outcome of <see cref="UserDataService.SetRatingAsync"/>, with the updated state on success.</summary>
public readonly record struct SetRatingResult(SetRatingStatus Status, UserItemDataDto? Data);

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
