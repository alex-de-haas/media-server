using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>One tombstoned top-level title with the signed-in user's signal summary.</summary>
public sealed record RemovedTitleDto(
    Guid Id,
    string Kind,
    string Title,
    int? Year,
    string? PosterUrl,
    DateTimeOffset RemovedAt,
    bool IsFavorite,
    int PlayCount,
    DateTimeOffset? LastWatchedAt);

/// <summary>
/// The window onto ghosts: the watched calendar shows only plays, and a tombstone can carry none at
/// all — a favorited, never-watched, deleted title would otherwise be invisible and unmanageable.
/// Lists every tombstoned movie and series with the signed-in user's signal (favorite, plays across
/// the ghost subtree, last watched), and clears a favorite on a ghost — the one write the ordinary
/// favorite endpoint refuses, because it reaches published items only.
/// </summary>
public sealed class RemovedTitlesService(MediaServerDbContext database)
{
    public async Task<IReadOnlyList<RemovedTitleDto>> ListAsync(int? appUserId, CancellationToken cancellationToken)
    {
        var roots = await database.MediaItems.AsNoTracking()
            .Where(item => item.RemovedAt != null &&
                (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series))
            .OrderByDescending(item => item.RemovedAt)
            .ToListAsync(cancellationToken);
        if (roots.Count == 0)
        {
            return [];
        }

        var rootIds = roots.Select(root => root.Id).ToList();

        // A series' plays live on its episodes: map every ghost descendant to its root so the summary
        // counts the whole title, not just the container row.
        var rootByItem = rootIds.ToDictionary(id => id, id => id);
        var descendants = await database.MediaItems.AsNoTracking()
            .Where(item => item.SeriesId != null && rootIds.Contains(item.SeriesId.Value))
            .Select(item => new { item.Id, SeriesId = item.SeriesId!.Value })
            .ToListAsync(cancellationToken);
        foreach (var descendant in descendants)
        {
            rootByItem[descendant.Id] = descendant.SeriesId;
        }

        var subtreeIds = rootByItem.Keys.ToList();
        var plays = appUserId is { } userId
            ? await database.PlaybackHistoryEntries.AsNoTracking()
                .Where(entry => entry.AppUserId == userId && subtreeIds.Contains(entry.MediaItemId))
                .Select(entry => new { entry.MediaItemId, entry.WatchedAt })
                .ToListAsync(cancellationToken)
            : [];
        var playsByRoot = plays
            .GroupBy(entry => rootByItem[entry.MediaItemId])
            .ToDictionary(group => group.Key, group => (Count: group.Count(), Last: group.Max(entry => entry.WatchedAt)));

        var favorites = appUserId is { } favoriteUserId
            ? await database.UserItemData.AsNoTracking()
                .Where(data => data.AppUserId == favoriteUserId && data.IsFavorite && rootIds.Contains(data.MediaItemId))
                .Select(data => data.MediaItemId)
                .ToListAsync(cancellationToken)
            : [];
        var favoriteSet = favorites.ToHashSet();

        var posters = new Dictionary<Guid, string>();
        var posterRows = await database.ImageAssets.AsNoTracking()
            .Where(image => rootIds.Contains(image.MediaItemId) && image.ImageType == ImageType.Primary)
            .GroupBy(image => image.MediaItemId)
            .Select(group => new
            {
                MediaItemId = group.Key,
                Url = group.OrderBy(image => image.SortOrder).Select(image => image.RemotePath).First(),
            })
            .ToListAsync(cancellationToken);
        foreach (var row in posterRows)
        {
            posters[row.MediaItemId] = row.Url;
        }

        return roots.Select(root => new RemovedTitleDto(
            root.Id,
            root.Kind.ToString(),
            root.Title,
            root.Year,
            posters.GetValueOrDefault(root.Id),
            root.RemovedAt!.Value,
            favoriteSet.Contains(root.Id),
            playsByRoot.TryGetValue(root.Id, out var summary) ? summary.Count : 0,
            playsByRoot.TryGetValue(root.Id, out var last) ? last.Last : null)).ToList();
    }

    /// <summary>Clears the user's favorite on a tombstoned title. False when there was nothing to clear.</summary>
    public async Task<bool> ClearFavoriteAsync(int appUserId, Guid mediaItemId, CancellationToken cancellationToken)
    {
        var row = await database.UserItemData.FirstOrDefaultAsync(data =>
            data.AppUserId == appUserId && data.MediaItemId == mediaItemId && data.IsFavorite &&
            database.MediaItems.Any(item => item.Id == mediaItemId && item.RemovedAt != null), cancellationToken);
        if (row is null)
        {
            return false;
        }

        // Tracked update on purpose: SaveChanges bumps StateRevision for the Jellyfin delta sync.
        row.IsFavorite = false;
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }
}
