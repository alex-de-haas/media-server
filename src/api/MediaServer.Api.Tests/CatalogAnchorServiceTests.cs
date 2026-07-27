using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests;

/// <summary>
/// The startup pass that keeps <see cref="Catalog.Root"/> valid for the runtime the app is running
/// under, and the operator's explicit re-anchor.
/// </summary>
public sealed class CatalogAnchorServiceTests : IDisposable
{
    private const string DevRoot = "/Users/haas/dev-media";
    private const string DockerRoot = "/mnt/catalogRoots/dev_media_1";

    private static readonly IReadOnlyList<CatalogMount> DevMounts = [new("dev_media_1", DevRoot)];
    private static readonly IReadOnlyList<CatalogMount> DockerMounts = [new("dev_media_1", DockerRoot)];

    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly RecordingCoreClient _core = new();

    public CatalogAnchorServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
    }

    private CatalogAnchorService CreateService(IReadOnlyList<CatalogMount> mounts, IFilesystemInspector? filesystem = null) =>
        new(_database,
            new MediaServerSettings { CatalogMountRoots = mounts },
            filesystem ?? new FakeFilesystem(),
            _core,
            NullLogger<CatalogAnchorService>.Instance);

    private Catalog SeedCatalog(string root, string? label = null, string? relative = null)
    {
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = root,
            MountLabel = label,
            MountRelativePath = relative,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.Catalogs.Add(catalog);
        _database.SaveChanges();
        return catalog;
    }

    [Fact]
    public async Task Rewrites_an_anchored_root_for_the_current_runtime()
    {
        var catalog = SeedCatalog($"{DevRoot}/movies", "dev_media_1", "movies");

        var summary = await CreateService(DockerMounts).ReanchorAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.Reanchored);
        Assert.Equal(0, summary.Unanchored);
        Assert.Equal($"{DockerRoot}/movies", (await Reload(catalog.Id)).Root);
    }

    [Fact]
    public async Task Backfills_a_label_for_a_catalog_created_before_anchoring_existed()
    {
        // No label recorded, but the stored root is still inside a mount of this runtime.
        var catalog = SeedCatalog($"{DevRoot}/movies");

        var summary = await CreateService(DevMounts).ReanchorAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.BackFilled);
        var reloaded = await Reload(catalog.Id);
        Assert.Equal("dev_media_1", reloaded.MountLabel);
        Assert.Equal("movies", reloaded.MountRelativePath);
        Assert.Equal($"{DevRoot}/movies", reloaded.Root); // Path is already right for this runtime.

        // And from then on the switch is automatic.
        Assert.Equal(1, (await CreateService(DockerMounts).ReanchorAllAsync(CancellationToken.None)).Reanchored);
        Assert.Equal($"{DockerRoot}/movies", (await Reload(catalog.Id)).Root);
    }

    [Fact]
    public async Task Leaves_a_catalog_whose_mount_this_runtime_lacks_untouched_and_notifies_once()
    {
        var catalog = SeedCatalog($"{DevRoot}/movies", "dev_media_1", "movies");

        var summary = await CreateService([new CatalogMount("other", "/mnt/catalogRoots/other")])
            .ReanchorAllAsync(CancellationToken.None);

        Assert.Equal(1, summary.Unanchored);
        Assert.Equal(0, summary.Reanchored);
        // No path is guessed at: the root stays exactly as stored, so nothing is written to the wrong place.
        Assert.Equal($"{DevRoot}/movies", (await Reload(catalog.Id)).Root);
        Assert.Equal(1, _core.CountFor("media-server:catalogs-unanchored"));
    }

    [Fact]
    public async Task Leaves_standalone_catalogs_alone_when_no_mounts_are_injected()
    {
        var catalog = SeedCatalog("/srv/media/movies");

        var summary = await CreateService([]).ReanchorAllAsync(CancellationToken.None);

        Assert.Equal(new CatalogAnchorSummary(0, 0, 0), summary);
        var reloaded = await Reload(catalog.Id);
        Assert.Null(reloaded.MountLabel);
        Assert.Equal("/srv/media/movies", reloaded.Root);
        Assert.Empty(_core.Notifications);
    }

    [Fact]
    public async Task Re_anchoring_follows_the_staging_directory_of_in_flight_downloads()
    {
        var catalog = SeedCatalog($"{DevRoot}/movies", "dev_media_1", "movies");
        var downloadId = Guid.NewGuid();
        _database.Downloads.Add(new Download
        {
            Id = downloadId,
            InfoHash = "hash-1",
            CatalogId = catalog.Id,
            State = DownloadState.Downloading,
            SavePath = $"{DevRoot}/movies/.incoming/{downloadId:N}",
            AddedAt = DateTimeOffset.UtcNow,
        });
        await _database.SaveChangesAsync();

        await CreateService(DockerMounts).ReanchorAllAsync(CancellationToken.None);

        await using var fresh = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        var download = await fresh.Downloads.AsNoTracking().FirstAsync(entry => entry.Id == downloadId);
        Assert.Equal($"{DockerRoot}/movies/.incoming/{downloadId:N}", download.SavePath);
    }

    [Fact]
    public async Task A_second_pass_in_the_same_runtime_changes_nothing()
    {
        SeedCatalog($"{DevRoot}/movies", "dev_media_1", "movies");
        await CreateService(DockerMounts).ReanchorAllAsync(CancellationToken.None);

        var summary = await CreateService(DockerMounts).ReanchorAllAsync(CancellationToken.None);

        Assert.Equal(new CatalogAnchorSummary(0, 0, 0), summary);
    }

    [Fact]
    public async Task Operator_re_anchor_moves_a_catalog_to_another_mount()
    {
        var catalog = SeedCatalog($"{DevRoot}/movies", "dev_media_1", "movies");
        var service = CreateService(
        [
            new CatalogMount("dev_media_1", DevRoot),
            new CatalogMount("archive", "/Volumes/archive"),
        ]);

        var updated = await service.AnchorAsync(catalog.Id, "archive", "/films/", CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("archive", updated.MountLabel);
        Assert.Equal("films", updated.MountRelativePath); // Normalized.
        Assert.Equal("/Volumes/archive/films", updated.Root);
    }

    [Fact]
    public async Task Operator_re_anchor_refuses_to_move_a_catalog_with_an_active_download()
    {
        var catalog = SeedCatalog($"{DevRoot}/movies", "dev_media_1", "movies");
        _database.Downloads.Add(new Download
        {
            Id = Guid.NewGuid(),
            InfoHash = "hash-1",
            CatalogId = catalog.Id,
            State = DownloadState.Downloading,
            SavePath = $"{DevRoot}/movies/.incoming/x",
            AddedAt = DateTimeOffset.UtcNow,
        });
        await _database.SaveChangesAsync();
        var service = CreateService(
        [
            new CatalogMount("dev_media_1", DevRoot),
            new CatalogMount("archive", "/Volumes/archive"),
        ]);

        // The engine is writing at the old location and would not be retargeted by a path rewrite.
        await Assert.ThrowsAsync<CatalogInUseException>(
            () => service.AnchorAsync(catalog.Id, "archive", "films", CancellationToken.None));
        Assert.Equal($"{DevRoot}/movies", (await Reload(catalog.Id)).Root);
    }

    [Fact]
    public async Task Operator_re_anchor_of_an_unreachable_catalog_is_not_blocked_by_its_downloads()
    {
        var catalog = SeedCatalog($"{DevRoot}/movies", "dev_media_1", "movies");
        _database.Downloads.Add(new Download
        {
            Id = Guid.NewGuid(),
            InfoHash = "hash-1",
            CatalogId = catalog.Id,
            State = DownloadState.Seeding,
            SavePath = $"{DevRoot}/movies/.incoming/x",
            AddedAt = DateTimeOffset.UtcNow,
        });
        await _database.SaveChangesAsync();

        // The current root is gone (this is the unanchored case), so nothing can be writing there and
        // blocking the repair over a stale download row would trap the operator.
        var filesystem = new FakeFilesystem();
        filesystem.Missing.Add($"{DevRoot}/movies");
        var service = CreateService([new CatalogMount("archive", "/Volumes/archive")], filesystem);

        var updated = await service.AnchorAsync(catalog.Id, "archive", "films", CancellationToken.None);

        Assert.Equal("/Volumes/archive/films", updated!.Root);
    }

    [Fact]
    public async Task Operator_re_anchor_refuses_a_target_whose_parent_is_unreachable()
    {
        var catalog = SeedCatalog($"{DevRoot}/movies", "dev_media_1", "movies");
        // The mount itself isn't there — a typo or an unmounted volume, not a sub-folder to create.
        var filesystem = new FakeFilesystem();
        filesystem.Missing.Add("/Volumes/archive/films");
        filesystem.Missing.Add("/Volumes/archive");
        var service = CreateService([new CatalogMount("archive", "/Volumes/archive")], filesystem);

        await Assert.ThrowsAsync<CatalogValidationException>(
            () => service.AnchorAsync(catalog.Id, "archive", "films", CancellationToken.None));

        // Nothing is written until the target checks out.
        Assert.Equal($"{DevRoot}/movies", (await Reload(catalog.Id)).Root);
    }

    [Fact]
    public async Task Operator_re_anchor_rejects_an_unknown_mount_and_an_occupied_location()
    {
        var first = SeedCatalog($"{DevRoot}/movies", "dev_media_1", "movies");
        var second = SeedCatalog($"{DevRoot}/series", "dev_media_1", "series");
        var service = CreateService(DevMounts);

        await Assert.ThrowsAsync<CatalogValidationException>(
            () => service.AnchorAsync(first.Id, "nope", "movies", CancellationToken.None));
        await Assert.ThrowsAsync<CatalogValidationException>(
            () => service.AnchorAsync(second.Id, "dev_media_1", "movies", CancellationToken.None));
    }

    private async Task<Catalog> Reload(Guid id)
    {
        await using var fresh = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        return await fresh.Catalogs.AsNoTracking().FirstAsync(catalog => catalog.Id == id);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }

    /// <summary>Every path is reachable unless named in <see cref="Missing"/>, so a test can make just the
    /// one directory it cares about absent without touching the real filesystem.</summary>
    private sealed class FakeFilesystem : IFilesystemInspector
    {
        public HashSet<string> Missing { get; } = [];

        public bool DirectoryExists(string path) => !Missing.Contains(path);
        public long GetAvailableFreeBytes(string path) => 500L * 1024 * 1024 * 1024;
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

        public int CountFor(string dedupeKey) => Notifications.Count(entry => entry.DedupeKey == dedupeKey);
    }
}
