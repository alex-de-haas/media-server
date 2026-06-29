using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// Coverage for <see cref="LibrarySourceService"/>: pinning/clearing the default source, and renaming a
/// movie source's version — which now renames the file on disk (locked <c>Title (Year)</c> stem), syncs the
/// stored label + originating <see cref="SourceFile"/>, validates characters, and rejects collisions. Backed
/// by in-memory SQLite plus a real temp catalog root so the file moves are exercised end to end.
/// </summary>
public sealed class LibrarySourceServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _context;
    private readonly LibrarySourceService _service;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-source-" + Guid.NewGuid().ToString("N"));

    private const string MovieFolder = "The Rock (1996)";
    private const string SourceARelative = MovieFolder + "/The Rock (1996) - HEVC 1080p.mkv";
    private const string SourceBRelative = MovieFolder + "/The Rock (1996) - Remux.mkv";
    private const string GhostRelative = MovieFolder + "/The Rock (1996) - Ghost.mkv";
    private const string OtherRelative = "Heat (1995)/Heat (1995).mkv";

    private readonly Guid _movieId = Guid.NewGuid();
    private readonly Guid _sourceA = Guid.NewGuid();
    private readonly Guid _sourceB = Guid.NewGuid();
    private readonly Guid _ghostSource = Guid.NewGuid();
    private readonly Guid _sourceFileId = Guid.NewGuid();
    private readonly Guid _otherMovieId = Guid.NewGuid();
    private readonly Guid _otherSource = Guid.NewGuid();
    private readonly Guid _videoId = Guid.NewGuid();
    private readonly Guid _videoSource = Guid.NewGuid();

    public LibrarySourceServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = CreateContext();
        _context.Database.Migrate();
        CatalogPaths.For(_root).EnsureCreated();
        Seed();
        _service = new LibrarySourceService(_context, new CatalogPathSandbox(), NullLogger<LibrarySourceService>.Instance);
    }

    [Fact]
    public async Task SetDefaultSource_pins_a_source_that_belongs_to_the_item()
    {
        var ok = await _service.SetDefaultSourceAsync(_movieId, _sourceB, CancellationToken.None);

        Assert.True(ok);
        await using var verify = CreateContext();
        Assert.Equal(_sourceB, (await verify.MediaItems.FindAsync(_movieId))!.DefaultSourceId);
    }

    [Fact]
    public async Task SetDefaultSource_rejects_a_source_from_another_item()
    {
        var ok = await _service.SetDefaultSourceAsync(_movieId, _otherSource, CancellationToken.None);

        Assert.False(ok);
        await using var verify = CreateContext();
        Assert.Null((await verify.MediaItems.FindAsync(_movieId))!.DefaultSourceId);
    }

    [Fact]
    public async Task SetDefaultSource_returns_false_for_unknown_item()
    {
        Assert.False(await _service.SetDefaultSourceAsync(Guid.NewGuid(), _sourceA, CancellationToken.None));
    }

    [Fact]
    public async Task RenameVersion_renames_the_file_and_syncs_db_and_source_file()
    {
        var result = await _service.RenameVersionAsync(_sourceA, "HDR 1080p", CancellationToken.None);

        Assert.Equal(RenameVersionResult.Kind.Ok, result.Status);

        const string expected = MovieFolder + "/The Rock (1996) - HDR 1080p.mkv";
        Assert.True(File.Exists(Absolute(expected)));
        Assert.False(File.Exists(Absolute(SourceARelative)));

        await using var verify = CreateContext();
        var source = (await verify.MediaSources.FindAsync(_sourceA))!;
        Assert.Equal(expected, source.Path);
        Assert.Equal("HDR 1080p", source.VersionName);

        var sourceFile = (await verify.SourceFiles.FindAsync(_sourceFileId))!;
        Assert.Equal(expected, sourceFile.RelativePath);
        Assert.Equal("HDR 1080p", sourceFile.Edition);

        // The item's primary-version pointer followed the file.
        Assert.Equal(expected, (await verify.MediaItems.FindAsync(_movieId))!.LibraryPath);
    }

    [Fact]
    public async Task RenameVersion_trims_and_collapses_whitespace()
    {
        var result = await _service.RenameVersionAsync(_sourceB, "  Remux   1080p  ", CancellationToken.None);

        Assert.Equal(RenameVersionResult.Kind.Ok, result.Status);

        const string expected = MovieFolder + "/The Rock (1996) - Remux 1080p.mkv";
        Assert.True(File.Exists(Absolute(expected)));
        await using var verify = CreateContext();
        Assert.Equal("Remux 1080p", (await verify.MediaSources.FindAsync(_sourceB))!.VersionName);
    }

    [Fact]
    public async Task RenameVersion_clears_to_the_bare_name()
    {
        var result = await _service.RenameVersionAsync(_sourceA, "  ", CancellationToken.None);

        Assert.Equal(RenameVersionResult.Kind.Ok, result.Status);

        const string expected = MovieFolder + "/The Rock (1996).mkv";
        Assert.True(File.Exists(Absolute(expected)));
        Assert.False(File.Exists(Absolute(SourceARelative)));
        await using var verify = CreateContext();
        var source = (await verify.MediaSources.FindAsync(_sourceA))!;
        Assert.Equal(expected, source.Path);
        Assert.Null(source.VersionName);
    }

    [Fact]
    public async Task RenameVersion_reconciles_a_drifted_label_without_moving_when_the_path_is_unchanged()
    {
        // The stored label ("1080p") differs from the on-disk suffix ("HEVC 1080p"); renaming to the actual
        // suffix is a no-op move that only reconciles the label.
        var result = await _service.RenameVersionAsync(_sourceA, "HEVC 1080p", CancellationToken.None);

        Assert.Equal(RenameVersionResult.Kind.Ok, result.Status);
        Assert.True(File.Exists(Absolute(SourceARelative)));
        await using var verify = CreateContext();
        var source = (await verify.MediaSources.FindAsync(_sourceA))!;
        Assert.Equal(SourceARelative, source.Path);
        Assert.Equal("HEVC 1080p", source.VersionName);
    }

    [Fact]
    public async Task RenameVersion_returns_conflict_when_the_target_name_is_taken()
    {
        // Renaming B onto A's existing path must not clobber A.
        var result = await _service.RenameVersionAsync(_sourceB, "HEVC 1080p", CancellationToken.None);

        Assert.Equal(RenameVersionResult.Kind.Conflict, result.Status);
        Assert.True(File.Exists(Absolute(SourceARelative)));
        Assert.True(File.Exists(Absolute(SourceBRelative)));
        await using var verify = CreateContext();
        Assert.Equal(SourceBRelative, (await verify.MediaSources.FindAsync(_sourceB))!.Path);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("Director: cut")]
    [InlineData("what?")]
    public async Task RenameVersion_rejects_filename_unsafe_characters(string name)
    {
        var result = await _service.RenameVersionAsync(_sourceA, name, CancellationToken.None);

        Assert.Equal(RenameVersionResult.Kind.InvalidName, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.True(File.Exists(Absolute(SourceARelative)));
        await using var verify = CreateContext();
        Assert.Equal("1080p", (await verify.MediaSources.FindAsync(_sourceA))!.VersionName);
    }

    [Fact]
    public async Task RenameVersion_returns_missing_file_when_the_source_file_is_gone()
    {
        var result = await _service.RenameVersionAsync(_ghostSource, "Phantom", CancellationToken.None);

        Assert.Equal(RenameVersionResult.Kind.MissingFile, result.Status);
        await using var verify = CreateContext();
        Assert.Equal(GhostRelative, (await verify.MediaSources.FindAsync(_ghostSource))!.Path);
    }

    [Fact]
    public async Task RenameVersion_rejects_a_non_movie_source()
    {
        var result = await _service.RenameVersionAsync(_videoSource, "x", CancellationToken.None);

        Assert.Equal(RenameVersionResult.Kind.Unsupported, result.Status);
    }

    [Fact]
    public async Task RenameVersion_returns_not_found_for_unknown_source()
    {
        var result = await _service.RenameVersionAsync(Guid.NewGuid(), "x", CancellationToken.None);

        Assert.Equal(RenameVersionResult.Kind.NotFound, result.Status);
    }

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(), Name = "Films", Type = CatalogType.Movie, Root = _root,
            NamingTemplate = "{Title} ({Year})", CreatedAt = now, UpdatedAt = now,
        };
        var movie = new MediaItem
        {
            Id = _movieId, PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id, Kind = MediaKind.Movie,
            Title = "The Rock", Year = 1996, LibraryPath = SourceARelative, AddedAt = now, UpdatedAt = now,
        };
        var other = new MediaItem
        {
            Id = _otherMovieId, PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id, Kind = MediaKind.Movie,
            Title = "Heat", Year = 1995, AddedAt = now, UpdatedAt = now,
        };
        var video = new MediaItem
        {
            Id = _videoId, PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id, Kind = MediaKind.Video,
            Title = "Home Clip", AddedAt = now, UpdatedAt = now,
        };
        var ingest = new IngestItem
        {
            Id = Guid.NewGuid(), CatalogId = catalog.Id, Stage = IngestStage.Identify, Status = IngestStatus.Pending,
            CreatedAt = now, UpdatedAt = now,
        };

        _context.Catalogs.Add(catalog);
        _context.MediaItems.AddRange(movie, other, video);
        _context.IngestItems.Add(ingest);
        _context.SourceFiles.Add(new SourceFile
        {
            Id = _sourceFileId, IngestItemId = ingest.Id, MediaItemId = _movieId, RelativePath = SourceARelative,
            Edition = "1080p", AssignmentStatus = SourceFileAssignmentStatus.Confirmed, CreatedAt = now, UpdatedAt = now,
        });
        _context.MediaSources.AddRange(
            new MediaSource { Id = _sourceA, MediaItemId = _movieId, SourceFileId = _sourceFileId, Container = "mkv", Path = SourceARelative, VersionName = "1080p", CreatedAt = now },
            new MediaSource { Id = _sourceB, MediaItemId = _movieId, Container = "mkv", Path = SourceBRelative, VersionName = "Remux", CreatedAt = now },
            new MediaSource { Id = _ghostSource, MediaItemId = _movieId, Container = "mkv", Path = GhostRelative, VersionName = "Ghost", CreatedAt = now },
            new MediaSource { Id = _otherSource, MediaItemId = _otherMovieId, Container = "mkv", Path = OtherRelative, CreatedAt = now },
            new MediaSource { Id = _videoSource, MediaItemId = _videoId, Container = "mkv", Path = "Home Clip.mkv", CreatedAt = now });
        _context.SaveChanges();

        // Files on disk for every source except the deliberately-missing "ghost".
        WriteFile(SourceARelative);
        WriteFile(SourceBRelative);
        WriteFile(OtherRelative);
    }

    private MediaServerDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);

    private string Absolute(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private void WriteFile(string relative)
    {
        var absolute = Absolute(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, "payload");
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
