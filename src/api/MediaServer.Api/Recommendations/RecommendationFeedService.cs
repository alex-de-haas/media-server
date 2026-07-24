using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Recommendations;

/// <summary>One card in the feed.</summary>
/// <param name="Kind">Movie or series.</param>
/// <param name="TmdbId">The shared coordinate every source and the library agree on.</param>
/// <param name="InLibrary">Whether this instance holds it — the difference between "play" and "discover".</param>
/// <param name="MediaItemId">
/// The local item, when held — and what a detail link must use: those routes are declared
/// <c>{id:guid}</c> and resolve by <see cref="MediaItem.Id"/>, so a public id would never match.
/// </param>
/// <param name="Sources">Which providers suggested it; more than one means they agreed.</param>
public sealed record RecommendationDto(
    string Kind,
    string TmdbId,
    string Title,
    int? Year,
    string? PosterUrl,
    bool InLibrary,
    Guid? MediaItemId,
    IReadOnlyList<string> Sources);

/// <summary>The feed plus what the UI needs to render its controls honestly.</summary>
/// <param name="Items">The merged, filtered feed.</param>
/// <param name="Sources">Every source available to this user, whether or not it is currently selected.</param>
/// <param name="SelectedSources">The user's narrowing, or every available source when they have none.</param>
public sealed record RecommendationFeedDto(
    IReadOnlyList<RecommendationDto> Items,
    IReadOnlyList<RecommendationProviderDescriptor> Sources,
    IReadOnlyList<string> SelectedSources);

/// <summary>
/// Builds one user's merged feed: ask the available providers, fuse, then answer the questions only
/// the library can — is this already held, already watched, or already dismissed.
/// </summary>
/// <remarks>
/// Providers deliberately know nothing about the local library. Watched and hidden filtering lives
/// here instead, so a provider stays a pure source and the same rules apply to every one of them.
/// </remarks>
public sealed class RecommendationFeedService(
    MediaServerDbContext database,
    IRecommendationProviderRegistry registry,
    ITmdbPosterLookup posters,
    ILogger<RecommendationFeedService> logger)
{
    /// <summary>How many each provider is asked for before fusion. Bounded so one long tail cannot drown the other's head.</summary>
    internal const int PerProvider = 50;

    public async Task<RecommendationFeedDto> BuildAsync(
        int appUserId, RecommendationKind? kind, int limit, CancellationToken cancellationToken)
    {
        var available = await registry.AvailableForAsync(appUserId, cancellationToken);
        var descriptors = available
            .Select(provider => new RecommendationProviderDescriptor(provider.Key, provider.DisplayName))
            .ToList();

        var selected = await SelectedSourcesAsync(appUserId, available, cancellationToken);
        var active = available.Where(provider => selected.Contains(provider.Key, StringComparer.OrdinalIgnoreCase)).ToList();

        var lists = new List<RankedList>(active.Count);
        foreach (var provider in active)
        {
            try
            {
                var candidates = await provider.GetAsync(appUserId, PerProvider, cancellationToken);
                lists.Add(new RankedList(provider.Key, candidates));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One source failing outright must not cost the user the others.
                logger.LogWarning(exception, "Recommendation provider {Key} failed; skipping it.", provider.Key);
            }
        }

        // Fuse generously, then filter: excluding watched and hidden titles afterwards would otherwise
        // eat into the limit and hand back a short feed.
        var fused = RecommendationFusion.Fuse(lists, limit * 4);
        var items = await ProjectAsync(appUserId, fused, kind, limit, cancellationToken);

        return new RecommendationFeedDto(items, descriptors, [.. selected]);
    }

    private async Task<List<RecommendationDto>> ProjectAsync(
        int appUserId,
        IReadOnlyList<FusedRecommendation> fused,
        RecommendationKind? kind,
        int limit,
        CancellationToken cancellationToken)
    {
        if (fused.Count == 0)
        {
            return [];
        }

        var hidden = await HiddenAsync(appUserId, cancellationToken);
        var library = await LibraryByTmdbIdAsync(cancellationToken);
        var watched = await WatchedAsync(appUserId, library, cancellationToken);

        var items = new List<RecommendationDto>(limit);
        foreach (var entry in fused)
        {
            if (kind is { } wanted && entry.Identity.Kind != wanted)
            {
                continue;
            }

            // Dismissed by this user, or already seen: neither belongs in "what next".
            if (hidden.Contains(entry.Identity) || watched.Contains(entry.Identity))
            {
                continue;
            }

            var held = library.GetValueOrDefault(entry.Identity)?.Representative;
            items.Add(new RecommendationDto(
                entry.Identity.Kind.ToString(),
                entry.Identity.TmdbId,
                // The library's own title wins when it holds the item: that is the name the user sees
                // everywhere else in this app.
                held?.Title ?? entry.Title,
                entry.Year,
                entry.PosterUrl,
                held is not null,
                held?.Id,
                entry.Sources));

            if (items.Count == limit)
            {
                break;
            }
        }

        return await WithPostersAsync(items, cancellationToken);
    }

    /// <summary>
    /// Fills artwork in for the cards that reached the feed without any — Trakt returns none, so a
    /// title only it suggested would otherwise render as a grey box.
    /// </summary>
    /// <remarks>
    /// Deliberately after the limit is applied: this costs one TMDb request per uncached title, and
    /// paying that for candidates nobody will see would be waste.
    /// </remarks>
    private async Task<List<RecommendationDto>> WithPostersAsync(
        List<RecommendationDto> items, CancellationToken cancellationToken)
    {
        var missing = items
            .Where(item => item.PosterUrl is null)
            .Select(item => new RecommendationIdentity(
                Enum.Parse<RecommendationKind>(item.Kind), item.TmdbId))
            .ToList();

        if (missing.Count == 0)
        {
            return items;
        }

        var found = await posters.ForAsync(missing, cancellationToken);
        return [.. items.Select(item => item.PosterUrl is not null
            ? item
            : found.TryGetValue(
                new RecommendationIdentity(Enum.Parse<RecommendationKind>(item.Kind), item.TmdbId),
                out var url)
                ? item with { PosterUrl = url }
                : item)];
    }

    private async Task<HashSet<RecommendationIdentity>> HiddenAsync(
        int appUserId, CancellationToken cancellationToken)
    {
        var rows = await database.RecommendationHides.AsNoTracking()
            .Where(hide => hide.AppUserId == appUserId)
            .Select(hide => new { hide.Kind, hide.TmdbId })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(row => new RecommendationIdentity(row.Kind, row.TmdbId))];
    }

    /// <summary>
    /// One title the library holds: every local copy of it, plus the one whose id the card links to.
    /// </summary>
    /// <remarks>
    /// Several catalogs can hold the same title (a 4K edition beside a regular one). Keeping only one
    /// copy would be enough to say "you have this", but not enough to say "you watched this" — a play
    /// recorded against the other copy would be missed and the title recommended anyway.
    /// </remarks>
    private sealed record LibraryTitle(MediaItem Representative, IReadOnlyList<Guid> CopyIds);

    /// <summary>Every movie and series the library holds, keyed by the coordinate the feed speaks.</summary>
    private async Task<Dictionary<RecommendationIdentity, LibraryTitle>> LibraryByTmdbIdAsync(
        CancellationToken cancellationToken)
    {
        var items = await database.MediaItems.AsNoTracking()
            .Where(item => item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series)
            .ToListAsync(cancellationToken);

        var copies = new Dictionary<RecommendationIdentity, List<MediaItem>>();
        foreach (var item in items)
        {
            if (RecommendationSeedSelector.TmdbIdOf(item) is not { } tmdbId)
            {
                continue;
            }

            var kind = item.Kind == MediaKind.Movie ? RecommendationKind.Movie : RecommendationKind.Series;
            var identity = new RecommendationIdentity(kind, tmdbId);
            if (copies.TryGetValue(identity, out var existing))
            {
                existing.Add(item);
            }
            else
            {
                copies[identity] = [item];
            }
        }

        return copies.ToDictionary(
            pair => pair.Key,
            // Oldest copy as the representative, so the link a user follows does not change when a
            // second edition is added.
            pair => new LibraryTitle(
                pair.Value.OrderBy(item => item.AddedAt).First(),
                [.. pair.Value.Select(item => item.Id)]));
    }

    /// <summary>
    /// Titles this user has already seen. A movie counts when played; a series counts once any episode
    /// has been — a part-watched show belongs to Next Up, not to discovery.
    /// </summary>
    private async Task<HashSet<RecommendationIdentity>> WatchedAsync(
        int appUserId,
        Dictionary<RecommendationIdentity, LibraryTitle> library,
        CancellationToken cancellationToken)
    {
        if (library.Count == 0)
        {
            return [];
        }

        // Every copy, not just the representative: watching the 4K edition counts.
        var itemIds = library.Values.SelectMany(title => title.CopyIds).ToHashSet();

        var playedItemIds = await database.UserItemData.AsNoTracking()
            .Where(row => row.AppUserId == appUserId && row.Played)
            .Select(row => row.MediaItemId)
            .ToListAsync(cancellationToken);

        // An episode play marks its series watched, which is why this joins through SeriesId.
        var playedSeriesIds = await database.PlaybackHistoryEntries.AsNoTracking()
            .Where(entry => entry.AppUserId == appUserId)
            .Join(
                database.MediaItems.AsNoTracking(),
                entry => entry.MediaItemId,
                item => item.Id,
                (_, item) => item.Kind == MediaKind.Episode && item.SeriesId != null ? item.SeriesId!.Value : item.Id)
            .Distinct()
            .ToListAsync(cancellationToken);

        var seen = playedItemIds.Concat(playedSeriesIds).Where(itemIds.Contains).ToHashSet();

        return [.. library
            .Where(pair => pair.Value.CopyIds.Any(seen.Contains))
            .Select(pair => pair.Key)];
    }

    private async Task<HashSet<string>> SelectedSourcesAsync(
        int appUserId, IReadOnlyList<IRecommendationProvider> available, CancellationToken cancellationToken)
    {
        var preference = await database.RecommendationPreferences.AsNoTracking()
            .FirstOrDefaultAsync(row => row.AppUserId == appUserId, cancellationToken);

        var everything = available.Select(provider => provider.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (preference?.Sources is not { } stored)
        {
            // No preference means every available source — the default, and distinct from a stored
            // empty string, which would mean the user turned everything off.
            return everything;
        }

        var chosen = stored
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(everything.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A preference naming only sources that have since disappeared would silently empty the feed;
        // fall back rather than show nothing with no explanation.
        return chosen.Count > 0 ? chosen : everything;
    }

    /// <summary>Stores the user's source narrowing, or clears it back to "every available source".</summary>
    public async Task SetSourcesAsync(
        int appUserId, IReadOnlyList<string>? sources, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var preference = await database.RecommendationPreferences
            .FirstOrDefaultAsync(row => row.AppUserId == appUserId, cancellationToken);

        var value = sources is null || sources.Count == 0 ? null : string.Join(',', sources);
        if (preference is null)
        {
            database.RecommendationPreferences.Add(new RecommendationPreference
            {
                Id = Guid.NewGuid(), AppUserId = appUserId, Sources = value, UpdatedAt = now,
            });
        }
        else
        {
            preference.Sources = value;
            preference.UpdatedAt = now;
        }

        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Hides a title from this user's feed. Idempotent: hiding twice is the same intent.</summary>
    public async Task HideAsync(
        int appUserId, RecommendationIdentity identity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var exists = await database.RecommendationHides.AnyAsync(
            hide => hide.AppUserId == appUserId && hide.Kind == identity.Kind && hide.TmdbId == identity.TmdbId,
            cancellationToken);

        if (exists)
        {
            return;
        }

        database.RecommendationHides.Add(new RecommendationHide
        {
            Id = Guid.NewGuid(), AppUserId = appUserId, Kind = identity.Kind, TmdbId = identity.TmdbId, CreatedAt = now,
        });

        try
        {
            await database.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Raced with another tab hiding the same card; the intent is satisfied either way.
            database.ChangeTracker.Clear();
        }
    }

    /// <summary>Restores a hidden title — what the undo on the hide toast calls.</summary>
    public async Task UnhideAsync(
        int appUserId, RecommendationIdentity identity, CancellationToken cancellationToken)
    {
        var hide = await database.RecommendationHides.FirstOrDefaultAsync(
            row => row.AppUserId == appUserId && row.Kind == identity.Kind && row.TmdbId == identity.TmdbId,
            cancellationToken);

        if (hide is null)
        {
            return;
        }

        database.RecommendationHides.Remove(hide);
        await database.SaveChangesAsync(cancellationToken);
    }
}
