using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
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
    MediaServerSettings settings)
{
    private string PreferredLanguage => settings.SupportedLanguages.Count > 0 ? settings.SupportedLanguages[0] : "en-US";

    public async Task<IReadOnlyList<BaseItemDto>> GetViewsAsync(CancellationToken cancellationToken)
    {
        var catalogs = await database.Catalogs.AsNoTracking().OrderBy(catalog => catalog.Name).ToListAsync(cancellationToken);
        return catalogs.Select(mapper.MapCollectionFolder).ToList();
    }

    public async Task<BaseItemDto?> GetItemAsync(string publicId, bool includeMediaSources, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.PublicId == publicId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var dtos = await MapManyAsync([item], includeMediaSources, cancellationToken);
        return dtos.FirstOrDefault();
    }

    public async Task<QueryResult<BaseItemDto>> ListItemsAsync(JellyfinItemsQuery query, CancellationToken cancellationToken)
    {
        var items = await ResolveItemsAsync(query, cancellationToken);

        var total = items.Count;
        var start = query.StartIndex ?? 0;
        IEnumerable<MediaItem> page = items.Skip(start);
        if (query.Limit is { } limit)
        {
            page = page.Take(limit);
        }

        var dtos = await MapManyAsync(page.ToList(), query.IncludeMediaSources, cancellationToken);
        return new QueryResult<BaseItemDto>(dtos, total, start);
    }

    public async Task<QueryResult<BaseItemDto>> GetSeasonsAsync(string seriesPublicId, CancellationToken cancellationToken)
    {
        var series = await FindByPublicIdAsync(seriesPublicId, cancellationToken);
        if (series is null || series.Kind != MediaKind.Series)
        {
            return new QueryResult<BaseItemDto>([], 0);
        }

        var seasons = await PublishedChildren(series.Id, MediaKind.Season)
            .OrderBy(item => item.IndexNumber)
            .ToListAsync(cancellationToken);

        var dtos = await MapManyAsync(seasons, includeMediaSources: false, cancellationToken);
        return new QueryResult<BaseItemDto>(dtos, dtos.Count);
    }

    public async Task<QueryResult<BaseItemDto>> GetEpisodesAsync(
        string seriesPublicId, string? seasonPublicId, int? seasonNumber, CancellationToken cancellationToken)
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

        var dtos = await MapManyAsync(episodes, includeMediaSources: false, cancellationToken);
        return new QueryResult<BaseItemDto>(dtos, dtos.Count);
    }

    public async Task<QueryResult<BaseItemDto>> GetLatestAsync(string? parentPublicId, int limit, CancellationToken cancellationToken)
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

        var items = await query.OrderByDescending(item => item.AddedAt).Take(limit).ToListAsync(cancellationToken);
        var dtos = await MapManyAsync(items, includeMediaSources: false, cancellationToken);
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
            var term = query.SearchTerm.Trim();
            source = source.Where(item => EF.Functions.Like(item.Title, $"%{term}%"));
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
        IReadOnlyList<MediaItem> items, bool includeMediaSources, CancellationToken cancellationToken)
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

        var result = new List<BaseItemDto>(items.Count);
        foreach (var item in items)
        {
            var sources = sourcesByItem.GetValueOrDefault(item.Id, []);
            var userData = new UserItemDataDto(Key: item.PublicId!);
            result.Add(mapper.MapItem(
                item,
                metadataByItem.GetValueOrDefault(item.Id),
                imagesByItem.GetValueOrDefault(item.Id, []),
                sources,
                userData,
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
        return records.FirstOrDefault(record => string.Equals(record.Language, preferred, StringComparison.OrdinalIgnoreCase))
            ?? records.FirstOrDefault(record => record.Language.StartsWith(preferred[..2], StringComparison.OrdinalIgnoreCase))
            ?? records[0];
    }
}
