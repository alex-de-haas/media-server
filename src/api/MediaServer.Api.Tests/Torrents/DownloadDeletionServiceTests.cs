using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Torrents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Torrents;

/// <summary>
/// Coverage for removing an in-flight download (before the download→identify hand-off): its staging data,
/// ingest, and source files are purged and the engine torrent is dropped with its files. Published items
/// have no download, so their removal is a library concern, not this service's.
/// </summary>
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

    private DownloadDeletionService Service() => new(_database, _engine, NullLogger<DownloadDeletionService>.Instance);

    private Catalog SeedCatalog()
    {
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        _database.Catalogs.Add(catalog);
        _database.SaveChanges();
        return catalog;
    }

    /// <summary>Seeds an in-flight download with an ingest, a source file under .incoming/, and the file on disk.</summary>
    private (Download Download, Guid IngestId, string StagingFile) SeedInFlight(Guid catalogId, DownloadState state)
    {
        var now = DateTimeOffset.UtcNow;
        var downloadId = Guid.NewGuid();
        var ingestId = Guid.NewGuid();
        var savePath = CatalogPaths.For(_root).IncomingFor(downloadId);
        var stagingRelative = $"{CatalogPaths.IncomingRelative(downloadId)}/movie.mkv";
        var stagingFile = Path.Combine(_root, stagingRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(savePath);
        File.WriteAllBytes(stagingFile, new byte[32]);

        var download = new Download
        {
            Id = downloadId, InfoHash = Guid.NewGuid().ToString("N"), Name = "The Movie 2020",
            CatalogId = catalogId, SourceType = TorrentSourceType.Magnet, State = state,
            SavePath = savePath, AddedAt = now,
        };
        _database.Downloads.Add(download);
        _database.IngestItems.Add(new IngestItem { Id = ingestId, CatalogId = catalogId, DownloadId = downloadId, Stage = IngestStage.Download, Status = IngestStatus.Pending, CreatedAt = now, UpdatedAt = now });
        _database.SourceFiles.Add(new SourceFile { Id = Guid.NewGuid(), IngestItemId = ingestId, DownloadId = downloadId, RelativePath = stagingRelative, AssignmentStatus = SourceFileAssignmentStatus.Unassigned, CreatedAt = now, UpdatedAt = now });
        _database.SaveChanges();
        return (download, ingestId, stagingFile);
    }

    [Fact]
    public async Task Deletes_the_download_its_ingest_source_files_and_staging()
    {
        var catalog = SeedCatalog();
        var (download, _, stagingFile) = SeedInFlight(catalog.Id, DownloadState.Downloading);

        var ok = await Service().DeleteAsync(download.Id, deleteFiles: false, CancellationToken.None);

        Assert.True(ok);
        await using var fresh = Fresh();
        Assert.False(await fresh.Downloads.AnyAsync());
        Assert.False(await fresh.IngestItems.AnyAsync());
        Assert.False(await fresh.SourceFiles.AnyAsync());
        Assert.False(File.Exists(stagingFile));               // .incoming staging removed
        Assert.Contains(_engine.Removed, r => r.InfoHash == download.InfoHash && r.DeleteFiles);
    }

    [Fact]
    public async Task Removes_a_completed_but_unhanded_off_download()
    {
        var catalog = SeedCatalog();
        var (download, _, _) = SeedInFlight(catalog.Id, DownloadState.Completed);

        var ok = await Service().DeleteAsync(download.Id, deleteFiles: true, CancellationToken.None);

        Assert.True(ok);
        await using var fresh = Fresh();
        Assert.False(await fresh.IngestItems.AnyAsync());
        Assert.False(await fresh.Downloads.AnyAsync());
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
