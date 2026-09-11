using System.Data.Common;
using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Organizer;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Pipeline.Stages;
using MediaServer.Api.Tests.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests;

public sealed class OrganizerServiceTests : IDisposable
{
    private readonly ClaimCommandCapture _commands = new();
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-org-" + Guid.NewGuid().ToString("N"));

    public OrganizerServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).AddInterceptors(_commands).Options);
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

        var retried = await organizer.OrganizeAsync([regular, blackWhite], catalog, CancellationToken.None);
        Assert.Equal(organized.Select(file => file.LibraryRelativePath), retried.Select(file => file.LibraryRelativePath));
    }

    [Fact]
    public async Task Organize_allocates_a_version_path_when_the_original_is_already_published()
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

        var result = Assert.Single(organized);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(originalAbsolute));
        Assert.Equal("video", await File.ReadAllTextAsync(result.AbsolutePath));
        Assert.Equal("Inception (2010)/Inception (2010) - Version 2.mkv", orphan.RelativePath);
        Assert.Equal("Version 2", orphan.Edition);
        Assert.False(File.Exists(Path.Combine(_root, staged.Replace('/', Path.DirectorySeparatorChar))));

        // A retry keeps the allocated path rather than adding another suffix or replacing either file.
        var retried = Assert.Single(await organizer.OrganizeAsync([orphan], catalog, CancellationToken.None));
        Assert.Equal(result.LibraryRelativePath, retried.LibraryRelativePath);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(originalAbsolute));
    }

    [Fact]
    public async Task Organize_allocates_a_version_path_when_another_pending_ingest_owns_the_original()
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
        var result = Assert.Single(organized);
        Assert.Equal("ORIGINAL", await File.ReadAllTextAsync(originalAbsolute));
        Assert.Equal("video", await File.ReadAllTextAsync(result.AbsolutePath));
        Assert.Equal("Inception (2010)/Inception (2010) - Version 2.mkv", loose.RelativePath);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Organize_preserves_untracked_files_and_reserves_missing_published_paths(bool originalOnDisk)
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, NamingTemplate = "{Title} ({Year})" };
        var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Inception", Year = 2010 };
        var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id };
        var source = MakeSource(ingest.Id, movie.Id, $".incoming/{ingest.Id:N}/Inception.mkv", 0, now);
        var canonical = "Inception (2010)/Inception (2010).mkv";
        _database.AddRange(catalog, movie, ingest, source);
        if (originalOnDisk)
        {
            await WriteStagingFileAsync(canonical);
        }
        else
        {
            _database.MediaSources.Add(new MediaSource
            {
                Id = Guid.NewGuid(), MediaItemId = movie.Id, Path = canonical, Container = "mkv",
            });
        }
        await _database.SaveChangesAsync();
        await WriteStagingFileAsync(source.RelativePath);
        var organizer = new OrganizerService(_database, new CatalogPathSandbox(), NullLogger<OrganizerService>.Instance);

        var result = Assert.Single(await organizer.OrganizeAsync([source], catalog, CancellationToken.None));

        Assert.Equal("Inception (2010)/Inception (2010) - Version 2.mkv", result.LibraryRelativePath);
        Assert.Equal("video", await File.ReadAllTextAsync(result.AbsolutePath));
        Assert.Equal(originalOnDisk, File.Exists(Path.Combine(_root, canonical)));
    }

    [Theory]
    [InlineData(".incoming/download/Inception.mkv")]
    [InlineData("Inception (2010)/Inception (2010).mkv")]
    public async Task OrganizeStage_fails_when_an_assigned_file_is_missing(string path)
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, NamingTemplate = "{Title} ({Year})" };
        var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Inception", Year = 2010 };
        var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id };
        var source = MakeSource(ingest.Id, movie.Id, path, 0, now);
        _database.AddRange(catalog, movie, ingest, source);
        await _database.SaveChangesAsync();
        var context = new IngestContext { Catalog = catalog, Item = ingest, SourceFiles = [source], Paths = CatalogPaths.For(catalog) };
        var stage = new OrganizeStage(new OrganizerService(_database, new CatalogPathSandbox(), NullLogger<OrganizerService>.Instance));

        var result = Assert.IsType<StageResult.Failed>(await stage.RunAsync(context, CancellationToken.None));

        Assert.Contains(path, result.Error);
        Assert.Null(movie.LibraryPath);
        Assert.Empty(await _database.MediaSources.ToListAsync());
    }

    [Theory]
    [InlineData(".incoming/download/Inception.mkv", true)]
    [InlineData("Inception (2010)/Inception (2010).mkv", false)]
    public async Task ProbeStage_does_not_publish_staged_or_missing_files(string path, bool exists)
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root };
        var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Inception" };
        var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id };
        var source = MakeSource(ingest.Id, movie.Id, path, 0, now);
        _database.AddRange(catalog, movie, ingest, source);
        await _database.SaveChangesAsync();
        if (exists)
        {
            await WriteStagingFileAsync(path);
        }
        var context = new IngestContext { Catalog = catalog, Item = ingest, SourceFiles = [source], Paths = CatalogPaths.For(catalog) };
        var stage = new ProbeStage(new FakeMediaProbe
        {
            OnProbe = _ => throw new InvalidOperationException("An unorganized file must not be probed."),
        }, _database);

        Assert.IsType<StageResult.Failed>(await stage.RunAsync(context, CancellationToken.None));

        Assert.Empty(await _database.MediaSources.ToListAsync());
        Assert.Equal(exists, File.Exists(Path.Combine(_root, path)));
    }

    [Fact]
    public async Task Organize_queries_only_candidate_claims_and_caches_them_across_the_batch()
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, NamingTemplate = "{Title}" };
        // Different titles sanitize to the same destination, so both files visit the same reserved names.
        var first = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Movie:A" };
        var second = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Movie?A" };
        var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id };
        var firstFile = MakeSource(ingest.Id, first.Id, $".incoming/{ingest.Id:N}/first.mkv", 0, now);
        var secondFile = MakeSource(ingest.Id, second.Id, $".incoming/{ingest.Id:N}/second.mkv", 1, now);
        var canonical = "Movie A/Movie A.mkv";
        var version2 = "Movie A/Movie A - Version 2.mkv";
        var reservedIngest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id };
        _database.AddRange(catalog, first, second, ingest, firstFile, secondFile, reservedIngest,
            MakeSource(reservedIngest.Id, first.Id, version2, 0, now),
            new MediaSource { Id = Guid.NewGuid(), MediaItemId = first.Id, Path = canonical, Container = "mkv" });
        _database.MediaSources.AddRange(Enumerable.Range(0, 128).Select(index => new MediaSource
        {
            Id = Guid.NewGuid(), MediaItemId = first.Id, Path = $"Unrelated/{index}.mkv", Container = "mkv",
        }));
        await _database.SaveChangesAsync();
        await WriteStagingFileAsync(firstFile.RelativePath);
        await WriteStagingFileAsync(secondFile.RelativePath);
        var organizer = new OrganizerService(_database, new CatalogPathSandbox(), NullLogger<OrganizerService>.Instance);
        _commands.Reads.Clear();

        var organized = await organizer.OrganizeAsync([firstFile, secondFile], catalog, CancellationToken.None);

        Assert.Equal(2, organized.Count);
        Assert.Equal("Movie A/Movie A - Version 3.mkv", firstFile.RelativePath);
        Assert.Equal("Movie A/Movie A - Version 4.mkv", secondFile.RelativePath);
        Assert.All(organized, file => Assert.True(File.Exists(file.AbsolutePath)));
        // Each destination is checked once, even the two missing-file claims shared by both inputs.
        Assert.Equal(4, _commands.Reads.Count);
        foreach (var path in new[] { canonical, version2, firstFile.RelativePath, secondFile.RelativePath })
        {
            var read = Assert.Single(_commands.Reads, read => read.Parameters.Contains(path));
            Assert.Contains("\"Path\" COLLATE organizer_path =", read.Sql);
            Assert.Contains("\"RelativePath\" COLLATE organizer_path =", read.Sql);
        }

        _commands.Reads.Clear();
        await organizer.OrganizeAsync([firstFile, secondFile], catalog, CancellationToken.None);
        Assert.Empty(_commands.Reads); // An in-place retry never loads catalog claims.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Organize_missing_claims_use_filesystem_case_rules_for_unicode_paths(bool pendingClaim)
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, NamingTemplate = "{Title}" };
        var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Солярис" };
        var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id };
        var source = MakeSource(ingest.Id, movie.Id, $".incoming/{ingest.Id:N}/movie.mkv", 0, now);
        _database.AddRange(catalog, movie, ingest, source);
        if (pendingClaim)
        {
            _database.SourceFiles.Add(MakeSource(ingest.Id, movie.Id, "СОЛЯРИС/СОЛЯРИС.mkv", 1, now));
        }
        else
        {
            _database.MediaSources.Add(new MediaSource
            {
                Id = Guid.NewGuid(), MediaItemId = movie.Id, Path = "СОЛЯРИС/СОЛЯРИС.mkv", Container = "mkv",
            });
        }
        await _database.SaveChangesAsync();
        await WriteStagingFileAsync(source.RelativePath);
        // Let EF open/close the connection as in production; the collation must survive those opens.
        using var disk = new SqliteConnection($"Data Source={Path.Combine(_root, "library.db")};Pooling=False");
        disk.Open();
        _connection.BackupDatabase(disk);
        disk.Close();
        using var database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(disk).Options);
        var trackedSource = await database.SourceFiles.SingleAsync(file => file.Id == source.Id);
        var organizer = new OrganizerService(database, new CatalogPathSandbox(), NullLogger<OrganizerService>.Instance);

        var result = Assert.Single(await organizer.OrganizeAsync([trackedSource], catalog, CancellationToken.None));

        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? "Солярис/Солярис - Version 2.mkv" : "Солярис/Солярис.mkv";
        Assert.Equal(expected, result.LibraryRelativePath);
        Assert.True(File.Exists(result.AbsolutePath));
    }

    private sealed class ClaimCommandCapture : DbCommandInterceptor
    {
        public List<(string Sql, string[] Parameters)> Reads { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) &&
                (command.CommandText.Contains("\"MediaSources\"", StringComparison.Ordinal) ||
                 command.CommandText.Contains("\"SourceFiles\"", StringComparison.Ordinal)))
            {
                Reads.Add((command.CommandText, command.Parameters.Cast<DbParameter>()
                    .Select(parameter => parameter.Value).OfType<string>().ToArray()));
            }
            return ValueTask.FromResult(result);
        }
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
