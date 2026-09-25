using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Pipeline;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

public sealed class LibraryImportServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _root = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "ms-import-" + Guid.NewGuid().ToString("N"));

    public LibraryImportServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        CatalogPaths.For(_root).EnsureCreated();
    }

    private LibraryImportService Service() => new(_database, new PipelineQueue(), NullLogger<LibraryImportService>.Instance);

    private Catalog SeedCatalog()
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, NamingTemplate = "{Title} ({Year})", CreatedAt = now, UpdatedAt = now };
        _database.Catalogs.Add(catalog);
        _database.SaveChanges();
        return catalog;
    }

    private void WriteFile(string relative, int bytes = 1024)
    {
        var absolute = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, new byte[bytes]);
    }

    [Fact]
    public async Task Scan_follows_the_configured_root_link_but_ignores_linked_children()
    {
        var catalog = SeedCatalog();
        WriteFile("Movie/film.mkv");
        var link = _root + "-link";
        Directory.CreateSymbolicLink(link, _root);
        Directory.CreateSymbolicLink(Path.Combine(_root, "loop"), _root);
        try
        {
            catalog.Root = link;
            await _database.SaveChangesAsync();
            var report = await Service().ImportAsync(catalog.Id, default);
            Assert.Equal(1, report!.Imported);
            Assert.Equal("Movie/film.mkv", (await _database.SourceFiles.SingleAsync()).RelativePath);
        }
        finally { Directory.Delete(Path.Combine(_root, "loop")); Directory.Delete(link); }
    }

    [Fact]
    public async Task Disc_scan_groups_clips_and_leaves_a_sibling_movie_independent()
    {
        var catalog = SeedCatalog();
        WriteFile("Film/BDMV/index.bdmv");
        WriteFile("Film/BDMV/STREAM/00001.m2ts");
        WriteFile("Film/CERTIFICATE/id.bdmv");
        WriteFile("Film/Bonus.mkv");
        var report = await Service().ImportAsync(catalog.Id, CancellationToken.None);
        Assert.Equal(2, report!.Imported);
        var disc = Assert.Single(await _database.SourceFiles.ToListAsync(), s => s.Kind == MediaSourceKind.Bluray);
        Assert.Equal("Film", disc.RelativePath);
        Assert.Equal(3072, disc.SizeBytes);
        Assert.Equal(0, (await Service().ImportAsync(catalog.Id, CancellationToken.None))!.Imported);
    }

    [Theory]
    [InlineData(CatalogType.Series)]
    [InlineData(CatalogType.Anime)]
    public async Task Series_and_anime_scans_never_import_disc_clips(CatalogType type)
    {
        var catalog = SeedCatalog();
        catalog.Type = type;
        await _database.SaveChangesAsync();
        WriteFile("Show/BDMV/index.bdmv");
        WriteFile("Show/BDMV/STREAM/00001.m2ts");
        var report = await Service().ImportAsync(catalog.Id, CancellationToken.None);
        Assert.Equal(0, report!.Imported);
        Assert.Equal(1, report.Skipped);
        Assert.Empty(await _database.SourceFiles.ToListAsync());
    }

    [Fact]
    public async Task Imports_orphan_media_and_ignores_incoming_and_nonmedia()
    {
        var catalog = SeedCatalog();
        WriteFile("Some Movie (2020)/Some Movie (2020).mkv"); // orphan media → import
        WriteFile(".incoming/abc/partial.mkv");                // transient staging → ignored
        WriteFile("Some Movie (2020)/poster.jpg");             // non-video → ignored

        var report = await Service().ImportAsync(catalog.Id, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(1, report!.Imported);
        var ingest = Assert.Single(await _database.IngestItems.ToListAsync());
        Assert.Equal(IngestStage.Identify, ingest.Stage);
        Assert.Null(ingest.DownloadId);
        var source = Assert.Single(await _database.SourceFiles.ToListAsync());
        Assert.Equal("Some Movie (2020)/Some Movie (2020).mkv", source.RelativePath);
        Assert.Null(source.DownloadId);
        Assert.Equal(ingest.Id, source.IngestItemId);
    }

    [Fact]
    public async Task Skips_already_published_and_in_flight_files()
    {
        var catalog = SeedCatalog();
        var now = DateTimeOffset.UtcNow;
        WriteFile("Published (2019)/Published (2019).mkv");
        WriteFile("Queued (2021)/Queued (2021).mkv");

        // The first file already backs a published media source.
        var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Published", AddedAt = now, UpdatedAt = now };
        _database.MediaItems.Add(movie);
        _database.MediaSources.Add(new MediaSource { Id = Guid.NewGuid(), MediaItemId = movie.Id, Container = "mkv", Path = "Published (2019)/Published (2019).mkv", CreatedAt = now });
        // The second file is already owned by an in-flight ingest.
        var ingestId = Guid.NewGuid();
        _database.IngestItems.Add(new IngestItem { Id = ingestId, CatalogId = catalog.Id, Stage = IngestStage.Identify, Status = IngestStatus.Pending, CreatedAt = now, UpdatedAt = now });
        _database.SourceFiles.Add(new SourceFile { Id = Guid.NewGuid(), IngestItemId = ingestId, RelativePath = "Queued (2021)/Queued (2021).mkv", AssignmentStatus = SourceFileAssignmentStatus.Unassigned, CreatedAt = now, UpdatedAt = now });
        await _database.SaveChangesAsync();

        var report = await Service().ImportAsync(catalog.Id, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(0, report!.Imported);
        Assert.Equal(2, report.Skipped);
        // No new ingest beyond the pre-existing in-flight one.
        Assert.Single(await _database.IngestItems.ToListAsync());
    }

    [Fact]
    public async Task Skips_orphaned_transcode_outputs()
    {
        var catalog = SeedCatalog();
        var now = DateTimeOffset.UtcNow;
        // The transcode output sits on disk with no MediaSource (e.g. its version was removed but the file
        // kept). Re-ingesting it would let the organizer rename it over the original — so it must be skipped.
        WriteFile("Movie (2020)/Movie (2020) - HEVC.mkv");
        var movie = new MediaItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Movie", AddedAt = now, UpdatedAt = now };
        _database.MediaItems.Add(movie);
        // The transcode job's input is the original source (still present); only its output version was removed.
        var original = new MediaSource { Id = Guid.NewGuid(), MediaItemId = movie.Id, Container = "mkv", Path = "Movie (2020)/Movie (2020).mkv", CreatedAt = now };
        _database.MediaSources.Add(original);
        _database.TranscodeJobs.Add(new TranscodeJob
        {
            Id = Guid.NewGuid(),
            EngineJobId = "job-1",
            MediaSourceId = original.Id,
            MediaItemId = movie.Id,
            CatalogId = catalog.Id,
            InputPath = "Movie (2020)/Movie (2020).mkv",
            OutputPath = "Movie (2020)/Movie (2020) - HEVC.mkv",
            VideoCodec = "hevc",
            HardwareAcceleration = "auto",
            State = TranscodeJobState.Completed,
            CreatedAt = now,
        });
        await _database.SaveChangesAsync();

        var report = await Service().ImportAsync(catalog.Id, CancellationToken.None);

        Assert.NotNull(report);
        Assert.Equal(0, report!.Imported);
        Assert.Equal(1, report.Skipped);
        Assert.Empty(await _database.IngestItems.ToListAsync());
    }

    [Fact]
    public async Task Re_running_is_idempotent()
    {
        var catalog = SeedCatalog();
        WriteFile("Movie (2020)/Movie (2020).mkv");

        var first = await Service().ImportAsync(catalog.Id, CancellationToken.None);
        var second = await Service().ImportAsync(catalog.Id, CancellationToken.None);

        Assert.Equal(1, first!.Imported);
        Assert.Equal(0, second!.Imported);
        Assert.Equal(1, second.Skipped);
        Assert.Single(await _database.IngestItems.ToListAsync());
    }

    [Fact]
    public async Task Returns_null_for_unknown_catalog()
    {
        Assert.Null(await Service().ImportAsync(Guid.NewGuid(), CancellationToken.None));
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
