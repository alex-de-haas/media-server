using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
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

    private CatalogHealthService CreateService(
        IFilesystemInspector filesystem, IHostyCoreClient core, MediaServerSettings? settings = null) =>
        new(_database, filesystem, new CatalogFileProbe(_database, new CatalogPathSandbox()),
            settings ?? new MediaServerSettings(), core, NullLogger<CatalogHealthService>.Instance);

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
        var service = CreateService(filesystem, core);

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
        var service = CreateService(filesystem, core);

        Assert.Equal(1, await service.CheckAsync(CancellationToken.None));
        Assert.NotNull((await Reload(id)).LowDiskSince);
        Assert.Equal(0, await service.CheckAsync(CancellationToken.None)); // No repeat while still low.

        filesystem.FreeBytes = 100L * 1024 * 1024 * 1024; // Plenty now.
        Assert.Equal(1, await service.CheckAsync(CancellationToken.None));
        Assert.Null((await Reload(id)).LowDiskSince);

        Assert.Equal(1, core.CountFor($"media-server:catalog-low-disk:{id}"));
    }

    [Fact]
    public async Task Unanchored_catalog_is_marked_offline_without_the_volume_notification()
    {
        // The catalog sits outside every mount this runtime injects — unreachable because the app is
        // running under the other runtime profile, not because a volume went away.
        var id = SeedCatalog("/Users/someone/media/movies");
        var settings = new MediaServerSettings
        {
            CatalogMountRoots = [new CatalogMount("media", "/mnt/catalogRoots/media")],
        };
        var filesystem = new FakeFilesystem { Reachable = false };
        var core = new RecordingCoreClient();
        var service = CreateService(filesystem, core, settings);

        Assert.Equal(1, await service.CheckAsync(CancellationToken.None));

        // Still marked offline, so file-backed actions stay blocked…
        Assert.NotNull((await Reload(id)).OfflineSince);
        // …but "the volume is unreachable, it'll come back" would point the operator the wrong way.
        Assert.Equal(0, core.CountFor($"media-server:catalog-offline:{id}"));
        Assert.Empty(core.Notifications);
    }

    [Fact]
    public async Task An_offline_catalog_stays_offline_while_its_root_is_back_but_empty()
    {
        // The docker failure this asymmetry exists for: the bind mount lost its host path, so the root
        // exists and holds nothing. Announcing "back online" here would undo the offline marker a scan
        // stamped after finding the whole catalog unreadable, and do it again every five minutes.
        var id = SeedCatalog();
        SeedLibraryFile(id, "Inception (2010)/Inception.mkv");
        var filesystem = new FakeFilesystem { Reachable = false };
        var core = new RecordingCoreClient();
        var service = CreateService(filesystem, core);

        Assert.Equal(1, await service.CheckAsync(CancellationToken.None));

        filesystem.Reachable = true; // The directory is back; its content is not.
        Assert.Equal(0, await service.CheckAsync(CancellationToken.None));
        Assert.NotNull((await Reload(id)).OfflineSince);
        Assert.Equal(0, core.CountFor($"media-server:catalog-online:{id}"));
    }

    [Fact]
    public async Task Healthy_catalog_makes_no_changes()
    {
        SeedCatalog();
        var filesystem = new FakeFilesystem { Reachable = true, FreeBytes = 500L * 1024 * 1024 * 1024 };
        var core = new RecordingCoreClient();
        var service = CreateService(filesystem, core);

        Assert.Equal(0, await service.CheckAsync(CancellationToken.None));
        Assert.Empty(core.Notifications);
    }

    private void SeedLibraryFile(Guid catalogId, string relativePath)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = catalogId,
            Kind = MediaKind.Movie,
            Title = "Inception",
            AddedAt = now,
            UpdatedAt = now,
        };
        _database.MediaItems.Add(item);
        _database.MediaSources.Add(new MediaSource
        {
            Id = Guid.NewGuid(), MediaItemId = item.Id, Container = "matroska", Path = relativePath,
            SizeBytes = 1, DurationTicks = 1, CreatedAt = now,
        });
        _database.SaveChanges();
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
        public string GetVolumeKey(string path) => "/";
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
