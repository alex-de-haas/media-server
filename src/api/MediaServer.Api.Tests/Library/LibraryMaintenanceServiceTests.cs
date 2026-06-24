using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using MediaServer.Api.Library;
using MediaServer.Api.People;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Tests.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

public sealed class LibraryMaintenanceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-maint-" + Guid.NewGuid().ToString("N"));
    private readonly FakeMetadataProvider _metadata = new();
    private readonly RecordingCore _core = new();

    public LibraryMaintenanceServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        Directory.CreateDirectory(_root);
    }

    private LibraryMaintenanceService Service() => new(
        _database,
        new CatalogPathSandbox(),
        new FilesystemInspector(),
        new EnrichService(_database, _metadata, new MediaServerSettings { SupportedLanguages = ["en-US"] }, new PersonSyncService(_database)),
        _core,
        NullLogger<LibraryMaintenanceService>.Instance);

    private Catalog SeedCatalog(string? root = null)
    {
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = root ?? _root,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.Catalogs.Add(catalog);
        _database.SaveChanges();
        return catalog;
    }

    private Guid SeedItemWithSource(Catalog catalog, string relativePath, bool identified = true)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalog.Id,
            Kind = MediaKind.Movie,
            Title = "A Movie",
            LibraryPath = relativePath,
            IdentityProvider = identified ? "tmdb" : null,
            IdentityProviderId = identified ? "123" : null,
            AddedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.MediaItems.Add(item);
        _database.MediaSources.Add(new MediaSource
        {
            Id = Guid.NewGuid(),
            MediaItemId = item.Id,
            Container = "mkv",
            Path = relativePath,
            SizeBytes = 1024,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _database.SaveChanges();
        return item.Id;
    }

    [Fact]
    public async Task Scan_reports_missing_library_files()
    {
        var catalog = SeedCatalog();
        // One source exists on disk, one does not.
        var present = Path.Combine("library", "Present (2020)", "Present.mkv");
        var absolutePresent = Path.Combine(_root, present);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePresent)!);
        await File.WriteAllBytesAsync(absolutePresent, new byte[16]);
        SeedItemWithSource(catalog, present);
        SeedItemWithSource(catalog, Path.Combine("library", "Gone (2019)", "Gone.mkv"));

        var report = await Service().ScanAsync(CancellationToken.None);

        Assert.Equal(1, report.CatalogsScanned);
        Assert.Equal(2, report.SourcesChecked);
        Assert.Equal(1, report.MissingFiles);
        Assert.Contains("Gone", report.MissingPaths.Single());
        Assert.Equal(1, _core.CountFor("media-server:library-missing"));
    }

    [Fact]
    public async Task Scan_skips_offline_catalogs()
    {
        var offlineRoot = Path.Combine(Path.GetTempPath(), "ms-offline-" + Guid.NewGuid().ToString("N")); // never created
        var catalog = SeedCatalog(offlineRoot);
        SeedItemWithSource(catalog, Path.Combine("library", "X", "x.mkv"));

        var report = await Service().ScanAsync(CancellationToken.None);

        Assert.Equal(0, report.CatalogsScanned);
        Assert.Equal(0, report.SourcesChecked);
        Assert.Equal(0, report.MissingFiles);
        Assert.Empty(_core.Notifications);
    }

    [Fact]
    public async Task Refresh_reenriches_an_identified_item()
    {
        var catalog = SeedCatalog();
        var itemId = SeedItemWithSource(catalog, Path.Combine("library", "A", "a.mkv"));

        var refreshed = await Service().RefreshMetadataAsync(itemId, CancellationToken.None);

        Assert.True(refreshed);
        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        Assert.True(await fresh.MetadataRecords.AnyAsync(r => r.MediaItemId == itemId));
    }

    [Fact]
    public async Task Refresh_returns_false_for_unidentified_item_and_unknown_id()
    {
        var catalog = SeedCatalog();
        var unidentified = SeedItemWithSource(catalog, Path.Combine("library", "U", "u.mkv"), identified: false);

        Assert.False(await Service().RefreshMetadataAsync(unidentified, CancellationToken.None));
        Assert.False(await Service().RefreshMetadataAsync(Guid.NewGuid(), CancellationToken.None));
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

    private sealed class RecordingCore : IHostyCoreClient
    {
        public List<string?> Notifications { get; } = [];
        public bool IsEnabled => true;
        public Task<CoreBackupResult?> CreateBackupAsync(string? note, CancellationToken cancellationToken) => Task.FromResult<CoreBackupResult?>(null);
        public Task<bool> PublishNotificationAsync(CoreNotificationLevel level, string title, string? body, string? link, string? dedupeKey, string target = HostyCoreClient.BroadcastTarget, CancellationToken cancellationToken = default)
        {
            Notifications.Add(dedupeKey);
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<CoreDirectoryUser>?> ListDirectoryUsersAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CoreDirectoryUser>?>([]);
        public int CountFor(string dedupeKey) => Notifications.Count(n => n == dedupeKey);
    }
}
