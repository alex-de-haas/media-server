using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.IO;
using MediaServer.Api.Organizer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests;

public sealed class OrganizerServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-org-" + Guid.NewGuid().ToString("N"));

    public OrganizerServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        CatalogPaths.For(_root).EnsureCreated();
    }

    [Fact]
    public async Task Organize_hardlinks_into_library_and_stop_seeding_preserves_data()
    {
        var organizer = new OrganizerService(_database, new CatalogPathSandbox(), new HardLinker(), NullLogger<OrganizerService>.Instance);
        var now = DateTimeOffset.UtcNow;

        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, NamingTemplate = "{Title} ({Year})", CreatedAt = now, UpdatedAt = now };
        var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Inception", Year = 2010, AddedAt = now, UpdatedAt = now };
        var download = new Download { Id = Guid.NewGuid(), InfoHash = "h", CatalogId = catalog.Id, State = DownloadState.Completed, SavePath = CatalogPaths.For(_root).FilesDir, AddedAt = now };
        var sourceFile = new SourceFile { Id = Guid.NewGuid(), DownloadId = download.Id, RelativePath = "Inception.2010/Inception.mkv", SizeBytes = 5, MediaItemId = movie.Id, AssignmentStatus = SourceFileAssignmentStatus.Confirmed, CreatedAt = now, UpdatedAt = now };

        _database.AddRange(catalog, movie, download, sourceFile);
        await _database.SaveChangesAsync();

        var filesPath = Path.Combine(CatalogPaths.For(_root).FilesDir, "Inception.2010", "Inception.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(filesPath)!);
        await File.WriteAllTextAsync(filesPath, "movie");

        var organized = await organizer.OrganizeAsync(download, [sourceFile], catalog, CancellationToken.None);

        var result = Assert.Single(organized);
        Assert.Equal("library/Inception (2010)/Inception (2010).mkv", result.LibraryRelativePath);
        Assert.True(File.Exists(result.AbsolutePath));
        Assert.Equal("movie", await File.ReadAllTextAsync(result.AbsolutePath));

        // Stop seeding unlinks the files/ copy; the library hardlink keeps the bytes alive.
        await organizer.UnlinkSeedCopyAsync(download, CancellationToken.None);

        Assert.False(File.Exists(filesPath));
        Assert.True(File.Exists(result.AbsolutePath));
        Assert.Equal("movie", await File.ReadAllTextAsync(result.AbsolutePath));
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
