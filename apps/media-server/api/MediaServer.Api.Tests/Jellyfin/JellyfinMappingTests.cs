using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;

namespace MediaServer.Api.Tests.Jellyfin;

public sealed class JellyfinMappingTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerSettings _settings = new() { SupportedLanguages = ["en-US"] };
    private readonly JellyfinLibraryService _library;

    private Guid _movieCatalogId;
    private Guid _seriesCatalogId;
    private string _moviePublicId = string.Empty;
    private string _seriesPublicId = string.Empty;
    private string _seasonPublicId = string.Empty;
    private string _primaryTag = string.Empty;

    public JellyfinMappingTests()
    {
        var hosty = new HostyOptions
        {
            AppId = "com.haas.media-server",
            CoreOrigin = "http://localhost:3001",
            AppDataDir = Path.GetTempPath(),
        };
        var server = new JellyfinServerContext(hosty, _settings);
        _library = new JellyfinLibraryService(_db.Create(), new JellyfinItemMapper(server), _settings);
        Seed();
    }

    [Fact]
    public async Task Views_map_catalogs_to_collection_folders()
    {
        var views = await _library.GetViewsAsync(CancellationToken.None);

        Assert.Equal(2, views.Count);
        Assert.All(views, view => Assert.Equal("CollectionFolder", view.Type));
        Assert.Contains(views, view => view.Id == JellyfinIds.Catalog(_movieCatalogId) && view.CollectionType == "movies");
        Assert.Contains(views, view => view.Id == JellyfinIds.Catalog(_seriesCatalogId) && view.CollectionType == "tvshows");
    }

    [Fact]
    public async Task Lists_a_movie_with_localized_metadata_images_and_provider_ids()
    {
        var result = await _library.ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.Catalog(_movieCatalogId) }, CancellationToken.None);

        var movie = Assert.Single(result.Items);
        Assert.Equal("Inception", movie.Name);
        Assert.Equal("Movie", movie.Type);
        Assert.Equal(2010, movie.ProductionYear);
        Assert.Equal(JellyfinIds.Catalog(_movieCatalogId), movie.ParentId);
        Assert.NotNull(movie.Genres);
        Assert.Contains("Science Fiction", movie.Genres!);
        Assert.Equal(_primaryTag, movie.ImageTags?["Primary"]);
        Assert.Equal("27205", movie.ProviderIds?["Tmdb"]);
        Assert.Equal(_moviePublicId, movie.UserData?.Key);
    }

    [Fact]
    public async Task Movie_detail_maps_media_sources_and_streams()
    {
        var movie = await _library.GetItemAsync(_moviePublicId, includeMediaSources: true, CancellationToken.None);

        Assert.NotNull(movie);
        var source = Assert.Single(movie!.MediaSources!);
        Assert.Equal("mkv", source.Container);
        Assert.True(source.SupportsDirectPlay);
        Assert.Contains($"/Videos/{_moviePublicId}/stream.mkv", source.DirectStreamUrl);

        var video = Assert.Single(source.MediaStreams, stream => stream.Type == "Video");
        Assert.Equal(1920, video.Width);
        Assert.Equal("SDR", video.VideoRange);

        var audio = Assert.Single(source.MediaStreams, stream => stream.Type == "Audio");
        Assert.Equal(6, audio.Channels);
        Assert.Equal("5.1", audio.ChannelLayout);

        var subtitle = Assert.Single(source.MediaStreams, stream => stream.Type == "Subtitle");
        Assert.Equal("Embed", subtitle.DeliveryMethod);
        Assert.True(subtitle.IsTextSubtitleStream);
    }

    [Fact]
    public async Task Search_matches_localized_metadata_titles()
    {
        // "Inception" is the localized title; the raw item title is "Inception (folder)".
        var result = await _library.ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.Catalog(_movieCatalogId), SearchTerm = "Inception" },
            CancellationToken.None);

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Series_hierarchy_resolves_parent_links_and_child_counts()
    {
        var seasons = await _library.GetSeasonsAsync(_seriesPublicId, CancellationToken.None);
        var season = Assert.Single(seasons.Items);
        Assert.Equal("Season", season.Type);
        Assert.Equal(_seriesPublicId, season.SeriesId);
        Assert.Equal(_seriesPublicId, season.ParentId);
        Assert.Equal(1, season.ChildCount);

        var episodes = await _library.GetEpisodesAsync(_seriesPublicId, null, null, CancellationToken.None);
        var episode = Assert.Single(episodes.Items);
        Assert.Equal("Episode", episode.Type);
        Assert.Equal(_seriesPublicId, episode.SeriesId);
        Assert.Equal(_seasonPublicId, episode.SeasonId);
        Assert.Equal(_seasonPublicId, episode.ParentId);
        Assert.Equal(1, episode.ParentIndexNumber);
        Assert.Equal(1, episode.IndexNumber);
        Assert.Equal("Breaking Bad", episode.SeriesName);
    }

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();

        var movieCatalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/movies", CreatedAt = now, UpdatedAt = now };
        var seriesCatalog = new Catalog { Id = Guid.NewGuid(), Name = "Shows", Type = CatalogType.Series, Root = "/shows", CreatedAt = now, UpdatedAt = now };
        _movieCatalogId = movieCatalog.Id;
        _seriesCatalogId = seriesCatalog.Id;
        context.Catalogs.AddRange(movieCatalog, seriesCatalog);

        // ---- Movie ----
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = movieCatalog.Id,
            Kind = MediaKind.Movie,
            Title = "Untitled.2010.1080p.BluRay", // raw title without the localized token
            Year = 2010,
            IdentityProvider = "tmdb",
            IdentityProviderId = "27205",
            Providers = new Dictionary<string, string> { ["tmdb"] = "27205" },
            AddedAt = now,
            UpdatedAt = now,
        };
        _moviePublicId = movie.PublicId!;
        context.MediaItems.Add(movie);
        context.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(),
            MediaItemId = movie.Id,
            Provider = "tmdb",
            Language = "en-US",
            Title = "Inception",
            Overview = "A thief who steals corporate secrets.",
            Genres = ["Science Fiction", "Action"],
            ReleaseDate = new DateTimeOffset(2010, 7, 16, 0, 0, 0, TimeSpan.Zero),
            RuntimeTicks = TimeSpan.FromMinutes(148).Ticks,
            FetchedAt = now,
        });

        _primaryTag = "primarytag000001";
        context.ImageAssets.AddRange(
            new ImageAsset { Id = Guid.NewGuid(), MediaItemId = movie.Id, ImageType = ImageType.Primary, Provider = "tmdb", RemotePath = "https://image.tmdb.org/p.jpg", Tag = _primaryTag, SortOrder = 0 },
            new ImageAsset { Id = Guid.NewGuid(), MediaItemId = movie.Id, ImageType = ImageType.Backdrop, Provider = "tmdb", RemotePath = "https://image.tmdb.org/b.jpg", Tag = "backdroptag00001", SortOrder = 0 });

        var source = new MediaSource
        {
            Id = Guid.NewGuid(),
            MediaItemId = movie.Id,
            Container = "matroska",
            Path = "library/Inception (2010)/Inception (2010).mkv",
            SizeBytes = 8_000_000_000,
            Bitrate = 12_000_000,
            DurationTicks = TimeSpan.FromMinutes(148).Ticks,
            CreatedAt = now,
        };
        context.MediaSources.Add(source);
        context.MediaStreams.AddRange(
            new MediaStream { Id = Guid.NewGuid(), MediaSourceId = source.Id, StreamType = StreamType.Video, Index = 0, Codec = "h264", Width = 1920, Height = 1080, FrameRate = 23.976, BitDepth = 8 },
            new MediaStream { Id = Guid.NewGuid(), MediaSourceId = source.Id, StreamType = StreamType.Audio, Index = 1, Codec = "ac3", Channels = 6, Language = "eng", IsDefault = true },
            new MediaStream { Id = Guid.NewGuid(), MediaSourceId = source.Id, StreamType = StreamType.Subtitle, Index = 2, Codec = "subrip", Language = "eng" });

        // ---- Series → Season → Episode ----
        var series = new MediaItem { Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = seriesCatalog.Id, Kind = MediaKind.Series, Title = "Breaking Bad", Year = 2008, AddedAt = now, UpdatedAt = now };
        var season = new MediaItem { Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = seriesCatalog.Id, Kind = MediaKind.Season, Title = "Season 1", ParentId = series.Id, SeriesId = series.Id, IndexNumber = 1, AddedAt = now, UpdatedAt = now };
        var episode = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = seriesCatalog.Id,
            Kind = MediaKind.Episode,
            Title = "Pilot",
            ParentId = season.Id,
            SeriesId = series.Id,
            SeasonId = season.Id,
            ParentIndexNumber = 1,
            IndexNumber = 1,
            LibraryPath = "library/Breaking Bad/Season 1/S01E01.mkv",
            AddedAt = now,
            UpdatedAt = now,
        };
        _seriesPublicId = series.PublicId!;
        _seasonPublicId = season.PublicId!;
        context.MediaItems.AddRange(series, season, episode);

        context.SaveChanges();
    }

    public void Dispose() => _db.Dispose();
}
