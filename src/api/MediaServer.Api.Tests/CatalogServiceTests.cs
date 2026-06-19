using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests;

public sealed class CatalogServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "ms-catalog-" + Guid.NewGuid().ToString("N"));

    public CatalogServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        Directory.CreateDirectory(_tempRoot);
    }

    private CatalogService CreateService(MediaServerSettings? settings = null)
    {
        var filesystem = new FilesystemInspector(new HardLinker());
        return new CatalogService(_database, filesystem, settings ?? new MediaServerSettings());
    }

    private CreateCatalogRequest Request(string root) =>
        new("Movies", CatalogType.Movie, root, null, false, null);

    [Fact]
    public async Task Creates_catalog_with_files_and_library_subtrees()
    {
        var service = CreateService();

        var catalog = await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        Assert.Equal("Movies", catalog.Name);
        Assert.True(catalog.Online);
        Assert.True(catalog.FreeBytes > 0);
        Assert.True(Directory.Exists(Path.Combine(_tempRoot, "files")));
        Assert.True(Directory.Exists(Path.Combine(_tempRoot, "library")));
    }

    [Fact]
    public async Task Rejects_duplicate_root()
    {
        var service = CreateService();
        await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        await Assert.ThrowsAsync<CatalogValidationException>(() => service.CreateAsync(Request(_tempRoot), CancellationToken.None));
    }

    [Fact]
    public async Task Creates_missing_root_when_parent_is_reachable()
    {
        var service = CreateService();
        var root = Path.Combine(_tempRoot, "movies");

        var catalog = await service.CreateAsync(Request(root), CancellationToken.None);

        Assert.True(catalog.Online);
        Assert.True(Directory.Exists(root));
        Assert.True(Directory.Exists(Path.Combine(root, "files")));
        Assert.True(Directory.Exists(Path.Combine(root, "library")));
    }

    [Fact]
    public async Task Rejects_root_whose_parent_is_unreachable()
    {
        var service = CreateService();
        var unreachable = Path.Combine(_tempRoot, "does-not-exist", "movies");

        await Assert.ThrowsAsync<CatalogValidationException>(() => service.CreateAsync(Request(unreachable), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_root_outside_configured_mounts()
    {
        var settings = new MediaServerSettings { CatalogMountRoots = ["/mnt/allowed"] };
        var service = CreateService(settings);

        await Assert.ThrowsAsync<CatalogValidationException>(() => service.CreateAsync(Request(_tempRoot), CancellationToken.None));
    }

    [Fact]
    public async Task Delete_removes_metadata_but_keeps_physical_files()
    {
        var service = CreateService();
        var catalog = await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        // A published item whose library file exists on disk.
        var now = DateTimeOffset.UtcNow;
        var item = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Inception", Year = 2010, AddedAt = now, UpdatedAt = now };
        var source = new MediaSource { Id = Guid.NewGuid(), MediaItemId = item.Id, Container = "mkv", Path = "Inception (2010)/Inception.mkv", SizeBytes = 5, DurationTicks = 0, CreatedAt = now };
        _database.AddRange(item, source);
        await _database.SaveChangesAsync();

        var libraryFile = Path.Combine(_tempRoot, "library", "Inception (2010)", "Inception.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(libraryFile)!);
        await File.WriteAllTextAsync(libraryFile, "movie");

        var deleted = await service.DeleteAsync(catalog.Id, CancellationToken.None);

        Assert.True(deleted);
        // DB metadata is gone (catalog + cascade-deleted media rows)…
        Assert.Empty(await _database.Catalogs.ToListAsync());
        Assert.Empty(await _database.MediaItems.ToListAsync());
        Assert.Empty(await _database.MediaSources.ToListAsync());
        // …but the physical files (and the catalog directory) are left untouched on disk.
        Assert.True(File.Exists(libraryFile));
        Assert.True(Directory.Exists(_tempRoot));
    }

    [Fact]
    public async Task Delete_is_blocked_while_a_download_references_the_catalog()
    {
        var service = CreateService();
        var catalog = await service.CreateAsync(Request(_tempRoot), CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        _database.Downloads.Add(new Download
        {
            Id = Guid.NewGuid(),
            InfoHash = "hash-1",
            CatalogId = catalog.Id,
            State = DownloadState.Downloading,
            SavePath = _tempRoot,
            AddedAt = now,
        });
        await _database.SaveChangesAsync();

        await Assert.ThrowsAsync<CatalogInUseException>(() => service.DeleteAsync(catalog.Id, CancellationToken.None));

        // The catalog (and its directory) survive the rejected delete.
        Assert.Single(await _database.Catalogs.ToListAsync());
        Assert.True(Directory.Exists(_tempRoot));
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
