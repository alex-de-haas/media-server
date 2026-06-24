using System.Text.Json;
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
        // Cast, crew, networks, studios, trailer, keywords, … all live in the cached TMDb payload (Raw);
        // parse them once here rather than persisting a column per field.
        var rich = ParseRich(meta?.Raw, item.Kind);

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
            LogoUrl(images),
            item.LibraryPath,
            userDataByItem.GetValueOrDefault(item.Id),
            mediaSources,
            seasons,
            rich.Networks,
            rich.Status,
            rich.VoteCount,
            rich.SeasonCount,
            rich.EpisodeCount,
            rich.CollectionName,
            rich.Homepage,
            rich.ImdbId,
            rich.TrailerUrl,
            rich.Cast,
            rich.Directors,
            rich.Creators,
            rich.Studios,
            rich.Keywords);
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

    // The TMDb image CDN base. Poster/backdrop/logo RemotePaths are already absolute (the provider
    // prefixes them), but network logo_paths come straight from the cached JSON payload, so prefix here.
    private const string TmdbImageBase = "https://image.tmdb.org/t/p/original";

    /// <summary>
    /// The best title logo: prefer the configured language, then English, then a language-neutral logo,
    /// then whatever is first. Logos are tagged with a 2-letter language (or null) by the provider.
    /// </summary>
    private string? LogoUrl(IReadOnlyList<ImageAsset> images)
    {
        var logos = images.Where(image => image.ImageType == ImageType.Logo).ToList();
        if (logos.Count == 0)
        {
            return null;
        }

        var preferred = settings.SupportedLanguages.Count > 0 ? settings.SupportedLanguages[0] : "en-US";
        var prefix = preferred.Length >= 2 ? preferred[..2] : preferred;

        string? Pick(string? language) => logos
            .Where(image => string.Equals(image.Language, language, StringComparison.OrdinalIgnoreCase))
            .OrderBy(image => image.SortOrder)
            .Select(image => image.RemotePath)
            .FirstOrDefault();

        return Pick(prefix)
            ?? Pick("en")
            ?? Pick(null)
            ?? logos.OrderBy(image => image.SortOrder).Select(image => image.RemotePath).First();
    }

    /// <summary>
    /// Rich detail fields derived from the cached TMDb payload (<c>MetadataRecord.Raw</c>). The provider
    /// folds credits/external ids/videos/certification/keywords into that payload via append_to_response,
    /// so everything here is parsed without an extra request or a dedicated column.
    /// </summary>
    private sealed record RichMetadata
    {
        public static readonly RichMetadata Empty = new();

        public IReadOnlyList<NetworkDto>? Networks { get; init; }
        public IReadOnlyList<StudioDto> Studios { get; init; } = [];
        public IReadOnlyList<CastMemberDto> Cast { get; init; } = [];
        public IReadOnlyList<string> Directors { get; init; } = [];
        public IReadOnlyList<string> Creators { get; init; } = [];
        public IReadOnlyList<string> Keywords { get; init; } = [];
        public string? Status { get; init; }
        public int? VoteCount { get; init; }
        public int? SeasonCount { get; init; }
        public int? EpisodeCount { get; init; }
        public string? CollectionName { get; init; }
        public string? Homepage { get; init; }
        public string? ImdbId { get; init; }
        public string? TrailerUrl { get; init; }
    }

    private const int MaxCast = 20;
    private const int MaxKeywords = 16;

    private static RichMetadata ParseRich(string? raw, MediaKind kind)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return RichMetadata.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return RichMetadata.Empty;
            }

            // Networks are a series concept; keep them null for movies so the UI hides the rail.
            var networks = kind == MediaKind.Series
                ? ParseBrands(root, "networks").Select(brand => new NetworkDto(brand.Name, brand.LogoUrl)).ToList()
                : [];

            return new RichMetadata
            {
                Networks = networks.Count > 0 ? networks : null,
                Studios = ParseBrands(root, "production_companies").Select(brand => new StudioDto(brand.Name, brand.LogoUrl)).ToList(),
                Cast = ParseCast(root),
                Directors = ParseCrewJob(root, "Director"),
                Creators = ParseNames(root, "created_by"),
                Keywords = ParseKeywords(root),
                Status = EmptyToNull(JsonString(root, "status")),
                VoteCount = JsonInt(root, "vote_count"),
                SeasonCount = JsonInt(root, "number_of_seasons"),
                EpisodeCount = JsonInt(root, "number_of_episodes"),
                CollectionName = ParseCollectionName(root),
                Homepage = EmptyToNull(JsonString(root, "homepage")),
                ImdbId = ParseImdbId(root),
                TrailerUrl = ParseTrailerUrl(root),
            };
        }
        catch (JsonException)
        {
            return RichMetadata.Empty;
        }
    }

    // A name + absolute logo url, shared by networks and production companies (identical TMDb shape).
    private static IReadOnlyList<(string Name, string? LogoUrl)> ParseBrands(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var brands = new List<(string, string?)>();
        foreach (var element in array.EnumerateArray())
        {
            if (EmptyToNull(JsonString(element, "name")) is { } name)
            {
                brands.Add((name, ImageUrl(JsonString(element, "logo_path"))));
            }
        }

        return brands;
    }

    private static IReadOnlyList<CastMemberDto> ParseCast(JsonElement root)
    {
        if (!TryGetArray(root, "credits", "cast", out var cast))
        {
            return [];
        }

        var members = new List<CastMemberDto>();
        foreach (var member in cast.EnumerateArray())
        {
            if (EmptyToNull(JsonString(member, "name")) is not { } name)
            {
                continue;
            }

            members.Add(new CastMemberDto(name, EmptyToNull(JsonString(member, "character")), ImageUrl(JsonString(member, "profile_path"))));
            if (members.Count >= MaxCast)
            {
                break;
            }
        }

        return members;
    }

    private static IReadOnlyList<string> ParseCrewJob(JsonElement root, string job)
    {
        if (!TryGetArray(root, "credits", "crew", out var crew))
        {
            return [];
        }

        var names = new List<string>();
        foreach (var member in crew.EnumerateArray())
        {
            if (JsonEquals(member, "job", job) &&
                EmptyToNull(JsonString(member, "name")) is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static IReadOnlyList<string> ParseNames(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var names = new List<string>();
        foreach (var element in array.EnumerateArray())
        {
            if (EmptyToNull(JsonString(element, "name")) is { } name && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return names;
    }

    // Keywords nest under keywords.keywords for movies and keywords.results for tv.
    private static IReadOnlyList<string> ParseKeywords(JsonElement root)
    {
        if (!root.TryGetProperty("keywords", out var keywords) || keywords.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        JsonElement array;
        if (keywords.TryGetProperty("keywords", out var movie) && movie.ValueKind == JsonValueKind.Array)
        {
            array = movie;
        }
        else if (keywords.TryGetProperty("results", out var series) && series.ValueKind == JsonValueKind.Array)
        {
            array = series;
        }
        else
        {
            return [];
        }

        var names = new List<string>();
        foreach (var keyword in array.EnumerateArray())
        {
            if (EmptyToNull(JsonString(keyword, "name")) is { } name)
            {
                names.Add(name);
            }

            if (names.Count >= MaxKeywords)
            {
                break;
            }
        }

        return names;
    }

    private static string? ParseCollectionName(JsonElement root) =>
        root.TryGetProperty("belongs_to_collection", out var collection) && collection.ValueKind == JsonValueKind.Object
            ? EmptyToNull(JsonString(collection, "name"))
            : null;

    private static string? ParseImdbId(JsonElement root)
    {
        if (root.TryGetProperty("external_ids", out var external) && external.ValueKind == JsonValueKind.Object &&
            EmptyToNull(JsonString(external, "imdb_id")) is { } id)
        {
            return id;
        }

        // Movies also carry imdb_id at the top level.
        return EmptyToNull(JsonString(root, "imdb_id"));
    }

    // The best YouTube trailer: an official trailer, then any trailer, then any YouTube clip.
    private static string? ParseTrailerUrl(JsonElement root)
    {
        if (!TryGetArray(root, "videos", "results", out var results))
        {
            return null;
        }

        string? official = null;
        string? trailer = null;
        string? anyYoutube = null;
        foreach (var video in results.EnumerateArray())
        {
            if (!JsonEquals(video, "site", "YouTube") || EmptyToNull(JsonString(video, "key")) is not { } key)
            {
                continue;
            }

            var url = "https://www.youtube.com/watch?v=" + key;
            anyYoutube ??= url;
            if (!JsonEquals(video, "type", "Trailer"))
            {
                continue;
            }

            trailer ??= url;
            if (video.TryGetProperty("official", out var isOfficial) && isOfficial.ValueKind == JsonValueKind.True)
            {
                official ??= url;
            }
        }

        return official ?? trailer ?? anyYoutube;
    }

    private static bool TryGetArray(JsonElement root, string objectProperty, string arrayProperty, out JsonElement array)
    {
        array = default;
        if (root.TryGetProperty(objectProperty, out var container) && container.ValueKind == JsonValueKind.Object &&
            container.TryGetProperty(arrayProperty, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            array = value;
            return true;
        }

        return false;
    }

    private static string? JsonString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Ordinal compare against a string-valued property, allocation-free (ValueEquals reads the UTF-8 bytes).
    // The ValueKind guard keeps it from throwing on a non-string value, unlike a bare ValueEquals call.
    private static bool JsonEquals(JsonElement element, string property, string value) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var prop) &&
        prop.ValueKind == JsonValueKind.String && prop.ValueEquals(value);

    private static int? JsonInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? ImageUrl(string? path) => string.IsNullOrWhiteSpace(path) ? null : TmdbImageBase + path;

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
        StreamType.Video => Join(ResolutionLabel(stream.Width, stream.Height), stream.Codec?.ToUpperInvariant(), stream.HdrFormat),
        StreamType.Audio => Join(stream.Language, stream.Codec?.ToUpperInvariant(), ChannelLabel(stream.Channels)),
        StreamType.Subtitle => Join(stream.Language, stream.IsForced ? "Forced" : null),
        _ => null,
    };

    private static string? Join(params string?[] parts)
    {
        var joined = string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return joined.Length == 0 ? null : joined;
    }

    // Key off width: widescreen films keep the nominal width (e.g. 1920) while the
    // height drops below the matching value, so height alone mislabels them. Height is
    // a fallback for vertical video and the unbucketed remainder.
    private static string? ResolutionLabel(int? width, int? height)
    {
        var w = width ?? 0;
        var h = height ?? 0;
        return (w, h) switch
        {
            ( <= 0, <= 0) => null,
            ( >= 3800, _) or (_, >= 2000) => "2160p",
            ( >= 1900, _) or (_, >= 1000) => "1080p",
            ( >= 1260, _) or (_, >= 700) => "720p",
            ( >= 700, _) or (_, >= 480) => "480p",
            _ => $"{(h > 0 ? h : w)}p",
        };
    }

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
