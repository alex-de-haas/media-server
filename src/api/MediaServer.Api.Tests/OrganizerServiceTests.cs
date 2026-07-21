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

    [Fact]
    public async Task Organize_gives_two_files_for_one_episode_distinct_versioned_paths()
    {
        var organizer = new OrganizerService(_database, new CatalogPathSandbox(), NullLogger<OrganizerService>.Instance);
        var now = DateTimeOffset.UtcNow;
        var downloadId = Guid.NewGuid();

        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Series", Type = CatalogType.Series, Root = _root, NamingTemplate = "{Title} ({Year})", CreatedAt = now, UpdatedAt = now };
        var series = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Series, Title = "Spider-Noir", AddedAt = now, UpdatedAt = now };
        var episode = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Episode, Title = "Episode 4", SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 4, AddedAt = now, UpdatedAt = now };
        var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Stage = IngestStage.Organize, Status = IngestStatus.Running, CreatedAt = now, UpdatedAt = now };

        // The same episode shipped as a regular cut (file 0) and a black-and-white cut (file 1).
        var regular = MakeSource(ingest.Id, episode.Id, $"{CatalogPaths.IncomingRelative(downloadId)}/Spider-Noir.S01E04.1080p.rus.LostFilm.TV.mkv", torrentIndex: 0, now);
        var blackWhite = MakeSource(ingest.Id, episode.Id, $"{CatalogPaths.IncomingRelative(downloadId)}/Spider-Noir.BW.S01E04.1080p.rus.LostFilm.TV.mkv", torrentIndex: 1, now);

        _database.AddRange(catalog, series, episode, ingest, regular, blackWhite);
        await _database.SaveChangesAsync();
        await WriteStagingFileAsync(regular.RelativePath);
        await WriteStagingFileAsync(blackWhite.RelativePath);

        var organized = await organizer.OrganizeAsync([regular, blackWhite], catalog, CancellationToken.None);

        // Both files organized to distinct, version-tagged canonical paths under the same season folder.
        Assert.Equal(2, organized.Count);
        Assert.Equal("Spider-Noir/Season 01/Spider-Noir S01E04 - Standard.mkv", regular.RelativePath);
        Assert.Equal("Spider-Noir/Season 01/Spider-Noir S01E04 - Black & White.mkv", blackWhite.RelativePath);
        Assert.Equal("Standard", regular.Edition);
        Assert.Equal("Black & White", blackWhite.Edition);

        // Both land on disk; neither overwrote the other.
        Assert.True(File.Exists(Path.Combine(_root, regular.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(Path.Combine(_root, blackWhite.RelativePath.Replace('/', Path.DirectorySeparatorChar))));

        // The episode's LibraryPath tracks the primary (lowest torrent index) version.
        Assert.Equal(regular.RelativePath, episode.LibraryPath);
    }

    [Fact]
    public async Task Organize_refuses_to_overwrite_a_file_backing_another_version()
    {
        var organizer = new OrganizerService(_database, new CatalogPathSandbox(), NullLogger<OrganizerService>.Instance);
        var now = DateTimeOffset.UtcNow;

        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, NamingTemplate = "{Title} ({Year})", CreatedAt = now, UpdatedAt = now };
        var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Inception", Year = 2010, AddedAt = now, UpdatedAt = now };
        // The original already exists on disk at the canonical path and is backed by a MediaSource.
        var canonical = "Inception (2010)/Inception (2010).mkv";
        _database.AddRange(catalog, movie, new MediaSource
        {
            Id = Guid.NewGuid(),
            MediaItemId = movie.Id,
            SourceFileId = Guid.NewGuid(),
            Container = "mkv",
            Path = canonical,
            CreatedAt = now,
        });

        // A freshly downloaded copy of a movie already in the library. Its staging name carries no edition
        // suffix to recover, so alone in its ingest its canonical path collides with the original's.
        var downloadId = Guid.NewGuid();
        var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Stage = IngestStage.Organize, Status = IngestStatus.Running, CreatedAt = now, UpdatedAt = now };
        var staged = $"{CatalogPaths.IncomingRelative(downloadId)}/Inception.2010.1080p/Inception.mkv";
        var orphan = new SourceFile { Id = Guid.NewGuid(), IngestItemId = ingest.Id, RelativePath = staged, SizeBytes = 5, MediaItemId = movie.Id, AssignmentStatus = SourceFileAssignmentStatus.Confirmed, CreatedAt = now, UpdatedAt = now };
        _database.AddRange(ingest, orphan);
        await _database.SaveChangesAsync();

        var originalAbsolute = Path.Combine(_root, canonical.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(originalAbsolute)!);
        await File.WriteAllTextAsync(originalAbsolute, "ORIGINAL");
        await WriteStagingFileAsync(staged); // writes "video"

        var organized = await organizer.OrganizeAsync([orphan], catalog, CancellationToken.None);

        // The collision is refused: nothing organized, the original is untouched, the newcomer stays put.
        Assert.Empty(organized);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(originalAbsolute));
        Assert.True(File.Exists(Path.Combine(_root, staged.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(staged, orphan.RelativePath);
    }

    [Fact]
    public async Task Organize_refuses_to_overwrite_a_file_another_pending_ingest_still_owns()
    {
        var organizer = new OrganizerService(_database, new CatalogPathSandbox(), NullLogger<OrganizerService>.Instance);
        var now = DateTimeOffset.UtcNow;

        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, NamingTemplate = "{Title} ({Year})", CreatedAt = now, UpdatedAt = now };
        var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Inception", Year = 2010, AddedAt = now, UpdatedAt = now };
        _database.AddRange(catalog, movie);

        // A scan over a pre-existing library queues one ingest per file, and a loose, unorganized copy
        // identifies as the same movie as the already-canonical one. Neither has reached probe, so
        // MediaSources (and TranscodeJobs) are still empty — the state a catalog created over an
        // already-populated root is in, which leaves the published-source guard inert.
        var canonical = "Inception (2010)/Inception (2010).mkv";
        var looseIngest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Stage = IngestStage.Organize, Status = IngestStatus.Running, CreatedAt = now, UpdatedAt = now };
        var loose = new SourceFile { Id = Guid.NewGuid(), IngestItemId = looseIngest.Id, RelativePath = "Inception.2010.1080p.BluRay.mkv", SizeBytes = 5, MediaItemId = movie.Id, AssignmentStatus = SourceFileAssignmentStatus.Confirmed, CreatedAt = now, UpdatedAt = now };
        var originalIngest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Stage = IngestStage.Organize, Status = IngestStatus.Pending, CreatedAt = now, UpdatedAt = now };
        var original = new SourceFile { Id = Guid.NewGuid(), IngestItemId = originalIngest.Id, RelativePath = canonical, SizeBytes = 5, MediaItemId = movie.Id, AssignmentStatus = SourceFileAssignmentStatus.Confirmed, CreatedAt = now, UpdatedAt = now };
        _database.AddRange(looseIngest, loose, originalIngest, original);
        await _database.SaveChangesAsync();

        var originalAbsolute = Path.Combine(_root, canonical.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(originalAbsolute)!);
        await File.WriteAllTextAsync(originalAbsolute, "ORIGINAL");
        await WriteStagingFileAsync(loose.RelativePath);

        var organized = await organizer.OrganizeAsync([loose], catalog, CancellationToken.None);

        // The original is a real library file another ingest still owns — never a stale leftover.
        Assert.Empty(organized);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(originalAbsolute));
        Assert.True(File.Exists(Path.Combine(_root, loose.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Theory]
    // A scanned transcode output keeps its place and its label…
    [InlineData("Inception", 2010, "Inception (2010)/Inception (2010) - HEVC 1080p.mkv", "HEVC 1080p")]
    // …including a label that itself contains a hyphen.
    [InlineData("Inception", 2010, "Inception (2010)/Inception (2010) - H-264.mkv", "H-264")]
    // …but a title containing " - " is not mistaken for a version: the canonical stem carries the hyphen.
    [InlineData("Mission Impossible - Fallout", 2018, "Mission Impossible - Fallout (2018)/Mission Impossible - Fallout (2018).mkv", null)]
    public async Task Organize_recovers_a_version_label_from_an_already_canonical_scanned_file(
        string title, int year, string relativePath, string? expectedEdition)
    {
        var organizer = new OrganizerService(_database, new CatalogPathSandbox(), NullLogger<OrganizerService>.Instance);
        var now = DateTimeOffset.UtcNow;

        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, NamingTemplate = "{Title} ({Year})", CreatedAt = now, UpdatedAt = now };
        var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = title, Year = year, AddedAt = now, UpdatedAt = now };
        var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Stage = IngestStage.Organize, Status = IngestStatus.Running, CreatedAt = now, UpdatedAt = now };
        var scanned = new SourceFile { Id = Guid.NewGuid(), IngestItemId = ingest.Id, RelativePath = relativePath, SizeBytes = 5, MediaItemId = movie.Id, AssignmentStatus = SourceFileAssignmentStatus.Confirmed, CreatedAt = now, UpdatedAt = now };
        _database.AddRange(catalog, movie, ingest, scanned);
        await _database.SaveChangesAsync();

        await WriteStagingFileAsync(relativePath);

        var organized = await organizer.OrganizeAsync([scanned], catalog, CancellationToken.None);

        // Already canonical for its edition: organized in place, nothing renamed, the label preserved.
        var result = Assert.Single(organized);
        Assert.Equal(relativePath, result.LibraryRelativePath);
        Assert.Equal(relativePath, scanned.RelativePath);
        Assert.Equal(expectedEdition, scanned.Edition);
        Assert.True(File.Exists(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static SourceFile MakeSource(Guid ingestId, Guid mediaItemId, string relativePath, int torrentIndex, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        IngestItemId = ingestId,
        RelativePath = relativePath,
        TorrentFileIndex = torrentIndex,
        SizeBytes = 5,
        MediaItemId = mediaItemId,
        AssignmentStatus = SourceFileAssignmentStatus.Confirmed,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private async Task WriteStagingFileAsync(string relativePath)
    {
        var absolute = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await File.WriteAllTextAsync(absolute, "video");
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
