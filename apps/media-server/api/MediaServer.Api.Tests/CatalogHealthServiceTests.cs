using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests;

public sealed class CatalogHealthServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;

    public CatalogHealthServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
    }

    private Guid SeedCatalog(string root = "/mnt/movies")
    {
        var id = Guid.NewGuid();
        _database.Catalogs.Add(new Catalog
        {
            Id = id,
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = root,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _database.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Marks_offline_and_notifies_once_then_recovers()
    {
        var id = SeedCatalog();
        var filesystem = new FakeFilesystem { Reachable = false };
        var core = new RecordingCoreClient();
        var service = new CatalogHealthService(_database, filesystem, core, NullLogger<CatalogHealthService>.Instance);

        // First check: offline → marked + notified.
        Assert.Equal(1, await service.CheckAsync(CancellationToken.None));
        Assert.NotNull((await Reload(id)).OfflineSince);

        // Second check while still offline: no further change, no second notification.
        Assert.Equal(0, await service.CheckAsync(CancellationToken.None));

        // Root returns: cleared + recovery notification.
        filesystem.Reachable = true;
        Assert.Equal(1, await service.CheckAsync(CancellationToken.None));
        Assert.Null((await Reload(id)).OfflineSince);

        Assert.Equal(1, core.CountFor($"media-server:catalog-offline:{id}"));
        Assert.Equal(1, core.CountFor($"media-server:catalog-online:{id}"));
    }

    [Fact]
    public async Task Warns_once_on_low_disk_and_clears_on_recovery()
    {
        var id = SeedCatalog();
        var filesystem = new FakeFilesystem { Reachable = true, FreeBytes = 1L * 1024 * 1024 * 1024 }; // 1 GiB < 5 GiB threshold.
        var core = new RecordingCoreClient();
        var service = new CatalogHealthService(_database, filesystem, core, NullLogger<CatalogHealthService>.Instance);

        Assert.Equal(1, await service.CheckAsync(CancellationToken.None));
        Assert.NotNull((await Reload(id)).LowDiskSince);
        Assert.Equal(0, await service.CheckAsync(CancellationToken.None)); // No repeat while still low.

        filesystem.FreeBytes = 100L * 1024 * 1024 * 1024; // Plenty now.
        Assert.Equal(1, await service.CheckAsync(CancellationToken.None));
        Assert.Null((await Reload(id)).LowDiskSince);

        Assert.Equal(1, core.CountFor($"media-server:catalog-low-disk:{id}"));
    }

    [Fact]
    public async Task Healthy_catalog_makes_no_changes()
    {
        SeedCatalog();
        var filesystem = new FakeFilesystem { Reachable = true, FreeBytes = 500L * 1024 * 1024 * 1024 };
        var core = new RecordingCoreClient();
        var service = new CatalogHealthService(_database, filesystem, core, NullLogger<CatalogHealthService>.Instance);

        Assert.Equal(0, await service.CheckAsync(CancellationToken.None));
        Assert.Empty(core.Notifications);
    }

    private async Task<Catalog> Reload(Guid id)
    {
        await using var fresh = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        return await fresh.Catalogs.AsNoTracking().FirstAsync(c => c.Id == id);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }

    private sealed class FakeFilesystem : IFilesystemInspector
    {
        public bool Reachable { get; set; } = true;
        public long FreeBytes { get; set; } = 500L * 1024 * 1024 * 1024;

        public bool DirectoryExists(string path) => Reachable;
        public bool AreSameFilesystem(string directoryA, string directoryB) => true;
        public long GetAvailableFreeBytes(string path) => FreeBytes;
    }

    private sealed class RecordingCoreClient : IHostyCoreClient
    {
        public List<(CoreNotificationLevel Level, string Title, string? DedupeKey)> Notifications { get; } = [];

        public bool IsEnabled => true;

        public Task<CoreBackupResult?> CreateBackupAsync(string? note, CancellationToken cancellationToken) =>
            Task.FromResult<CoreBackupResult?>(new CoreBackupResult("completed", "bkp"));

        public Task<bool> PublishNotificationAsync(
            CoreNotificationLevel level, string title, string? body, string? link, string? dedupeKey,
            string target = HostyCoreClient.BroadcastTarget, CancellationToken cancellationToken = default)
        {
            Notifications.Add((level, title, dedupeKey));
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<CoreDirectoryUser>?> ListDirectoryUsersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CoreDirectoryUser>?>([]);

        public int CountFor(string dedupeKey) => Notifications.Count(n => n.DedupeKey == dedupeKey);
    }
}
