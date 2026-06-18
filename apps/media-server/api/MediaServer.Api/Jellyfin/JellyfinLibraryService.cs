using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Jellyfin;

/// <summary>Filters for an items query (Jellyfin <c>/Items</c> and friends).</summary>
public sealed record JellyfinItemsQuery
{
    public string? ParentId { get; init; }
    public IReadOnlyList<string>? Ids { get; init; }
    public IReadOnlySet<string>? IncludeItemTypes { get; init; }
    public string? SearchTerm { get; init; }
    public bool Recursive { get; init; }
    public bool IncludeMediaSources { get; init; }
    public int? StartIndex { get; init; }
    public int? Limit { get; init; }
}

/// <summary>
/// Browsing/projection over the published library for the Jellyfin surface. Resolves public ids back to
/// catalogs/items, loads localized metadata, images, sources and parent links, and projects everything
/// through <see cref="JellyfinItemMapper"/>. Only published items (those with a public id) are exposed.
/// </summary>
public sealed class JellyfinLibraryService(
    MediaServerDbContext database,
    JellyfinItemMapper mapper,
    UserDataService userData,
    MediaServerSettings settings)
{
    private string PreferredLanguage => settings.SupportedLanguages.Count > 0 ? settings.SupportedLanguages[0] : "en-US";

    public async Task<IReadOnlyList<BaseItemDto>> GetViewsAsync(CancellationToken cancellationToken)
    {
        var catalogs = await database.Catalogs.AsNoTracking().OrderBy(catalog => catalog.Name).ToListAsync(cancellationToken);
        return catalogs.Select(mapper.MapCollectionFolder).ToList();
    }

    /// <summary>A single library/view (collection folder) by its public id, or null if it is not a catalog.</summary>
    public async Task<BaseItemDto?> GetViewAsync(string publicId, CancellationToken cancellationToken)
    {
        // A view is always a catalog, so query catalogs directly rather than probing MediaItems first.
        var catalogs = await database.Catalogs.AsNoTracking().ToListAsync(cancellationToken);
        var catalog = catalogs.FirstOrDefault(candidate => JellyfinIds.Catalog(candidate.Id) == publicId);
        return catalog is null ? null : mapper.MapCollectionFolder(catalog);
    }

    public async Task<BaseItemDto?> GetItemAsync(
        string publicId, bool includeMediaSources, int? appUserId, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.PublicId == publicId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var dtos = await MapManyAsync([item], includeMediaSources, appUserId, cancellationToken);
        return dtos.FirstOrDefault();
    }

    public async Task<QueryResult<BaseItemDto>> ListItemsAsync(
        JellyfinItemsQuery query, int? appUserId, CancellationToken cancellationToken)
    {
        var items = await ResolveItemsAsync(query, cancellationToken);

        var total = items.Count;
        var start = query.StartIndex ?? 0;
        IEnumerable<MediaItem> page = items.Skip(start);
        if (query.Limit is { } limit)
        {
            page = page.Take(limit);
        }

        var dtos = await MapManyAsync(page.ToList(), query.IncludeMediaSources, appUserId, cancellationToken);
        return new QueryResult<BaseItemDto>(dtos, total, start);
    }

    public async Task<QueryResult<BaseItemDto>> GetSeasonsAsync(
        string seriesPublicId, int? appUserId, CancellationToken cancellationToken)
    {
        var series = await FindByPublicIdAsync(seriesPublicId, cancellationToken);
        if (series is null || series.Kind != MediaKind.Series)
        {
            return new QueryResult<BaseItemDto>([], 0);
        }

        var seasons = await PublishedChildren(series.Id, MediaKind.Season)
            .OrderBy(item => item.IndexNumber)
            .ToListAsync(cancellationToken);

        var dtos = await MapManyAsync(seasons, includeMediaSources: false, appUserId, cancellationToken);
        return new QueryResult<BaseItemDto>(dtos, dtos.Count);
    }

    public async Task<QueryResult<BaseItemDto>> GetEpisodesAsync(
        string seriesPublicId, string? seasonPublicId, int? seasonNumber, int? appUserId, CancellationToken cancellationToken)
    {
        var series = await FindByPublicIdAsync(seriesPublicId, cancellationToken);
        if (series is null || series.Kind != MediaKind.Series)
        {
            return new QueryResult<BaseItemDto>([], 0);
        }

        var query = database.MediaItems.AsNoTracking()
            .Where(item => item.SeriesId == series.Id && item.Kind == MediaKind.Episode && item.PublicId != null);

        if (!string.IsNullOrEmpty(seasonPublicId))
        {
            var season = await FindByPublicIdAsync(seasonPublicId, cancellationToken);
            if (season is not null)
            {
                query = query.Where(item => item.SeasonId == season.Id);
            }
        }
        else if (seasonNumber is { } number)
        {
            query = query.Where(item => item.ParentIndexNumber == number);
        }

        var episodes = await query
            .OrderBy(item => item.ParentIndexNumber)
            .ThenBy(item => item.IndexNumber)
            .ToListAsync(cancellationToken);

        var dtos = await MapManyAsync(episodes, includeMediaSources: false, appUserId, cancellationToken);
        return new QueryResult<BaseItemDto>(dtos, dtos.Count);
    }

    public async Task<QueryResult<BaseItemDto>> GetLatestAsync(
        string? parentPublicId, int limit, int? appUserId, CancellationToken cancellationToken)
    {
        var query = TopLevelItems();
        if (!string.IsNullOrEmpty(parentPublicId))
        {
            var catalogId = await ResolveCatalogIdAsync(parentPublicId, cancellationToken);
            if (catalogId is { } id)
            {
                query = query.Where(item => item.CatalogId == id);
            }
        }

        var items = await query
            .OrderByDescending(item => item.AddedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        var dtos = await MapManyAsync(items, includeMediaSources: false, appUserId, cancellationToken);
        return new QueryResult<BaseItemDto>(dtos, dtos.Count);
    }

    /// <summary>
    /// In-progress items (resume points) for a user, most recently played first. Optionally scoped to a
    /// catalog via <paramref name="parentPublicId"/>. Episodes and movies only — folders are not resumable.
    /// </summary>
    public async Task<QueryResult<BaseItemDto>> GetResumeAsync(
        int appUserId, string? parentPublicId, int limit, CancellationToken cancellationToken)
    {
        var resumable = await database.UserItemData.AsNoTracking()
            .Where(data => data.AppUserId == appUserId && !data.Played && data.PlaybackPositionTicks > 0)
            .Select(data => new { data.MediaItemId, data.LastPlayedDate })
            .ToListAsync(cancellationToken);
        if (resumable.Count == 0)
        {
            return new QueryResult<BaseItemDto>([], 0);
        }

        var lastPlayedByItem = resumable.ToDictionary(
            entry => entry.MediaItemId, entry => entry.LastPlayedDate ?? DateTimeOffset.MinValue);
        var itemIds = lastPlayedByItem.Keys.ToList();

        var query = database.MediaItems.AsNoTracking().Where(item =>
            itemIds.Contains(item.Id) && item.PublicId != null &&
            (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Episode));

        if (!string.IsNullOrEmpty(parentPublicId))
        {
            var catalogId = await ResolveCatalogIdAsync(parentPublicId, cancellationToken);
            if (catalogId is { } id)
            {
                query = query.Where(item => item.CatalogId == id);
            }
        }

        var items = await query.ToListAsync(cancellationToken);
        var ordered = items
            .OrderByDescending(item => lastPlayedByItem[item.Id])
            .Take(limit)
            .ToList();

        var dtos = await MapManyAsync(ordered, includeMediaSources: false, appUserId, cancellationToken);
        return new QueryResult<BaseItemDto>(dtos, dtos.Count);
    }

    /// <summary>
    /// The next unwatched episode for each series the user has started watching, most recently played
    /// series first. A series is "started" once any of its episodes is played; a fully-watched series is
    /// dropped. Optionally scoped to a single series via <paramref name="seriesPublicId"/>.
    /// </summary>
    public async Task<QueryResult<BaseItemDto>> GetNextUpAsync(
        int appUserId, string? seriesPublicId, int limit, CancellationToken cancellationToken)
    {
        // Played episodes joined to their series with a single server-side join — no aggregate (SQLite
        // can't MAX over DateTimeOffset) and, crucially, no large id list passed to SQL as parameters.
        // Grouping/Max run client-side over the bounded result.
        var played = await database.UserItemData.AsNoTracking()
            .Where(data => data.AppUserId == appUserId && data.Played)
            .Join(
                database.MediaItems.AsNoTracking().Where(episode => episode.Kind == MediaKind.Episode && episode.SeriesId != null),
                data => data.MediaItemId,
                episode => episode.Id,
                (data, episode) => new { EpisodeId = episode.Id, SeriesId = episode.SeriesId!.Value, data.LastPlayedDate })
            .ToListAsync(cancellationToken);
        if (played.Count == 0)
        {
            return new QueryResult<BaseItemDto>([], 0);
        }

        Guid? onlySeriesId = null;
        if (!string.IsNullOrEmpty(seriesPublicId))
        {
            var series = await FindByPublicIdAsync(seriesPublicId, cancellationToken);
            if (series is null)
            {
                return new QueryResult<BaseItemDto>([], 0);
            }

            onlySeriesId = series.Id;
        }

        var playedItemIds = played.Select(entry => entry.EpisodeId).ToHashSet();
        var lastPlayedBySeries = played
            .Where(entry => onlySeriesId is null || entry.SeriesId == onlySeriesId)
            .GroupBy(entry => entry.SeriesId)
            .ToDictionary(group => group.Key, group => group.Max(entry => entry.LastPlayedDate) ?? DateTimeOffset.MinValue);

        var startedSeriesIds = lastPlayedBySeries.Keys.ToList();
        if (startedSeriesIds.Count == 0)
        {
            return new QueryResult<BaseItemDto>([], 0);
        }

        var episodes = await database.MediaItems.AsNoTracking()
            .Where(episode => episode.Kind == MediaKind.Episode && episode.PublicId != null &&
                episode.SeriesId != null && startedSeriesIds.Contains(episode.SeriesId.Value))
            .ToListAsync(cancellationToken);

        var nextUp = new List<(MediaItem Episode, DateTimeOffset LastPlayed)>();
        foreach (var group in episodes.GroupBy(episode => episode.SeriesId!.Value))
        {
            var next = group
                .OrderBy(episode => episode.ParentIndexNumber ?? 0)
                .ThenBy(episode => episode.IndexNumber ?? 0)
                .FirstOrDefault(episode => !playedItemIds.Contains(episode.Id));
            if (next is null)
            {
                continue; // Series fully watched.
            }

            nextUp.Add((next, lastPlayedBySeries.GetValueOrDefault(group.Key, DateTimeOffset.MinValue)));
        }

        var nextEpisodes = nextUp
            .OrderByDescending(entry => entry.LastPlayed)
            .Take(limit)
            .Select(entry => entry.Episode)
            .ToList();

        var dtos = await MapManyAsync(nextEpisodes, includeMediaSources: false, appUserId, cancellationToken);
        return new QueryResult<BaseItemDto>(dtos, dtos.Count);
    }

    /// <summary>Resolves a published item by public id to its on-disk source for streaming.</summary>
    public async Task<(MediaItem Item, MediaSource Source, Catalog Catalog)?> ResolvePlayableAsync(
        string publicId, string? mediaSourceId, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.PublicId == publicId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var sources = await database.MediaSources.AsNoTracking()
            .Where(source => source.MediaItemId == item.Id)
            .ToListAsync(cancellationToken);
        if (sources.Count == 0)
        {
            return null;
        }

        // Honor an explicit MediaSourceId so multi-version titles play the right file.
        var source = mediaSourceId is { Length: > 0 }
            ? sources.FirstOrDefault(candidate => JellyfinIds.MediaSource(candidate.Id) == mediaSourceId)
            : sources[0];
        if (source is null)
        {
            return null;
        }

        var catalog = await database.Catalogs.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == item.CatalogId, cancellationToken);
        return catalog is null ? null : (item, source, catalog);
    }

    private async Task<IReadOnlyList<MediaItem>> ResolveItemsAsync(JellyfinItemsQuery query, CancellationToken cancellationToken)
    {
        if (query.Ids is { Count: > 0 } ids)
        {
            var byId = await database.MediaItems.AsNoTracking()
                .Where(item => item.PublicId != null && ids.Contains(item.PublicId))
                .ToListAsync(cancellationToken);
            return byId;
        }

        IQueryable<MediaItem> source;
        if (string.IsNullOrEmpty(query.ParentId))
        {
            // No parent: top-level browsable items (optionally recursive search across the library).
            source = query.Recursive
                ? database.MediaItems.AsNoTracking().Where(item => item.PublicId != null &&
                    (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series || item.Kind == MediaKind.Episode))
                : TopLevelItems();
        }
        else
        {
            var (catalog, parent) = await ResolveParentAsync(query.ParentId, cancellationToken);
            if (catalog is not null)
            {
                source = TopLevelItems().Where(item => item.CatalogId == catalog.Id);
            }
            else if (parent is not null)
            {
                var childKind = parent.Kind == MediaKind.Series ? MediaKind.Season : MediaKind.Episode;
                source = PublishedChildren(parent.Id, childKind);
            }
            else
            {
                return [];
            }
        }

        if (query.IncludeItemTypes is { Count: > 0 } types)
        {
            source = source.Where(item => types.Contains(item.Kind.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(query.SearchTerm))
        {
            var term = $"%{query.SearchTerm.Trim()}%";
            // Match the raw title or any localized metadata title.
            source = source.Where(item =>
                EF.Functions.Like(item.Title, term) ||
                database.MetadataRecords.Any(meta => meta.MediaItemId == item.Id && meta.Title != null && EF.Functions.Like(meta.Title, term)));
        }

        return await source
            .OrderBy(item => item.ParentIndexNumber)
            .ThenBy(item => item.IndexNumber)
            .ThenBy(item => item.Title)
            .ToListAsync(cancellationToken);
    }

    private IQueryable<MediaItem> TopLevelItems() =>
        database.MediaItems.AsNoTracking().Where(item =>
            item.PublicId != null && item.ParentId == null &&
            (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series));

    private IQueryable<MediaItem> PublishedChildren(Guid parentId, MediaKind kind) =>
        database.MediaItems.AsNoTracking().Where(item =>
            item.ParentId == parentId && item.Kind == kind && item.PublicId != null);

    private async Task<MediaItem?> FindByPublicIdAsync(string publicId, CancellationToken cancellationToken) =>
        await database.MediaItems.AsNoTracking().FirstOrDefaultAsync(item => item.PublicId == publicId, cancellationToken);

    private async Task<(Catalog? Catalog, MediaItem? Item)> ResolveParentAsync(string publicId, CancellationToken cancellationToken)
    {
        var item = await FindByPublicIdAsync(publicId, cancellationToken);
        if (item is not null)
        {
            return (null, item);
        }

        var catalogs = await database.Catalogs.AsNoTracking().ToListAsync(cancellationToken);
        var catalog = catalogs.FirstOrDefault(candidate => JellyfinIds.Catalog(candidate.Id) == publicId);
        return (catalog, null);
    }

    private async Task<Guid?> ResolveCatalogIdAsync(string publicId, CancellationToken cancellationToken)
    {
        var (catalog, item) = await ResolveParentAsync(publicId, cancellationToken);
        return catalog?.Id ?? item?.CatalogId;
    }

    private async Task<IReadOnlyList<BaseItemDto>> MapManyAsync(
        IReadOnlyList<MediaItem> items, bool includeMediaSources, int? appUserId, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var itemIds = items.Select(item => item.Id).ToList();

        var metadata = await database.MetadataRecords.AsNoTracking()
            .Where(record => itemIds.Contains(record.MediaItemId))
            .ToListAsync(cancellationToken);
        var metadataByItem = metadata.GroupBy(record => record.MediaItemId)
            .ToDictionary(group => group.Key, group => PickLanguage(group.ToList()));

        var images = await database.ImageAssets.AsNoTracking()
            .Where(image => itemIds.Contains(image.MediaItemId))
            .ToListAsync(cancellationToken);
        var imagesByItem = images.GroupBy(image => image.MediaItemId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<ImageAsset>)group.ToList());

        var sourcesByItem = new Dictionary<Guid, IReadOnlyList<MediaSource>>();
        if (includeMediaSources)
        {
            var sources = await database.MediaSources.AsNoTracking()
                .Include(source => source.Streams)
                .Where(source => itemIds.Contains(source.MediaItemId))
                .ToListAsync(cancellationToken);
            sourcesByItem = sources.GroupBy(source => source.MediaItemId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<MediaSource>)group.ToList());
        }

        var parents = await LoadParentsAsync(items, cancellationToken);
        var childCounts = await LoadChildCountsAsync(items, cancellationToken);
        var userDataByItem = await userData.LoadAsync(appUserId, items, cancellationToken);

        var result = new List<BaseItemDto>(items.Count);
        foreach (var item in items)
        {
            var sources = sourcesByItem.GetValueOrDefault(item.Id, []);
            result.Add(mapper.MapItem(
                item,
                metadataByItem.GetValueOrDefault(item.Id),
                imagesByItem.GetValueOrDefault(item.Id, []),
                sources,
                userDataByItem.GetValueOrDefault(item.Id, new UserItemDataDto(Key: item.PublicId!)),
                BuildParents(item, parents),
                includeMediaSources,
                childCounts.GetValueOrDefault(item.Id)));
        }

        return result;
    }

    private async Task<Dictionary<Guid, (string? PublicId, string Title)>> LoadParentsAsync(
        IReadOnlyList<MediaItem> items, CancellationToken cancellationToken)
    {
        var parentIds = items
            .SelectMany(item => new[] { item.ParentId, item.SeriesId, item.SeasonId })
            .OfType<Guid>()
            .Distinct()
            .ToList();
        if (parentIds.Count == 0)
        {
            return [];
        }

        var parents = await database.MediaItems.AsNoTracking()
            .Where(item => parentIds.Contains(item.Id))
            .Select(item => new { item.Id, item.PublicId, item.Title })
            .ToListAsync(cancellationToken);
        return parents.ToDictionary(parent => parent.Id, parent => (parent.PublicId, parent.Title));
    }

    private async Task<Dictionary<Guid, int>> LoadChildCountsAsync(
        IReadOnlyList<MediaItem> items, CancellationToken cancellationToken)
    {
        var folderIds = items
            .Where(item => item.Kind is MediaKind.Series or MediaKind.Season)
            .Select(item => item.Id)
            .ToList();
        if (folderIds.Count == 0)
        {
            return [];
        }

        var counts = await database.MediaItems.AsNoTracking()
            .Where(item => item.ParentId != null && folderIds.Contains(item.ParentId.Value) && item.PublicId != null)
            .GroupBy(item => item.ParentId!.Value)
            .Select(group => new { ParentId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        return counts.ToDictionary(entry => entry.ParentId, entry => entry.Count);
    }

    private static ItemParents BuildParents(MediaItem item, Dictionary<Guid, (string? PublicId, string Title)> parents)
    {
        (string? PublicId, string Title)? Lookup(Guid? id) => id is { } value && parents.TryGetValue(value, out var found) ? found : null;

        return item.Kind switch
        {
            MediaKind.Movie or MediaKind.Series => new ItemParents(ParentId: JellyfinIds.Catalog(item.CatalogId)),
            MediaKind.Season => new ItemParents(
                ParentId: Lookup(item.SeriesId ?? item.ParentId)?.PublicId,
                SeriesId: Lookup(item.SeriesId)?.PublicId,
                SeriesName: Lookup(item.SeriesId)?.Title),
            MediaKind.Episode => new ItemParents(
                ParentId: Lookup(item.SeasonId ?? item.ParentId)?.PublicId,
                SeriesId: Lookup(item.SeriesId)?.PublicId,
                SeriesName: Lookup(item.SeriesId)?.Title,
                SeasonId: Lookup(item.SeasonId)?.PublicId,
                SeasonName: Lookup(item.SeasonId)?.Title),
            _ => new ItemParents(ParentId: JellyfinIds.Catalog(item.CatalogId)),
        };
    }

    private MetadataRecord PickLanguage(List<MetadataRecord> records)
    {
        var preferred = PreferredLanguage;
        var prefix = preferred.Length >= 2 ? preferred[..2] : preferred;
        return records.FirstOrDefault(record => string.Equals(record.Language, preferred, StringComparison.OrdinalIgnoreCase))
            ?? records.FirstOrDefault(record => record.Language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?? records[0];
    }
}
