using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Recommendations;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Jellyfin;

/// <summary>
/// Synthesizes artwork for the synthetic "Recommended" view, which — like a catalog — carries no images of
/// its own. It borrows the backdrop of the title its shelf leads with, so the library tile shows what is
/// currently being suggested instead of a blank placeholder. The image bytes are served by
/// <see cref="JellyfinImageService"/> from the ordinary <see cref="ImageAsset"/> cache: the shelf points at
/// library titles, so nothing new is fetched or cached for the tile.
/// </summary>
public sealed class JellyfinShelfArtwork(
    MediaServerDbContext database, IRecommendationShelf shelf, MediaServerSettings settings)
{
    /// <summary>
    /// How far down the shelf to look for a title with a backdrop. Deep enough that a couple of unenriched
    /// titles at the top do not blank the tile, shallow enough that the tile still represents the row.
    /// </summary>
    public const int Depth = 10;

    /// <summary>The backdrop standing in for a user's shelf, or null when the shelf has none to lend.</summary>
    public async Task<ImageAsset?> BackdropAsync(int appUserId, CancellationToken cancellationToken) =>
        await BackdropAsync(await shelf.GetAsync(appUserId, Depth, cancellationToken), cancellationToken);

    /// <summary>
    /// Same, for a caller that has already read the shelf — the view listing reads it to decide whether the
    /// view exists at all, and one read answers both questions.
    /// </summary>
    public async Task<ImageAsset?> BackdropAsync(IReadOnlyList<MediaItem> ranked, CancellationToken cancellationToken)
    {
        if (ranked.Count == 0)
        {
            return null;
        }

        // Bounded by Depth, so the id list is nowhere near SQLite's parameter limit and needs no chunking.
        var itemIds = ranked.Select(item => item.Id).ToList();
        var backdrops = await database.ImageAssets.AsNoTracking()
            .Where(image => image.ImageType == ImageType.Backdrop && itemIds.Contains(image.MediaItemId))
            .ToListAsync(cancellationToken);
        if (backdrops.Count == 0)
        {
            return null;
        }

        // Walked in rank order rather than taking whichever image sorts first: the tile should show the
        // title the shelf leads with. Which of *that* title's backdrops is then a question for
        // ImageSelection, which prefers one with no text burned into it (see
        // docs/features/artwork-language/feature.md) — the shelf tile carries its own label.
        var byItem = backdrops
            .GroupBy(image => image.MediaItemId)
            .ToDictionary(
                group => group.Key,
                group => group.Best(ImageType.Backdrop, settings.PreferredLanguage)!);
        return ranked
            .Select(item => byItem.GetValueOrDefault(item.Id))
            .FirstOrDefault(image => image is not null);
    }
}
