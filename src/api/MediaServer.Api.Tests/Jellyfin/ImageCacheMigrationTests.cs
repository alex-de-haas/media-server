using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Jellyfin;

public sealed class ImageCacheMigrationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-imgmigrate-" + Guid.NewGuid().ToString("N"));
    private readonly string _legacyImages;

    public ImageCacheMigrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        _legacyImages = Path.Combine(_root, "data", "images");
        Directory.CreateDirectory(_legacyImages);
    }

    private HostyOptions Options(bool withCache = true) => new()
    {
        AppId = "com.haas.media-server",
        CoreOrigin = "http://localhost:3001",
        AppDataDir = Path.Combine(_root, "data"),
        AppCacheDirOverride = withCache ? Path.Combine(_root, "cache") : null,
    };

    [Fact]
    public void Migration_moves_artwork_repoints_rows_and_removes_the_legacy_directory()
    {
        var legacy = WriteLegacy("abc123.jpg", "poster");
        // A failed write's leftover beside it: garbage at either location, deleted in passing.
        WriteLegacy($"abc123.jpg.{Guid.NewGuid():N}.tmp", "leftover");
        var image = SeedImage("abc123", localPath: legacy);

        JellyfinImageService.MigrateCache(Options(), _database, NullLogger.Instance);

        var migrated = Path.Combine(_root, "cache", "images", "abc123.jpg");
        Assert.Equal("poster", File.ReadAllText(migrated));
        Assert.False(Directory.Exists(_legacyImages));
        Assert.Equal(migrated, image.LocalPath);
    }

    [Fact]
    public void Migration_is_idempotent_and_the_destination_wins()
    {
        // The same name on both sides — a crash between move and delete on a copying filesystem —
        // with the legacy side stale: the destination copy must survive.
        WriteLegacy("abc123.jpg", "stale");
        var destination = Path.Combine(_root, "cache", "images", "abc123.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "fresh");

        JellyfinImageService.MigrateCache(Options(), _database, NullLogger.Instance);
        Assert.Equal("fresh", File.ReadAllText(destination));
        Assert.False(Directory.Exists(_legacyImages));

        // A second run — every start after the legacy directory is gone — changes nothing.
        JellyfinImageService.MigrateCache(Options(), _database, NullLogger.Instance);
        Assert.Equal("fresh", File.ReadAllText(destination));
    }

    [Fact]
    public void Migration_is_a_noop_when_cache_and_data_share_a_root()
    {
        // The old-Core fallback: no HOSTY_APP_CACHE_DIR, so the cache already sits under data and
        // "legacy" and "current" are the same directory. Nothing may move, be deleted, or be repointed.
        var path = WriteLegacy("abc123.jpg", "poster");
        var image = SeedImage("abc123", localPath: path);

        JellyfinImageService.MigrateCache(Options(withCache: false), _database, NullLogger.Instance);

        Assert.Equal("poster", File.ReadAllText(path));
        Assert.Equal(path, image.LocalPath);
    }

    private string WriteLegacy(string fileName, string content)
    {
        var path = Path.Combine(_legacyImages, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private ImageAsset SeedImage(string tag, string localPath)
    {
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = Path.Combine(_root, "media"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.Catalogs.Add(catalog);
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
        var image = new ImageAsset
        {
            Id = Guid.NewGuid(),
            MediaItemId = item.Id,
            ImageType = ImageType.Primary,
            Provider = "tmdb",
            RemotePath = $"https://images.test/{tag}.jpg",
            Tag = tag,
            LocalPath = localPath,
        };
        _database.ImageAssets.Add(image);
        _database.SaveChanges();
        return image;
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
