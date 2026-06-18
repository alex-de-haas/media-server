using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Torrents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Torrents;

public sealed class DownloadDeletionServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-del-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingEngine _engine = new();

    public DownloadDeletionServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        Directory.CreateDirectory(_root);
    }

    private DownloadDeletionService Service() => new(
        _database,
        _engine,
        new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance),
        NullLogger<DownloadDeletionService>.Instance);

    private Catalog SeedCatalog()
    {
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        _database.Catalogs.Add(catalog);
        _database.SaveChanges();
        return catalog;
    }

    private Download SeedDownload(Guid catalogId, DownloadState state)
    {
        var download = new Download
        {
            Id = Guid.NewGuid(),
            InfoHash = Guid.NewGuid().ToString("N"),
            Name = "The Movie 2020",
            CatalogId = catalogId,
            SourceType = TorrentSourceType.Magnet,
            State = state,
            SavePath = Path.Combine(_root, "files"),
            AddedAt = DateTimeOffset.UtcNow,
        };
        _database.Downloads.Add(download);
        _database.SaveChanges();
        return download;
    }

    // A fully published movie: media item + source + stream + metadata + a physical library file + a
    // confirmed source file + a Done ingest row.
    private (Guid MovieId, Guid IngestId, string LibraryFile) SeedPublishedMovie(Catalog catalog, Download download)
    {
        var relative = Path.Combine("library", "The Movie (2020)", "The Movie.mkv");
        var absolute = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllBytes(absolute, new byte[32]);

        var movie = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "The Movie", PublicId = "pub-movie",
            LibraryPath = relative, IdentityProvider = "tmdb", IdentityProviderId = "1", AddedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.MediaItems.Add(movie);
        var source = new MediaSource { Id = Guid.NewGuid(), MediaItemId = movie.Id, Container = "mkv", Path = relative, SizeBytes = 32, DurationTicks = 1, CreatedAt = DateTimeOffset.UtcNow };
        _database.MediaSources.Add(source);
        _database.MediaStreams.Add(new MediaStream { Id = Guid.NewGuid(), MediaSourceId = source.Id, StreamType = StreamType.Video, Index = 0 });
        _database.MetadataRecords.Add(new MetadataRecord { Id = Guid.NewGuid(), MediaItemId = movie.Id, Provider = "tmdb", Language = "en-US" });
        _database.SourceFiles.Add(new SourceFile { Id = Guid.NewGuid(), DownloadId = download.Id, RelativePath = relative, MediaItemId = movie.Id, AssignmentStatus = SourceFileAssignmentStatus.Confirmed, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        var ingest = new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, DownloadId = download.Id, MediaItemId = movie.Id, Stage = IngestStage.Publish, Status = IngestStatus.Done, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        _database.IngestItems.Add(ingest);
        _database.SaveChanges();
        return (movie.Id, ingest.Id, absolute);
    }

    [Fact]
    public async Task Completed_delete_files_purges_media_library_file_ingest_and_download()
    {
        var catalog = SeedCatalog();
        var download = SeedDownload(catalog.Id, DownloadState.Completed);
        var (movieId, _, libraryFile) = SeedPublishedMovie(catalog, download);

        var ok = await Service().DeleteAsync(download.Id, deleteFiles: true, CancellationToken.None);

        Assert.True(ok);
        await using var fresh = Fresh();
        Assert.False(File.Exists(libraryFile));
        Assert.False(await fresh.MediaItems.AnyAsync(m => m.Id == movieId));
        Assert.False(await fresh.MediaSources.AnyAsync());
        Assert.False(await fresh.MediaStreams.AnyAsync());
        Assert.False(await fresh.MetadataRecords.AnyAsync());
        Assert.False(await fresh.IngestItems.AnyAsync());
        Assert.False(await fresh.Downloads.AnyAsync());
        Assert.False(await fresh.SourceFiles.AnyAsync());
        Assert.Contains(_engine.Removed, r => r.InfoHash == download.InfoHash && r.DeleteFiles);
    }

    [Fact]
    public async Task Completed_keep_files_keeps_library_item_and_file_but_removes_download()
    {
        var catalog = SeedCatalog();
        var download = SeedDownload(catalog.Id, DownloadState.Seeding);
        var (movieId, ingestId, libraryFile) = SeedPublishedMovie(catalog, download);

        var ok = await Service().DeleteAsync(download.Id, deleteFiles: false, CancellationToken.None);

        Assert.True(ok);
        await using var fresh = Fresh();
        Assert.True(File.Exists(libraryFile));                                  // library file kept
        Assert.True(await fresh.MediaItems.AnyAsync(m => m.Id == movieId));      // published item kept
        Assert.True(await fresh.IngestItems.AnyAsync(i => i.Id == ingestId));    // Done record kept
        Assert.Null((await fresh.IngestItems.FirstAsync(i => i.Id == ingestId)).DownloadId); // detached
        Assert.False(await fresh.Downloads.AnyAsync());                          // download gone
        Assert.False(await fresh.SourceFiles.AnyAsync());                        // its source files gone
        Assert.Contains(_engine.Removed, r => r.InfoHash == download.InfoHash && !r.DeleteFiles);
    }

    [Fact]
    public async Task Completed_keep_files_removes_an_in_flight_ingest()
    {
        var catalog = SeedCatalog();
        var download = SeedDownload(catalog.Id, DownloadState.Completed);
        _database.SourceFiles.Add(new SourceFile { Id = Guid.NewGuid(), DownloadId = download.Id, RelativePath = "files/movie.mkv", AssignmentStatus = SourceFileAssignmentStatus.NeedsReview, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _database.IngestItems.Add(new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, DownloadId = download.Id, Stage = IngestStage.Identify, Status = IngestStatus.NeedsReview, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _database.SaveChanges();

        var ok = await Service().DeleteAsync(download.Id, deleteFiles: false, CancellationToken.None);

        Assert.True(ok);
        await using var fresh = Fresh();
        Assert.False(await fresh.IngestItems.AnyAsync());  // the dead in-flight item is cleaned up
        Assert.False(await fresh.Downloads.AnyAsync());
    }

    [Fact]
    public async Task Unfinished_download_forces_file_deletion_regardless_of_flag()
    {
        var catalog = SeedCatalog();
        var download = SeedDownload(catalog.Id, DownloadState.Downloading);
        _database.IngestItems.Add(new IngestItem { Id = Guid.NewGuid(), CatalogId = catalog.Id, DownloadId = download.Id, Stage = IngestStage.Download, Status = IngestStatus.Pending, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _database.SaveChanges();

        // Caller asked to keep files, but an unfinished download only has a partial — force-delete.
        var ok = await Service().DeleteAsync(download.Id, deleteFiles: false, CancellationToken.None);

        Assert.True(ok);
        await using var fresh = Fresh();
        Assert.False(await fresh.IngestItems.AnyAsync());
        Assert.False(await fresh.Downloads.AnyAsync());
        Assert.Contains(_engine.Removed, r => r.InfoHash == download.InfoHash && r.DeleteFiles);
    }

    [Fact]
    public async Task Returns_false_for_unknown_download()
    {
        Assert.False(await Service().DeleteAsync(Guid.NewGuid(), deleteFiles: true, CancellationToken.None));
    }

    private MediaServerDbContext Fresh() =>
        new(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class RecordingEngine : ITorrentEngine
    {
        public List<(string InfoHash, bool DeleteFiles)> Removed { get; } = [];

        public event EventHandler<string>? MetadataReceived { add { } remove { } }
        public event EventHandler<string>? DownloadCompleted { add { } remove { } }
        public event EventHandler<string>? DownloadErrored { add { } remove { } }

        public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken)
        {
            Removed.Add((infoHash, deleteFiles));
            return Task.CompletedTask;
        }

        public TorrentDescriptor Inspect(TorrentSource source) => new("hash", null, 0, false, []);
        public Task<TorrentDescriptor> AddAsync(TorrentSource source, string saveDirectory, TorrentLimits limits, bool autoStart, CancellationToken cancellationToken) => Task.FromResult(new TorrentDescriptor("hash", null, 0, false, []));
        public Task PauseAsync(string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResumeAsync(string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;
        public TorrentSnapshot? GetSnapshot(string infoHash) => null;
        public IReadOnlyList<TorrentSnapshot> GetAllSnapshots() => [];
        public IReadOnlyList<TorrentFileInfo> GetFiles(string infoHash) => [];
    }
}
