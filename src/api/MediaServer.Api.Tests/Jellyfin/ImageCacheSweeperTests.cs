using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Jellyfin;

public sealed class ImageCacheSweeperTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _appData = Path.Combine(Path.GetTempPath(), "ms-imgsweep-" + Guid.NewGuid().ToString("N"));
    private readonly string _images;

    public ImageCacheSweeperTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        _images = Path.Combine(_appData, "images");
        Directory.CreateDirectory(_images);
    }

    private ImageCacheSweeper Sweeper() => new(
        _database,
        new HostyOptions { AppId = "com.haas.media-server", CoreOrigin = "http://localhost:3001", AppDataDir = _appData },
        NullLogger<ImageCacheSweeper>.Instance);

    [Fact]
    public async Task Sweep_deletes_a_cached_file_whose_rows_are_all_gone()
    {
        var stale = WriteCached("deadbeefdeadbeef.jpg", aged: true);

        var report = await Sweeper().SweepAsync(CancellationToken.None);

        Assert.False(File.Exists(stale));
        Assert.Equal(1, report.FilesDeleted);
        Assert.Equal(1, report.FilesScanned);
        Assert.Equal(4, report.BytesReclaimed);
    }

    [Fact]
    public async Task Sweep_keeps_a_file_a_row_still_references()
    {
        var item = SeedItem();
        SeedImage(item, "abc123");
        var live = WriteCached("abc123.jpg", aged: true);

        var report = await Sweeper().SweepAsync(CancellationToken.None);

        Assert.True(File.Exists(live));
        Assert.Equal(0, report.FilesDeleted);
    }

    [Fact]
    public async Task Sweep_keeps_a_shared_tag_until_the_last_row_referencing_it_is_deleted()
    {
        // The tag is the provider's image hash, so two items reusing the same artwork share one cached file.
        var first = SeedItem();
        var second = SeedItem();
        var shared = SeedImage(first, "sharedtag");
        SeedImage(second, "sharedtag");
        var path = WriteCached("sharedtag.jpg", aged: true);

        _database.ImageAssets.Remove(shared);
        await _database.SaveChangesAsync();

        Assert.Equal(0, (await Sweeper().SweepAsync(CancellationToken.None)).FilesDeleted);
        Assert.True(File.Exists(path));

        _database.ImageAssets.RemoveRange(_database.ImageAssets);
        await _database.SaveChangesAsync();

        Assert.Equal(1, (await Sweeper().SweepAsync(CancellationToken.None)).FilesDeleted);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Sweep_keeps_live_collection_artwork_and_reclaims_the_superseded_poster()
    {
        var collection = SeedCollection("https://images.test/poster-v2.jpg", backdropUrl: null);
        var names = JellyfinImageService.CollectionCacheNames(collection).ToList();
        // Poster and backdrop slots: a collection without its own backdrop serves the poster in both.
        Assert.Equal(2, names.Count);

        var live = names.Select(name => WriteCached(name + ".jpg", aged: true)).ToList();
        var superseded = WriteCached($"collection-{collection.Id:N}-primary-0123456789abcdef.jpg", aged: true);

        var report = await Sweeper().SweepAsync(CancellationToken.None);

        Assert.All(live, path => Assert.True(File.Exists(path)));
        Assert.False(File.Exists(superseded));
        Assert.Equal(1, report.FilesDeleted);
    }

    [Fact]
    public async Task Sweep_reclaims_artwork_of_a_deleted_collection()
    {
        var collection = SeedCollection("https://images.test/poster.jpg", "https://images.test/backdrop.jpg");
        var paths = JellyfinImageService.CollectionCacheNames(collection)
            .Select(name => WriteCached(name + ".jpg", aged: true))
            .ToList();
        Assert.Equal(3, paths.Count); // primary + backdrop-under-poster-tag + backdrop.

        _database.MovieCollections.Remove(collection);
        await _database.SaveChangesAsync();

        var report = await Sweeper().SweepAsync(CancellationToken.None);

        Assert.All(paths, path => Assert.False(File.Exists(path)));
        Assert.Equal(3, report.FilesDeleted);
    }

    [Fact]
    public async Task Sweep_reclaims_stale_temp_files_from_failed_writes()
    {
        var leftover = WriteCached($"abc123.jpg.{Guid.NewGuid():N}.tmp", aged: true);

        var report = await Sweeper().SweepAsync(CancellationToken.None);

        Assert.False(File.Exists(leftover));
        Assert.Equal(1, report.FilesDeleted);
    }

    [Fact]
    public async Task Sweep_leaves_recently_written_files_alone()
    {
        // An in-flight first fetch: the binary and its temp file exist before the sweep can know they are live.
        var fresh = WriteCached("freshtag.jpg", aged: false);
        var tempFile = WriteCached($"freshtag.jpg.{Guid.NewGuid():N}.tmp", aged: false);

        var report = await Sweeper().SweepAsync(CancellationToken.None);

        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(tempFile));
        Assert.Equal(0, report.FilesDeleted);
        Assert.Equal(2, report.FilesScanned);
    }

    [Fact]
    public async Task Sweep_is_a_noop_when_nothing_has_been_cached_yet()
    {
        Directory.Delete(_images);

        var report = await Sweeper().SweepAsync(CancellationToken.None);

        Assert.Equal(new ImageCacheSweepReport(0, 0, 0), report);
    }

    private string WriteCached(string fileName, bool aged)
    {
        var path = Path.Combine(_images, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        File.SetLastWriteTimeUtc(path, aged ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow);
        return path;
    }

    private MediaItem SeedItem()
    {
        var catalog = _database.Catalogs.FirstOrDefault();
        if (catalog is null)
        {
            catalog = new Catalog
            {
                Id = Guid.NewGuid(),
                Name = "Movies",
                Type = CatalogType.Movie,
                Root = _appData,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _database.Catalogs.Add(catalog);
        }

        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            Kind = MediaKind.Movie,
            Title = "A Movie",
            AddedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.MediaItems.Add(item);
        _database.SaveChanges();
        return item;
    }

    private ImageAsset SeedImage(MediaItem item, string tag)
    {
        var image = new ImageAsset
        {
            Id = Guid.NewGuid(),
            MediaItemId = item.Id,
            ImageType = ImageType.Primary,
            Provider = "tmdb",
            RemotePath = $"https://images.test/{tag}.jpg",
            Tag = tag,
        };
        _database.ImageAssets.Add(image);
        _database.SaveChanges();
        return image;
    }

    private MovieCollection SeedCollection(string? posterUrl, string? backdropUrl)
    {
        var collection = new MovieCollection
        {
            Id = Guid.NewGuid(),
            Provider = "tmdb",
            ProviderId = "10",
            Name = "A Franchise",
            PosterUrl = posterUrl,
            BackdropUrl = backdropUrl,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.MovieCollections.Add(collection);
        _database.SaveChanges();
        return collection;
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_appData))
        {
            Directory.Delete(_appData, recursive: true);
        }
    }
}
