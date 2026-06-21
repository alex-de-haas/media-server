using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.IO;
using MediaServer.Api.Pipeline;
using MediaServer.Api.Tests.Pipeline;
using MediaServer.Api.Torrents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Torrents;

/// <summary>
/// Regression: the download row must be committed before the engine is started, so a re-added
/// already-complete torrent (which fires completion almost immediately) finds its row instead of
/// racing it and stranding the ingest item in the download stage.
/// </summary>
public sealed class TorrentAddOrderingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ms-add-" + Guid.NewGuid().ToString("N"));

    public TorrentAddOrderingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task AddAsync_commits_the_download_before_starting_the_engine()
    {
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _root, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        _database.Catalogs.Add(catalog);
        await _database.SaveChangesAsync();

        var engine = new RowProbeEngine(() =>
        {
            using var probe = Fresh();
            return probe.Downloads.Any(download => download.InfoHash == "feedface");
        });
        var service = new TorrentService(
            _database, engine, new FilesystemInspector(), new MediaServerSettings(),
            new HostyOptions { AppId = "test", CoreOrigin = "http://localhost", AppDataDir = _root },
            new PipelineQueue(),
            new DownloadDeletionService(_database, engine, NullLogger<DownloadDeletionService>.Instance),
            NullLogger<TorrentService>.Instance);

        await service.AddAsync(new AddTorrentRequest(catalog.Id, "magnet:?xt=urn:btih:feedface", null, null), CancellationToken.None);

        Assert.True(engine.RowExistedWhenStarted);   // the fix: row committed before engine.AddAsync
        await using var verify = Fresh();
        Assert.True(await verify.Downloads.AnyAsync(d => d.InfoHash == "feedface"));
        Assert.True(await verify.IngestItems.AnyAsync(i => i.Stage == IngestStage.Intake));
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

    private sealed class RowProbeEngine(Func<bool> downloadExists) : ITorrentEngine
    {
        public bool RowExistedWhenStarted { get; private set; }

        public event EventHandler<string>? MetadataReceived { add { } remove { } }
        public event EventHandler<string>? DownloadCompleted { add { } remove { } }
        public event EventHandler<string>? DownloadErrored { add { } remove { } }

        public TorrentDescriptor Inspect(TorrentSource source) =>
            new("feedface", "The Movie", 100, true, [new TorrentFileInfo(0, "The Movie/The Movie.mkv", 100)]);

        public Task<TorrentDescriptor> AddAsync(TorrentSource source, string saveDirectory, TorrentLimits limits, bool autoStart, CancellationToken cancellationToken)
        {
            RowExistedWhenStarted = downloadExists();
            return Task.FromResult(Inspect(source));
        }

        public Task PauseAsync(string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResumeAsync(string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(string infoHash, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken cancellationToken) => Task.CompletedTask;
        public TorrentSnapshot? GetSnapshot(string infoHash) => null;
        public IReadOnlyList<TorrentSnapshot> GetAllSnapshots() => [];
        public IReadOnlyList<TorrentFileInfo> GetFiles(string infoHash) => [];
    }
}
