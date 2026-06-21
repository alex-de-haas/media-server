using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
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
    public async Task Organize_moves_the_file_into_the_canonical_layout_and_clears_staging()
    {
        var organizer = new OrganizerService(_database, new CatalogPathSandbox(), NullLogger<OrganizerService>.Instance);
        var now = DateTimeOffset.UtcNow;
        var downloadId = Guid.NewGuid();

        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, NamingTemplate = "{Title} ({Year})", CreatedAt = now, UpdatedAt = now };
        var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Inception", Year = 2010, AddedAt = now, UpdatedAt = now };
        var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Stage = IngestStage.Organize, Status = IngestStatus.Running, CreatedAt = now, UpdatedAt = now };
        var stagingRelative = $"{CatalogPaths.IncomingRelative(downloadId)}/Inception.2010/Inception.mkv";
        var sourceFile = new SourceFile { Id = Guid.NewGuid(), IngestItemId = ingest.Id, RelativePath = stagingRelative, SizeBytes = 5, MediaItemId = movie.Id, AssignmentStatus = SourceFileAssignmentStatus.Confirmed, CreatedAt = now, UpdatedAt = now };

        _database.AddRange(catalog, movie, ingest, sourceFile);
        await _database.SaveChangesAsync();

        var stagingAbsolute = Path.Combine(_root, stagingRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(stagingAbsolute)!);
        await File.WriteAllTextAsync(stagingAbsolute, "movie");

        var organized = await organizer.OrganizeAsync([sourceFile], catalog, CancellationToken.None);

        var result = Assert.Single(organized);
        // Canonical, root-relative (no library/ prefix), extension preserved.
        Assert.Equal("Inception (2010)/Inception (2010).mkv", result.LibraryRelativePath);
        Assert.True(File.Exists(result.AbsolutePath));
        Assert.Equal("movie", await File.ReadAllTextAsync(result.AbsolutePath));

        // The move leaves nothing behind: the source file is gone and the .incoming staging folder is cleaned.
        Assert.False(File.Exists(stagingAbsolute));
        Assert.False(Directory.Exists(Path.Combine(_root, CatalogPaths.IncomingDirName, downloadId.ToString("N"))));

        // The source-file row and the media item now point at the canonical path.
        Assert.Equal("Inception (2010)/Inception (2010).mkv", sourceFile.RelativePath);
        Assert.Equal("Inception (2010)/Inception (2010).mkv", movie.LibraryPath);
    }

    [Fact]
    public async Task Organize_in_place_keeps_an_already_canonical_scanned_file()
    {
        var organizer = new OrganizerService(_database, new CatalogPathSandbox(), NullLogger<OrganizerService>.Instance);
        var now = DateTimeOffset.UtcNow;

        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, NamingTemplate = "{Title} ({Year})", CreatedAt = now, UpdatedAt = now };
        var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Inception", Year = 2010, AddedAt = now, UpdatedAt = now };
        var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Stage = IngestStage.Organize, Status = IngestStatus.Running, CreatedAt = now, UpdatedAt = now };
        // A scanned file already sits at the canonical path (no .incoming prefix).
        var canonical = "Inception (2010)/Inception (2010).mkv";
        var sourceFile = new SourceFile { Id = Guid.NewGuid(), IngestItemId = ingest.Id, RelativePath = canonical, SizeBytes = 5, MediaItemId = movie.Id, AssignmentStatus = SourceFileAssignmentStatus.Confirmed, CreatedAt = now, UpdatedAt = now };

        _database.AddRange(catalog, movie, ingest, sourceFile);
        await _database.SaveChangesAsync();

        var absolute = Path.Combine(_root, canonical.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, "movie");

        var organized = await organizer.OrganizeAsync([sourceFile], catalog, CancellationToken.None);

        var result = Assert.Single(organized);
        Assert.Equal(canonical, result.LibraryRelativePath);
        Assert.True(File.Exists(absolute));
        Assert.Equal("movie", await File.ReadAllTextAsync(absolute));
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
