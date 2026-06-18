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
    public async Task Rejects_missing_root()
    {
        var service = CreateService();
        var missing = Path.Combine(_tempRoot, "does-not-exist");

        await Assert.ThrowsAsync<CatalogValidationException>(() => service.CreateAsync(Request(missing), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_root_outside_configured_mounts()
    {
        var settings = new MediaServerSettings { CatalogMountRoots = ["/mnt/allowed"] };
        var service = CreateService(settings);

        await Assert.ThrowsAsync<CatalogValidationException>(() => service.CreateAsync(Request(_tempRoot), CancellationToken.None));
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
