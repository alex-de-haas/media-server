using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
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
    DateTimeOffset? LastWatchedAt,
    int? UserRating = null);

/// <summary>
/// The window onto ghosts: the watched calendar shows only plays, and a tombstone can carry none at
/// all — a favorited, never-watched, deleted title would otherwise be invisible and unmanageable.
/// Lists every tombstoned movie and series with the signed-in user's signal (favorite, plays across
/// the ghost subtree, last watched), and clears a favorite on a ghost — the one write the ordinary
/// favorite endpoint refuses, because it reaches published items only.
/// </summary>
public sealed class RemovedTitlesService(MediaServerDbContext database, MediaServerSettings settings)
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

        // Favorites aggregate over the same subtree as plays: a favorited episode kept its series
        // alive as a tombstone, so the series entry must show (and be able to clear) that favorite.
        var favorites = appUserId is { } favoriteUserId
            ? await database.UserItemData.AsNoTracking()
                .Where(data => data.AppUserId == favoriteUserId && data.IsFavorite && subtreeIds.Contains(data.MediaItemId))
                .Select(data => data.MediaItemId)
                .ToListAsync(cancellationToken)
            : [];
        var favoriteSet = favorites.Select(id => rootByItem[id]).ToHashSet();

        // Ratings need no subtree walk: only works are ratable, so the row is the root's own.
        var ratings = appUserId is { } ratingUserId
            ? await database.UserItemData.AsNoTracking()
                .Where(data => data.AppUserId == ratingUserId && data.Rating != null && rootIds.Contains(data.MediaItemId))
                .ToDictionaryAsync(data => data.MediaItemId, data => data.Rating, cancellationToken)
            : [];

        var posters = await database.BestPosterUrlsAsync(rootIds, settings.PreferredLanguage, cancellationToken);

        return roots.Select(root => new RemovedTitleDto(
            root.Id,
            root.Kind.ToString(),
            root.Title,
            root.Year,
            posters.GetValueOrDefault(root.Id),
            root.RemovedAt!.Value,
            favoriteSet.Contains(root.Id),
            playsByRoot.TryGetValue(root.Id, out var summary) ? summary.Count : 0,
            playsByRoot.TryGetValue(root.Id, out var last) ? last.Last : null,
            ratings.GetValueOrDefault(root.Id))).ToList();
    }

    /// <summary>
    /// Clears the user's rating on a tombstoned title. False when there was nothing to clear.
    /// </summary>
    /// <remarks>
    /// Its own action rather than part of <see cref="ClearFavoriteAsync"/>, because the two are not the
    /// same gesture: a deleted file does not retract a verdict on a film that was watched, so a rating
    /// survives the removal and only goes when the user says so. Unlike a favorite it needs no subtree
    /// walk — only works are ratable, and a work is the root row.
    /// </remarks>
    public async Task<bool> ClearRatingAsync(int appUserId, Guid mediaItemId, CancellationToken cancellationToken)
    {
        var isTombstone = await database.MediaItems.AsNoTracking()
            .AnyAsync(item => item.Id == mediaItemId && item.RemovedAt != null, cancellationToken);
        if (!isTombstone)
        {
            return false;
        }

        var row = await database.UserItemData.FirstOrDefaultAsync(
            data => data.AppUserId == appUserId && data.MediaItemId == mediaItemId && data.Rating != null,
            cancellationToken);
        if (row is null)
        {
            return false;
        }

        // Tracked update on purpose: SaveChanges bumps StateRevision for the Jellyfin delta sync.
        row.Rating = null;
        await database.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Clears the user's favorites across a tombstoned title's whole ghost subtree — the flag may sit
    /// on the root or on any episode/season that kept the chain alive. False when there was nothing
    /// to clear.
    /// </summary>
    public async Task<bool> ClearFavoriteAsync(int appUserId, Guid mediaItemId, CancellationToken cancellationToken)
    {
        var isTombstone = await database.MediaItems.AsNoTracking()
            .AnyAsync(item => item.Id == mediaItemId && item.RemovedAt != null, cancellationToken);
        if (!isTombstone)
        {
            return false;
        }

        var subtreeIds = await database.MediaItems.AsNoTracking()
            .Where(item => item.Id == mediaItemId || item.SeriesId == mediaItemId || item.ParentId == mediaItemId)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var rows = await database.UserItemData
            .Where(data => data.AppUserId == appUserId && data.IsFavorite && subtreeIds.Contains(data.MediaItemId))
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return false;
        }

        // Tracked update on purpose: SaveChanges bumps StateRevision for the Jellyfin delta sync.
        foreach (var row in rows)
        {
            row.IsFavorite = false;
        }

        await database.SaveChangesAsync(cancellationToken);
        return true;
    }
}
