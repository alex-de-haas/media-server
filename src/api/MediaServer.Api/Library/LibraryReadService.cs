using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Library;

/// <summary>
/// Read-only projection of the published library into UI DTOs for the internal <c>/api</c> surface.
/// It queries the EF domain directly and reuses the surface-neutral <see cref="UserDataService"/> for
/// per-user playback state. This is a sibling of the Jellyfin <c>JellyfinLibraryService</c> — both read
/// the same domain, neither depends on the other, and the UI never touches the Jellyfin DTOs.
/// </summary>
public sealed class LibraryReadService(
    MediaServerDbContext database,
    UserDataService userData,
    MediaServerSettings settings)
{
    /// <summary>Top-level browsable items (published movies and series), optionally filtered by catalog/kind.</summary>
    public async Task<IReadOnlyList<LibraryItemDto>> ListAsync(
        Guid? catalogId, MediaKind? kind, int? appUserId, CancellationToken cancellationToken)
    {
        var query = database.MediaItems.AsNoTracking().Where(item =>
            item.PublicId != null && item.ParentId == null &&
            (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series));

        if (catalogId is { } id)
        {
            query = query.Where(item => item.CatalogId == id);
        }

        if (kind is { } requested)
        {
            query = query.Where(item => item.Kind == requested);
        }

        var items = await query.OrderBy(item => item.Title).ToListAsync(cancellationToken);
        return await ProjectItemsAsync(items, appUserId, cancellationToken);
    }

    /// <summary>Most recently published top-level items (movies/series), newest first — the Recently Added rail.</summary>
    public async Task<IReadOnlyList<LibraryItemDto>> GetRecentAsync(int limit, int? appUserId, CancellationToken cancellationToken)
    {
        var items = await database.MediaItems.AsNoTracking()
            .Where(item => item.PublicId != null && item.ParentId == null &&
                (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Series))
            .OrderByDescending(item => item.AddedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);
        return await ProjectItemsAsync(items, appUserId, cancellationToken);
    }

    /// <summary>In-progress leaves (movies/episodes) for the Continue Watching rail, most recently played first.</summary>
    public async Task<IReadOnlyList<LibraryRailItemDto>> GetResumeAsync(int appUserId, int limit, CancellationToken cancellationToken)
    {
        // Server-side join + order/limit so the IN-list never grows (avoids SQLite's 999-parameter limit);
        // the DateTimeOffset converter makes the LastPlayedDate ordering translatable.
        var leaves = await database.UserItemData.AsNoTracking()
            .Where(data => data.AppUserId == appUserId && !data.Played && data.PlaybackPositionTicks > 0)
            .Join(
                database.MediaItems.AsNoTracking().Where(item => item.PublicId != null &&
                    (item.Kind == MediaKind.Movie || item.Kind == MediaKind.Episode)),
                data => data.MediaItemId,
                item => item.Id,
                (data, item) => new { Item = item, data.LastPlayedDate })
            .OrderByDescending(entry => entry.LastPlayedDate)
            .Take(limit)
            .Select(entry => entry.Item)
            .ToListAsync(cancellationToken);
        return await ProjectRailAsync(leaves, appUserId, cancellationToken);
    }

    /// <summary>The next unwatched episode of each started series (most recently played first) — the Next Up rail.</summary>
    public async Task<IReadOnlyList<LibraryRailItemDto>> GetNextUpAsync(int appUserId, int limit, CancellationToken cancellationToken)
    {
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
            return [];
        }

        var playedIds = played.Select(entry => entry.EpisodeId).ToHashSet();
        var lastBySeries = played.GroupBy(entry => entry.SeriesId)
            .ToDictionary(group => group.Key, group => group.Max(entry => entry.LastPlayedDate) ?? DateTimeOffset.MinValue);

        // Join episodes to the started-series set in SQL (vs a large IN list) to stay under the
        // 999-parameter limit; "next unwatched per series" is then chosen over the bounded result.
        var startedSeriesIds = database.UserItemData.AsNoTracking()
            .Where(data => data.AppUserId == appUserId && data.Played)
            .Join(
                database.MediaItems.AsNoTracking().Where(episode => episode.Kind == MediaKind.Episode && episode.SeriesId != null),
                data => data.MediaItemId,
                episode => episode.Id,
                (data, episode) => episode.SeriesId!.Value)
            .Distinct();

        var episodes = await database.MediaItems.AsNoTracking()
            .Where(episode => episode.Kind == MediaKind.Episode && episode.PublicId != null && episode.SeriesId != null)
            .Join(startedSeriesIds, episode => episode.SeriesId!.Value, seriesId => seriesId, (episode, _) => episode)
            .ToListAsync(cancellationToken);

        var nextUp = new List<(MediaItem Episode, DateTimeOffset LastPlayed)>();
        foreach (var group in episodes.GroupBy(episode => episode.SeriesId!.Value))
        {
            var next = group
                .OrderBy(episode => episode.ParentIndexNumber ?? 0)
                .ThenBy(episode => episode.IndexNumber ?? 0)
                .FirstOrDefault(episode => !playedIds.Contains(episode.Id));
            if (next is not null)
            {
                nextUp.Add((next, lastBySeries.GetValueOrDefault(group.Key, DateTimeOffset.MinValue)));
            }
        }

        var ordered = nextUp.OrderByDescending(entry => entry.LastPlayed).Take(limit).Select(entry => entry.Episode).ToList();
        return await ProjectRailAsync(ordered, appUserId, cancellationToken);
    }

    private async Task<List<LibraryItemDto>> ProjectItemsAsync(
        IReadOnlyList<MediaItem> items, int? appUserId, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var itemIds = items.Select(item => item.Id).ToList();
        var posters = await PostersAsync(itemIds, cancellationToken);
        var metaByItem = await MetadataByItemAsync(itemIds, cancellationToken);
        var userDataByItem = await userData.LoadAsync(appUserId, items, cancellationToken);

        return items.Select(item =>
        {
            var meta = metaByItem.GetValueOrDefault(item.Id);
            return new LibraryItemDto(
                item.Id,
                item.PublicId,
                item.CatalogId,
                item.Kind.ToString(),
                TitleFor(meta, item.Title),
                item.Year ?? meta?.ReleaseDate?.Year,
                posters.GetValueOrDefault(item.Id),
                userDataByItem.GetValueOrDefault(item.Id));
        }).ToList();
    }

    private async Task<List<LibraryRailItemDto>> ProjectRailAsync(
        IReadOnlyList<MediaItem> leaves, int appUserId, CancellationToken cancellationToken)
    {
        if (leaves.Count == 0)
        {
            return [];
        }

        var leafIds = leaves.Select(leaf => leaf.Id).ToList();
        var metaByLeaf = await MetadataByItemAsync(leafIds, cancellationToken);
        var leafPosters = await PostersAsync(leafIds, cancellationToken);
        var userDataByLeaf = await userData.LoadAsync(appUserId, leaves, cancellationToken);

        var seriesIds = leaves
            .Where(leaf => leaf.Kind == MediaKind.Episode && leaf.SeriesId != null)
            .Select(leaf => leaf.SeriesId!.Value).Distinct().ToList();
        var seriesById = seriesIds.Count == 0
            ? new Dictionary<Guid, MediaItem>()
            : await database.MediaItems.AsNoTracking()
                .Where(series => seriesIds.Contains(series.Id))
                .ToDictionaryAsync(series => series.Id, cancellationToken);
        var seriesMeta = await MetadataByItemAsync(seriesIds, cancellationToken);
        var seriesPosters = await PostersAsync(seriesIds, cancellationToken);

        var result = new List<LibraryRailItemDto>(leaves.Count);
        foreach (var leaf in leaves)
        {
            if (leaf.Kind == MediaKind.Episode && leaf.SeriesId is { } seriesId && seriesById.TryGetValue(seriesId, out var series))
            {
                var label = $"S{(leaf.ParentIndexNumber ?? 0):00}E{(leaf.IndexNumber ?? 0):00}";
                var episodeTitle = TitleFor(metaByLeaf.GetValueOrDefault(leaf.Id), leaf.Title);
                result.Add(new LibraryRailItemDto(
                    leaf.Id, "Episode", series.Id, "Series",
                    TitleFor(seriesMeta.GetValueOrDefault(series.Id), series.Title),
                    $"{label} · {episodeTitle}",
                    seriesPosters.GetValueOrDefault(series.Id) ?? leafPosters.GetValueOrDefault(leaf.Id),
                    userDataByLeaf.GetValueOrDefault(leaf.Id)));
            }
            else
            {
                result.Add(new LibraryRailItemDto(
                    leaf.Id, "Movie", leaf.Id, "Movie",
                    TitleFor(metaByLeaf.GetValueOrDefault(leaf.Id), leaf.Title),
                    null,
                    leafPosters.GetValueOrDefault(leaf.Id),
                    userDataByLeaf.GetValueOrDefault(leaf.Id)));
            }
        }

        return result;
    }

    /// <summary>Detail for a single item by internal id. Movies/episodes carry media sources; series carry seasons.</summary>
    public async Task<LibraryDetailDto?> GetDetailAsync(Guid id, int? appUserId, CancellationToken cancellationToken)
    {
        var item = await database.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == id && candidate.PublicId != null, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var meta = await PickMetadataAsync(item.Id, cancellationToken);
        var images = await database.ImageAssets.AsNoTracking()
            .Where(image => image.MediaItemId == item.Id)
            .ToListAsync(cancellationToken);

        var mediaSources = new List<MediaSourceDto>();
        if (item.Kind is MediaKind.Movie or MediaKind.Episode or MediaKind.Video)
        {
            var sources = await database.MediaSources.AsNoTracking()
                .Include(source => source.Streams)
                .Where(source => source.MediaItemId == item.Id)
                .ToListAsync(cancellationToken);
            mediaSources = sources.Select(MapSource).ToList();
        }

        List<SeasonSummaryDto>? seasons = null;
        if (item.Kind == MediaKind.Series)
        {
            seasons = await LoadSeasonsAsync(item.Id, appUserId, cancellationToken);
        }

        var userDataByItem = await userData.LoadAsync(appUserId, [item], cancellationToken);
        var runtimeTicks = meta?.RuntimeTicks
            ?? (mediaSources.Count > 0 ? mediaSources.Max(source => source.DurationTicks) : null);

        return new LibraryDetailDto(
            item.Id,
            item.PublicId,
            TmdbId(item),
            item.CatalogId,
            item.Kind.ToString(),
            TitleFor(meta, item.Title),
            item.OriginalTitle,
            item.Year ?? meta?.ReleaseDate?.Year,
            meta?.Overview,
            meta?.Tagline,
            meta?.Genres ?? [],
            meta?.OfficialRating,
            meta?.CommunityRating,
            runtimeTicks,
            item.IndexNumber,
            item.ParentIndexNumber,
            ImageUrl(images, ImageType.Primary),
            ImageUrl(images, ImageType.Backdrop),
            item.LibraryPath,
            userDataByItem.GetValueOrDefault(item.Id),
            mediaSources,
            seasons);
    }

    /// <summary>Episodes of a series (optionally a single season), ordered by season then episode number.</summary>
    public async Task<IReadOnlyList<EpisodeDto>> GetEpisodesAsync(
        Guid seriesId, Guid? seasonId, int? appUserId, CancellationToken cancellationToken)
    {
        var seriesExists = await database.MediaItems.AsNoTracking()
            .AnyAsync(item => item.Id == seriesId && item.Kind == MediaKind.Series, cancellationToken);
        if (!seriesExists)
        {
            return [];
        }

        var query = database.MediaItems.AsNoTracking()
            .Where(item => item.SeriesId == seriesId && item.Kind == MediaKind.Episode && item.PublicId != null);
        if (seasonId is { } sid)
        {
            query = query.Where(item => item.SeasonId == sid);
        }

        var episodes = await query
            .OrderBy(item => item.ParentIndexNumber)
            .ThenBy(item => item.IndexNumber)
            .ToListAsync(cancellationToken);
        if (episodes.Count == 0)
        {
            return [];
        }

        var ids = episodes.Select(episode => episode.Id).ToList();
        var metaByItem = await MetadataByItemAsync(ids, cancellationToken);
        var posters = await PostersAsync(ids, cancellationToken);
        var userDataByItem = await userData.LoadAsync(appUserId, episodes, cancellationToken);

        return episodes.Select(episode =>
        {
            var meta = metaByItem.GetValueOrDefault(episode.Id);
            return new EpisodeDto(
                episode.Id,
                episode.PublicId,
                TmdbId(episode),
                episode.ParentIndexNumber,
                episode.IndexNumber,
                TitleFor(meta, episode.Title),
                meta?.Overview,
                meta?.RuntimeTicks,
                posters.GetValueOrDefault(episode.Id),
                userDataByItem.GetValueOrDefault(episode.Id));
        }).ToList();
    }

    private async Task<List<SeasonSummaryDto>> LoadSeasonsAsync(
        Guid seriesId, int? appUserId, CancellationToken cancellationToken)
    {
        var seasons = await database.MediaItems.AsNoTracking()
            .Where(item => item.SeriesId == seriesId && item.Kind == MediaKind.Season && item.PublicId != null)
            .OrderBy(item => item.IndexNumber)
            .ToListAsync(cancellationToken);
        if (seasons.Count == 0)
        {
            return [];
        }

        var seasonIds = seasons.Select(season => season.Id).ToList();
        var counts = await database.MediaItems.AsNoTracking()
            .Where(episode => episode.Kind == MediaKind.Episode && episode.PublicId != null &&
                episode.SeasonId != null && seasonIds.Contains(episode.SeasonId.Value))
            .GroupBy(episode => episode.SeasonId!.Value)
            .Select(group => new { SeasonId = group.Key, Count = group.Count() })
            .ToListAsync(cancellationToken);
        var countBySeason = counts.ToDictionary(entry => entry.SeasonId, entry => entry.Count);

        var metaByItem = await MetadataByItemAsync(seasonIds, cancellationToken);
        var userDataBySeason = await userData.LoadAsync(appUserId, seasons, cancellationToken);

        return seasons.Select(season => new SeasonSummaryDto(
            season.Id,
            season.PublicId,
            season.IndexNumber,
            TitleFor(metaByItem.GetValueOrDefault(season.Id), season.Title),
            countBySeason.GetValueOrDefault(season.Id),
            userDataBySeason.GetValueOrDefault(season.Id))).ToList();
    }

    // Chunked so a large library (itemIds > 999) never exceeds SQLite's parameter limit in the IN-list.
    private async Task<Dictionary<Guid, string>> PostersAsync(IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
    {
        var posters = new Dictionary<Guid, string>();
        foreach (var chunk in itemIds.Chunk(500))
        {
            var rows = await database.ImageAssets.AsNoTracking()
                .Where(image => chunk.Contains(image.MediaItemId) && image.ImageType == ImageType.Primary)
                .GroupBy(image => image.MediaItemId)
                .Select(group => new
                {
                    MediaItemId = group.Key,
                    Url = group.OrderBy(image => image.SortOrder).Select(image => image.RemotePath).First(),
                })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                posters[row.MediaItemId] = row.Url;
            }
        }
        return posters;
    }

    private async Task<Dictionary<Guid, MetadataRecord>> MetadataByItemAsync(
        IReadOnlyList<Guid> itemIds, CancellationToken cancellationToken)
    {
        var records = new List<MetadataRecord>();
        foreach (var chunk in itemIds.Chunk(500))
        {
            records.AddRange(await database.MetadataRecords.AsNoTracking()
                .Where(record => chunk.Contains(record.MediaItemId))
                .ToListAsync(cancellationToken));
        }
        return records.GroupBy(record => record.MediaItemId)
            .ToDictionary(group => group.Key, group => PickLanguage(group.ToList()));
    }

    private async Task<MetadataRecord?> PickMetadataAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var records = await database.MetadataRecords.AsNoTracking()
            .Where(record => record.MediaItemId == itemId)
            .ToListAsync(cancellationToken);
        return records.Count == 0 ? null : PickLanguage(records);
    }

    private MetadataRecord PickLanguage(List<MetadataRecord> records)
    {
        var preferred = settings.SupportedLanguages.Count > 0 ? settings.SupportedLanguages[0] : "en-US";
        var prefix = preferred.Length >= 2 ? preferred[..2] : preferred;
        return records.FirstOrDefault(record => string.Equals(record.Language, preferred, StringComparison.OrdinalIgnoreCase))
            ?? records.FirstOrDefault(record => record.Language.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?? records[0];
    }

    /// <summary>The item's TMDb id (for episodes/seasons this is the series id, which is how they're identified).</summary>
    private static string? TmdbId(MediaItem item) =>
        string.Equals(item.IdentityProvider, "tmdb", StringComparison.OrdinalIgnoreCase)
            ? item.IdentityProviderId
            : item.Providers.GetValueOrDefault("tmdb");

    private static string TitleFor(MetadataRecord? meta, string fallback) =>
        !string.IsNullOrWhiteSpace(meta?.Title) ? meta!.Title! : fallback;

    private static string? ImageUrl(IReadOnlyList<ImageAsset> images, ImageType type) =>
        images.Where(image => image.ImageType == type).OrderBy(image => image.SortOrder)
            .Select(image => image.RemotePath).FirstOrDefault();

    private static MediaSourceDto MapSource(MediaSource source) => new(
        source.Id,
        source.VersionName,
        source.Container,
        source.SizeBytes,
        source.Bitrate,
        source.DurationTicks,
        source.Streams.OrderBy(stream => stream.Index).Select(MapStream).ToList());

    private static MediaStreamDto MapStream(MediaStream stream) => new(
        stream.StreamType.ToString(),
        stream.Index,
        stream.Codec,
        stream.Language,
        DisplayTitle(stream),
        stream.Width,
        stream.Height,
        stream.HdrFormat,
        stream.Channels,
        stream.IsDefault,
        stream.IsForced,
        stream.IsExternal);

    private static string? DisplayTitle(MediaStream stream) => stream.StreamType switch
    {
        StreamType.Video => Join(ResolutionLabel(stream.Height), stream.Codec?.ToUpperInvariant(), stream.HdrFormat),
        StreamType.Audio => Join(stream.Language, stream.Codec?.ToUpperInvariant(), ChannelLabel(stream.Channels)),
        StreamType.Subtitle => Join(stream.Language, stream.IsForced ? "Forced" : null),
        _ => null,
    };

    private static string? Join(params string?[] parts)
    {
        var joined = string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return joined.Length == 0 ? null : joined;
    }

    private static string? ResolutionLabel(int? height) => height switch
    {
        null or <= 0 => null,
        >= 2160 => "2160p",
        >= 1080 => "1080p",
        >= 720 => "720p",
        >= 480 => "480p",
        _ => $"{height}p",
    };

    private static string? ChannelLabel(int? channels) => channels switch
    {
        null or <= 0 => null,
        1 => "Mono",
        2 => "Stereo",
        6 => "5.1",
        8 => "7.1",
        _ => $"{channels}.0",
    };
}
