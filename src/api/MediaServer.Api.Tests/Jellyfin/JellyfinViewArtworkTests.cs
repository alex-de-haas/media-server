using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Library;
using MediaServer.Api.Recommendations;

namespace MediaServer.Api.Tests.Jellyfin;

/// <summary>
/// The two synthetic views — Collections and Recommended — own no artwork, and a library list of
/// illustrated catalogs next to two blank tiles tells the operator nothing. Each borrows an image from
/// what it holds: a representative franchise's, and the backdrop of the title the shelf leads with.
/// These tests cover both halves — the tag the view advertises, and the bytes the image route answers with.
/// </summary>
public sealed class JellyfinViewArtworkTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerSettings _settings = new() { SupportedLanguages = ["en-US"] };
    private readonly StubShelf _shelf = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"view-artwork-{Guid.NewGuid():N}");
    private readonly HostyOptions _hosty;
    private readonly JellyfinLibraryService _library;

    private Guid _catalogId;
    private const int UserId = 3;

    public JellyfinViewArtworkTests()
    {
        _hosty = new HostyOptions { AppId = "com.haas.media-server", CoreOrigin = "http://localhost:3001", AppDataDir = _root };
        var server = new JellyfinServerContext(_hosty, _settings);
        _library = new JellyfinLibraryService(
            _db.Create(), new JellyfinItemMapper(server), new JellyfinCatalogArtwork(_db.Create()),
            new JellyfinShelfArtwork(_db.Create(), _shelf), new JellyfinCollectionService(_db.Create()),
            new JellyfinPersonService(_db.Create()), _shelf, new UserDataService(_db.Create(), TimeProvider.System), _settings);
        Seed();
    }

    [Fact]
    public async Task The_collections_view_wears_the_biggest_franchises_backdrop()
    {
        // Two qualifying franchises; the one with more owned movies is the one worth showing.
        var big = AddCollection("Big", movies: 3, poster: "poster-big.jpg", backdrop: "backdrop-big.jpg");
        AddCollection("Small", movies: 2, poster: "poster-small.jpg", backdrop: "backdrop-small.jpg");

        var view = Assert.Single(
            await _library.GetViewsAsync(UserId, CancellationToken.None),
            candidate => candidate.Id == JellyfinIds.CollectionsView());

        var tag = JellyfinCollectionService.BackdropTag(big);
        Assert.NotNull(tag);
        Assert.Equal(tag, view.ImageTags?["Primary"]);
        Assert.Equal([tag], view.BackdropImageTags);
    }

    [Fact]
    public async Task A_franchise_with_no_backdrop_lends_its_poster_instead()
    {
        var only = AddCollection("Poster only", movies: 2, poster: "poster.jpg", backdrop: null);

        var view = await _library.GetViewAsync(JellyfinIds.CollectionsView(), UserId, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(JellyfinCollectionService.PrimaryTag(only), view!.ImageTags?["Primary"]);
    }

    [Fact]
    public async Task A_franchise_with_no_artwork_at_all_still_gets_its_view_listed()
    {
        // The view exists because a franchise qualifies, not because one has a picture.
        AddCollection("Artless", movies: 2, poster: null, backdrop: null);

        var view = Assert.Single(
            await _library.GetViewsAsync(UserId, CancellationToken.None),
            candidate => candidate.Id == JellyfinIds.CollectionsView());

        Assert.Null(view.ImageTags);
        Assert.Null(view.BackdropImageTags);
    }

    [Fact]
    public async Task The_collections_view_image_serves_the_cover_franchises_backdrop()
    {
        var cover = AddCollection("Big", movies: 2, poster: "poster.jpg", backdrop: "backdrop.jpg");
        // Pre-cached on disk under the name the service writes, so serving needs no HTTP.
        var bytes = CacheCollectionArtwork(cover, new byte[] { 7, 7, 7 });

        var payload = await CreateImageService().GetImageAsync(
            JellyfinIds.CollectionsView(), ImageType.Primary, tag: null, index: 0, appUserId: UserId, CancellationToken.None);

        Assert.NotNull(payload);
        Assert.Equal(JellyfinCollectionService.BackdropTag(cover), payload!.Tag);
        Assert.Equal(bytes, payload.Content);
    }

    [Fact]
    public async Task The_recommended_view_wears_the_backdrop_of_the_title_its_shelf_leads_with()
    {
        _shelf.Items =
        [
            AddMovie("Leading", backdropTag: "shelfbackdrop000", bytes: [1, 1, 1]),
            AddMovie("Trailing", backdropTag: "otherbackdrop000", bytes: [2, 2, 2]),
        ];

        var view = Assert.Single(
            await _library.GetViewsAsync(UserId, CancellationToken.None),
            candidate => candidate.Id == JellyfinIds.RecommendationsView());

        Assert.Equal("shelfbackdrop000", view.ImageTags?["Primary"]);
        Assert.Equal(["shelfbackdrop000"], view.BackdropImageTags);
    }

    [Fact]
    public async Task An_unenriched_title_at_the_top_does_not_blank_the_tile()
    {
        // The shelf leads with a title that has no backdrop yet; the next one down lends its own.
        _shelf.Items =
        [
            AddMovie("Bare", backdropTag: null, bytes: null),
            AddMovie("Illustrated", backdropTag: "nextbackdrop0000", bytes: [3, 3, 3]),
        ];

        var view = await _library.GetViewAsync(JellyfinIds.RecommendationsView(), UserId, CancellationToken.None);

        Assert.Equal("nextbackdrop0000", view?.ImageTags?["Primary"]);
    }

    [Fact]
    public async Task A_shelf_with_no_artwork_anywhere_still_gets_its_view_listed()
    {
        _shelf.Items = [AddMovie("Bare", backdropTag: null, bytes: null)];

        var view = Assert.Single(
            await _library.GetViewsAsync(UserId, CancellationToken.None),
            candidate => candidate.Id == JellyfinIds.RecommendationsView());

        Assert.Null(view.ImageTags);
    }

    [Fact]
    public async Task The_recommended_view_image_serves_that_backdrop()
    {
        _shelf.Items = [AddMovie("Leading", backdropTag: "shelfbackdrop000", bytes: [1, 1, 1])];

        var payload = await CreateImageService().GetImageAsync(
            JellyfinIds.RecommendationsView(), ImageType.Primary, tag: null, index: 0, appUserId: UserId, CancellationToken.None);

        Assert.NotNull(payload);
        Assert.Equal("shelfbackdrop000", payload!.Tag);
        Assert.Equal([1, 1, 1], payload.Content);
    }

    [Fact]
    public async Task The_recommended_view_image_honors_the_advertised_tag()
    {
        // An admin listing another user's views is given that user's tag, and must be served that
        // user's tile rather than one re-derived from their own shelf.
        AddMovie("Someone else's", backdropTag: "otherbackdrop000", bytes: [2, 2, 2]);
        _shelf.Items = [AddMovie("Leading", backdropTag: "shelfbackdrop000", bytes: [1, 1, 1])];

        var payload = await CreateImageService().GetImageAsync(
            JellyfinIds.RecommendationsView(), ImageType.Primary, tag: "otherbackdrop000", index: 0,
            appUserId: UserId, CancellationToken.None);

        Assert.NotNull(payload);
        Assert.Equal("otherbackdrop000", payload?.Tag);
        Assert.Equal([2, 2, 2], payload!.Content);
    }

    [Fact]
    public async Task A_tag_that_names_nothing_falls_back_to_the_acting_users_shelf()
    {
        // A tag can outlive the title it named — a stale one must not blank the tile.
        _shelf.Items = [AddMovie("Leading", backdropTag: "shelfbackdrop000", bytes: [1, 1, 1])];

        var payload = await CreateImageService().GetImageAsync(
            JellyfinIds.RecommendationsView(), ImageType.Primary, tag: "goneforevergone00", index: 0,
            appUserId: UserId, CancellationToken.None);

        Assert.Equal("shelfbackdrop000", payload?.Tag);
    }

    [Fact]
    public async Task The_recommended_view_image_needs_an_acting_user()
    {
        // The shelf is personal: there is no "the" recommendation tile without someone to recommend to.
        _shelf.Items = [AddMovie("Leading", backdropTag: "shelfbackdrop000", bytes: [1, 1, 1])];

        var payload = await CreateImageService().GetImageAsync(
            JellyfinIds.RecommendationsView(), ImageType.Primary, tag: null, index: 0, appUserId: null, CancellationToken.None);

        Assert.Null(payload);
    }

    private JellyfinImageService CreateImageService() => new(
        _db.Create(), new JellyfinCatalogArtwork(_db.Create()), new JellyfinShelfArtwork(_db.Create(), _shelf),
        new JellyfinCollectionService(_db.Create()), new JellyfinPersonService(_db.Create()),
        new StubHttpClientFactory(), _hosty);

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/movies", CreatedAt = now, UpdatedAt = now,
        };
        _catalogId = catalog.Id;
        context.Catalogs.Add(catalog);
        context.SaveChanges();
    }

    /// <summary>A franchise with the given number of owned movies, and optionally its own artwork.</summary>
    private MovieCollection AddCollection(string name, int movies, string? poster, string? backdrop)
    {
        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();
        var collection = new MovieCollection
        {
            Id = Guid.NewGuid(),
            Provider = "tmdb",
            ProviderId = Guid.NewGuid().ToString("N"),
            Name = name,
            PosterUrl = poster is null ? null : $"https://image.tmdb.org/t/p/original/{poster}",
            BackdropUrl = backdrop is null ? null : $"https://image.tmdb.org/t/p/original/{backdrop}",
            UpdatedAt = now,
        };
        context.MovieCollections.Add(collection);
        for (var index = 0; index < movies; index++)
        {
            context.MediaItems.Add(new MediaItem
            {
                Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = _catalogId,
                Kind = MediaKind.Movie, Title = $"{name} {index}", Year = 2000 + index, CollectionId = collection.Id,
                AddedAt = now, UpdatedAt = now,
            });
        }

        context.SaveChanges();
        return collection;
    }

    /// <summary>A movie, with a pre-cached backdrop when one is asked for.</summary>
    private MediaItem AddMovie(string title, string? backdropTag, byte[]? bytes)
    {
        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = _catalogId,
            Kind = MediaKind.Movie, Title = title, Year = 2020, AddedAt = now, UpdatedAt = now,
        };
        context.MediaItems.Add(movie);

        if (backdropTag is not null)
        {
            var directory = JellyfinImageService.CacheDirectory(_hosty);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{backdropTag}.jpg");
            File.WriteAllBytes(path, bytes ?? []);
            context.ImageAssets.Add(new ImageAsset
            {
                Id = Guid.NewGuid(), MediaItemId = movie.Id, ImageType = ImageType.Backdrop, Provider = "tmdb",
                RemotePath = "https://image.tmdb.org/t/p/original/b.jpg", LocalPath = path, Tag = backdropTag, SortOrder = 0,
            });
        }

        context.SaveChanges();
        return movie;
    }

    /// <summary>
    /// Writes a collection's backdrop bytes to the cache file the image service would fetch into, using the
    /// service's own naming so the test cannot drift from it.
    /// </summary>
    private byte[] CacheCollectionArtwork(MovieCollection collection, byte[] bytes)
    {
        var directory = JellyfinImageService.CacheDirectory(_hosty);
        Directory.CreateDirectory(directory);
        var name = JellyfinImageService.CollectionCacheNames(collection).First();
        File.WriteAllBytes(Path.Combine(directory, name + ".jpg"), bytes);
        return bytes;
    }

    private sealed class StubShelf : IRecommendationShelf
    {
        public IReadOnlyList<MediaItem> Items { get; set; } = [];

        public Task<IReadOnlyList<MediaItem>> GetAsync(int appUserId, int? limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaItem>>(limit is { } wanted ? [.. Items.Take(wanted)] : Items);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        // Never invoked: every test serves from a pre-written cache file.
        public HttpClient CreateClient(string name) => new();
    }

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }
}
