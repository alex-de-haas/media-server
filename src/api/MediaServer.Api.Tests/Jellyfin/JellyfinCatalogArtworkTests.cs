using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;

namespace MediaServer.Api.Tests.Jellyfin;

/// <summary>
/// Catalogs (Jellyfin collection folders) have no artwork of their own; they borrow the backdrop of their
/// most recently added title so Infuse renders a library tile. These tests cover the image-serving side:
/// a request for a catalog's image resolves to that backdrop.
/// </summary>
public sealed class JellyfinCatalogArtworkTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly List<string> _tempFiles = [];

    [Fact]
    public async Task Catalog_image_serves_the_most_recently_added_titles_backdrop()
    {
        var now = DateTimeOffset.UtcNow;
        var catalogId = Guid.NewGuid();

        using (var context = _db.Create())
        {
            context.Catalogs.Add(new Catalog
            {
                Id = catalogId, Name = "Movies", Type = CatalogType.Movie, Root = "/movies", CreatedAt = now, UpdatedAt = now,
            });
            // Older title comes first; the newer one should win.
            SeedMovieWithBackdrop(context, catalogId, "Old", now.AddDays(-3), "oldbackdroptag00", [1, 1, 1]);
            SeedMovieWithBackdrop(context, catalogId, "New", now, "newbackdroptag00", [2, 2, 2]);
            context.SaveChanges();
        }

        var images = CreateImageService();
        // The client requests the collection folder's image by the catalog's public id.
        var payload = await images.GetImageAsync(
            JellyfinIds.Catalog(catalogId), ImageType.Primary, tag: null, index: 0, CancellationToken.None);

        Assert.NotNull(payload);
        Assert.Equal("newbackdroptag00", payload!.Tag);
        Assert.Equal([2, 2, 2], payload.Content);
    }

    [Fact]
    public async Task Catalog_without_any_backdrop_serves_nothing()
    {
        var now = DateTimeOffset.UtcNow;
        var catalogId = Guid.NewGuid();

        using (var context = _db.Create())
        {
            context.Catalogs.Add(new Catalog
            {
                Id = catalogId, Name = "Shows", Type = CatalogType.Series, Root = "/shows", CreatedAt = now, UpdatedAt = now,
            });
            context.MediaItems.Add(new MediaItem
            {
                Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalogId,
                Kind = MediaKind.Series, Title = "Breaking Bad", AddedAt = now, UpdatedAt = now,
            });
            context.SaveChanges();
        }

        var images = CreateImageService();
        var payload = await images.GetImageAsync(
            JellyfinIds.Catalog(catalogId), ImageType.Primary, tag: null, index: 0, CancellationToken.None);

        Assert.Null(payload);
    }

    [Fact]
    public async Task Unknown_id_serves_nothing()
    {
        var images = CreateImageService();
        var payload = await images.GetImageAsync(
            "ffffffffffffffffffffffffffffffff", ImageType.Primary, tag: null, index: 0, CancellationToken.None);

        Assert.Null(payload);
    }

    private void SeedMovieWithBackdrop(
        MediaServerDbContext context, Guid catalogId, string title, DateTimeOffset addedAt, string tag, byte[] bytes)
    {
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalogId,
            Kind = MediaKind.Movie, Title = title, AddedAt = addedAt, UpdatedAt = addedAt,
        };
        context.MediaItems.Add(movie);

        // Pre-cache the bytes on disk so serving needs no HTTP.
        var path = Path.Combine(Path.GetTempPath(), $"{tag}.jpg");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);

        context.ImageAssets.Add(new ImageAsset
        {
            Id = Guid.NewGuid(), MediaItemId = movie.Id, ImageType = ImageType.Backdrop,
            Provider = "tmdb", RemotePath = "https://image.tmdb.org/b.jpg", LocalPath = path, Tag = tag, SortOrder = 0,
        });
    }

    private JellyfinImageService CreateImageService()
    {
        var hosty = new HostyOptions
        {
            AppId = "com.haas.media-server",
            CoreOrigin = "http://localhost:3001",
            AppDataDir = Path.GetTempPath(),
        };
        return new JellyfinImageService(_db.Create(), new JellyfinCatalogArtwork(_db.Create()), new StubHttpClientFactory(), hosty);
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
                // Best-effort cleanup.
            }
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        // Never invoked: every test serves from a cached LocalPath.
        public HttpClient CreateClient(string name) => new();
    }
}
