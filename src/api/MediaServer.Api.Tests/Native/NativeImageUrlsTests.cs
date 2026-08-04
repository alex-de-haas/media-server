using MediaServer.Api.Data;
using MediaServer.Api.Native;
using MediaServer.Api.Tests.Jellyfin;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// Which artwork this instance offers for an item, and under which URL. A client prefers these over
/// the provider URLs the shared detail projection carries, so an absent type has to be absent rather
/// than a URL that 404s.
/// </summary>
public sealed class NativeImageUrlsTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly Guid _itemId = Guid.NewGuid();

    public NativeImageUrlsTests()
    {
        _context = _db.Create();

        var catalogId = Guid.NewGuid();
        _context.Catalogs.Add(new Catalog
        {
            Id = catalogId,
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = "/tmp/none",
        });
        _context.MediaItems.Add(new MediaItem
        {
            Id = _itemId,
            CatalogId = catalogId,
            Kind = MediaKind.Movie,
            Title = "Film",
            PublicId = Guid.NewGuid().ToString("N"),
        });
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private void AddImage(ImageType type, string tag)
    {
        _context.ImageAssets.Add(new ImageAsset
        {
            Id = Guid.NewGuid(),
            MediaItemId = _itemId,
            ImageType = type,
            Provider = "tmdb",
            RemotePath = "https://image.tmdb.org/t/p/original/x.jpg",
            Tag = tag,
        });
        _context.SaveChanges();
    }

    [Fact]
    public async Task Offers_only_the_artwork_the_instance_actually_holds()
    {
        AddImage(ImageType.Primary, "abc123");

        var images = await NativeImageEndpoints.BuildAsync(_context, _itemId, CancellationToken.None);

        Assert.Equal($"/native/v1/items/{_itemId:D}/images/primary?tag=abc123", images.Primary);
        Assert.Null(images.Backdrop);
        Assert.Null(images.Logo);
    }

    [Fact]
    public async Task Carries_the_tag_so_artwork_can_be_cached_hard()
    {
        // The tag is a content hash, which is what makes the URL safe to cache: new artwork means a
        // new tag and therefore a new URL.
        AddImage(ImageType.Backdrop, "hash-one");

        var images = await NativeImageEndpoints.BuildAsync(_context, _itemId, CancellationToken.None);

        Assert.Contains("tag=hash-one", images.Backdrop);
    }

    [Fact]
    public async Task An_item_with_no_artwork_offers_none()
    {
        var images = await NativeImageEndpoints.BuildAsync(_context, _itemId, CancellationToken.None);

        Assert.Null(images.Primary);
        Assert.Null(images.Backdrop);
        Assert.Null(images.Logo);
    }
}
