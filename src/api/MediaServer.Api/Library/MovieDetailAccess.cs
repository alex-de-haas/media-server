using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>Web-only access to published items and removed movies with the caller's own retained signal.</summary>
internal static class MovieDetailAccess
{
    public static IQueryable<MediaItem> Query(MediaServerDbContext database, int? appUserId) =>
        database.MediaItems.AsNoTracking().Where(item =>
            (item.PublicId != null && item.RemovedAt == null) ||
            (appUserId != null && item.Kind == MediaKind.Movie && item.RemovedAt != null &&
                (database.UserItemData.Any(data => data.MediaItemId == item.Id && data.AppUserId == appUserId &&
                    (data.IsFavorite || data.Rating != null)) ||
                 database.PlaybackHistoryEntries.Any(entry => entry.MediaItemId == item.Id && entry.AppUserId == appUserId))));
}
