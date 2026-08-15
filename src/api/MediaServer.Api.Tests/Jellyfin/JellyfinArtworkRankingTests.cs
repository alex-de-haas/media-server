using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Jellyfin;

/// <summary>
/// The Jellyfin artwork contract has two halves that must agree: the mapper advertises tags, and the image
/// service serves the bytes. A client may address artwork by tag — safe either way — or by <em>index</em>
/// into the advertised <c>BackdropImageTags</c> list, so if only one half ranked by language, index 1 would
/// serve the image the client was told is index 0.
/// </summary>
public sealed class JellyfinArtworkRankingTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly List<string> _tempFiles = [];
    private string _publicId = string.Empty;
    private Guid _itemId;

    public JellyfinArtworkRankingTests()
    {
        using var context = _db.Create();
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/movies",
            CreatedAt = now, UpdatedAt = now,
        };
        context.Catalogs.Add(catalog);

        var movie = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id,
            Kind = MediaKind.Movie, Title = "John Wick: Chapter 3", AddedAt = now, UpdatedAt = now,
        };
        context.MediaItems.Add(movie);
        _itemId = movie.Id;
        _publicId = movie.PublicId!;

        // Sort orders deliberately oppose the language ranking, so a surface still ordering by them fails.
        Add(context, ImageType.Primary, "postertextless01", language: null, sortOrder: 0);
        Add(context, ImageType.Primary, "posterru00000001", language: "ru", sortOrder: 5);
        Add(context, ImageType.Backdrop, "backdropru000001", language: "ru", sortOrder: 0);
        Add(context, ImageType.Backdrop, "backdroptext0001", language: null, sortOrder: 5);
        context.SaveChanges();
    }

    [Fact]
    public async Task An_index_addressed_backdrop_resolves_to_the_tag_advertised_at_that_index()
    {
        var advertised = (await MapAsync()).BackdropImageTags;
        var images = ImageService();

        Assert.Equal(["backdroptext0001", "backdropru000001"], advertised);
        for (var index = 0; index < advertised!.Count; index++)
        {
            var payload = await images.GetImageAsync(_publicId, ImageType.Backdrop, tag: null, index: index, CancellationToken.None);
            Assert.Equal(advertised[index], payload?.Tag);
        }
    }

    [Fact]
    public async Task An_untagged_primary_request_serves_the_advertised_poster()
    {
        var advertised = (await MapAsync()).ImageTags?["Primary"];
        var payload = await ImageService().GetImageAsync(_publicId, ImageType.Primary, tag: null, index: 0, CancellationToken.None);

        Assert.Equal("posterru00000001", advertised);
        Assert.Equal(advertised, payload?.Tag);
    }

    [Fact]
    public async Task A_tag_addressed_request_still_serves_exactly_that_image()
    {
        // Ranking decides what is offered, never what a tag means: a client holding an older tag keeps
        // getting the image it asked for rather than today's winner.
        var payload = await ImageService().GetImageAsync(
            _publicId, ImageType.Primary, tag: "postertextless01", index: 0, CancellationToken.None);

        Assert.Equal("postertextless01", payload?.Tag);
    }

    [Fact]
    public async Task A_pinned_poster_is_served_for_an_untagged_request_too()
    {
        using (var context = _db.Create())
        {
            context.MediaItems.First(item => item.Id == _itemId).PreferredPosterTag = "postertextless01";
            context.SaveChanges();
        }

        var advertised = (await MapAsync()).ImageTags?["Primary"];
        var payload = await ImageService().GetImageAsync(_publicId, ImageType.Primary, tag: null, index: 0, CancellationToken.None);

        Assert.Equal("postertextless01", advertised);
        Assert.Equal(advertised, payload?.Tag);
    }

    private async Task<BaseItemDto> MapAsync()
    {
        var library = new JellyfinLibraryService(
            _db.Create(), new JellyfinItemMapper(ServerContext(), Settings), new JellyfinCatalogArtwork(_db.Create(), Settings),
            new JellyfinCollectionService(_db.Create()), new JellyfinPersonService(_db.Create()), new EmptyShelf(),
            new Api.Library.UserDataService(_db.Create(), TimeProvider.System), Settings);
        return (await library.GetItemAsync(_publicId, includeMediaSources: false, appUserId: null, CancellationToken.None))!;
    }

    private JellyfinImageService ImageService() => new(
        _db.Create(), new JellyfinCatalogArtwork(_db.Create(), Settings), new JellyfinCollectionService(_db.Create()),
        new JellyfinPersonService(_db.Create()), new NeverCalledHttpClientFactory(), Hosty, Settings);

    private static Api.Configuration.MediaServerSettings Settings => TestSettings.For("ru-RU", "en-US");

    private static HostyOptions Hosty => new()
    {
        AppId = "com.haas.media-server",
        CoreOrigin = "http://localhost:3001",
        AppDataDir = Path.GetTempPath(),
    };

    private JellyfinServerContext ServerContext() => new(Hosty, Settings);

    private void Add(MediaServerDbContext context, ImageType type, string tag, string? language, int sortOrder)
    {
        // Pre-cached on disk so serving the bytes needs no HTTP, and each image gets its own path — one item
        // may not hold the same remote path twice.
        var path = Path.Combine(Path.GetTempPath(), $"{tag}.jpg");
        File.WriteAllBytes(path, [1, 2, 3]);
        _tempFiles.Add(path);

        context.ImageAssets.Add(new ImageAsset
        {
            Id = Guid.NewGuid(),
            MediaItemId = _itemId,
            ImageType = type,
            Language = language,
            Provider = "tmdb",
            RemotePath = $"https://image.tmdb.org/{tag}.jpg",
            LocalPath = path,
            Tag = tag,
            SortOrder = sortOrder,
        });
    }

    private sealed class NeverCalledHttpClientFactory : IHttpClientFactory
    {
        // Every image in this fixture is cached on disk, so a fetch would mean the resolver missed it.
        public HttpClient CreateClient(string name) => throw new InvalidOperationException(
            "Artwork should have been served from the pre-written cache file.");
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var path in _tempFiles)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless.
            }
        }
    }
}
