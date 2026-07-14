using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Watchlist;

/// <summary>
/// Wishlist ↔ library linking: matches a <see cref="TrackedTitle"/>'s canonical identity against
/// published library items (on add and on each publish via a reconcile), and computes the read-time
/// owned-vs-aired gap for linked series. Deleting a library item unlinks via the FK's SetNull; a remap
/// or unpublish is caught by the reconcile. No persisted state beyond <see cref="TrackedTitle.MediaItemId"/>.
/// </summary>
public sealed class WatchlistLibraryLinker(MediaServerDbContext database, TimeProvider timeProvider)
{
    /// <summary>Links one title to a published top-level library item with the same identity, if any.
    /// Returns true when the link changed. Does not save.</summary>
    public async Task<bool> TryLinkAsync(TrackedTitle title, CancellationToken cancellationToken)
    {
        var match = await FindLibraryItemIdAsync(title.Kind, title.IdentityProvider, title.IdentityProviderId, cancellationToken);
        if (title.MediaItemId == match)
        {
            return false;
        }

        title.MediaItemId = match;
        return true;
    }

    /// <summary>
    /// Reconciles every tracked title against the library: links titles whose identity is now published,
    /// and unlinks ones whose linked item vanished or no longer carries that identity (remap). Saves when
    /// anything changed.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var titles = await database.TrackedTitles.ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var title in titles)
        {
            if (await TryLinkAsync(title, cancellationToken))
            {
                title.UpdatedAt = timeProvider.GetUtcNow();
                changed++;
            }
        }

        if (changed > 0)
        {
            await database.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    /// <summary>
    /// The owned-vs-aired projection for a linked series: episodes the library holds vs episodes that
    /// have aired per the tracked schedule (within the sync horizon), and the "behind by N" difference.
    /// Null for movies and unlinked titles — a linked movie is just "in library" (the release reads as an
    /// upgrade, not a first acquisition).
    /// </summary>
    public async Task<LibraryGapDto?> ComputeGapAsync(TrackedTitle title, CancellationToken cancellationToken)
    {
        if (title.Kind != MediaKind.Series || title.MediaItemId is not { } seriesId)
        {
            return null;
        }

        var owned = await database.MediaItems.AsNoTracking()
            .Where(episode => episode.Kind == MediaKind.Episode && episode.PublicId != null
                && episode.SeriesId == seriesId
                && episode.IdentitySeasonNumber != null && episode.IdentityEpisodeNumber != null)
            .Select(episode => new { Season = episode.IdentitySeasonNumber!.Value, Episode = episode.IdentityEpisodeNumber!.Value })
            .ToListAsync(cancellationToken);
        var ownedSet = owned.Select(pair => (pair.Season, pair.Episode)).ToHashSet();

        var today = WatchlistScope.LocalDay(timeProvider.GetUtcNow(), timeProvider.LocalTimeZone);
        var aired = title.Releases
            .Where(release => release is { Type: ReleaseType.EpisodeAir, Season: not null, Episode: not null }
                && release.Date <= today)
            .Select(release => (release.Season!.Value, release.Episode!.Value))
            .ToHashSet();

        return new LibraryGapDto(
            OwnedEpisodes: ownedSet.Count,
            AiredEpisodes: aired.Count,
            MissingAired: aired.Count(pair => !ownedSet.Contains(pair)));
    }

    private async Task<Guid?> FindLibraryItemIdAsync(
        MediaKind kind, string provider, string providerId, CancellationToken cancellationToken)
    {
        // Published (PublicId set) top-level items only; movies match movies, series match series.
        return await database.MediaItems.AsNoTracking()
            .Where(item => item.Kind == kind && item.PublicId != null && item.ParentId == null
                && item.IdentityProvider == provider && item.IdentityProviderId == providerId)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
