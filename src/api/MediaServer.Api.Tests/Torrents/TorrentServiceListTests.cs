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
/// Regression coverage that download listing returns newest-first. Ordering runs in SQL via the
/// global <c>DateTimeOffset</c> value converter, which stores a sortable UTC string so SQLite can
/// <c>ORDER BY</c> the column — historically this threw at runtime and had to be sorted client-side.
/// </summary>
public sealed class TorrentServiceListTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "ms-torrent-" + Guid.NewGuid().ToString("N"));

    public TorrentServiceListTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        Directory.CreateDirectory(_tempRoot);
    }

    private TorrentService CreateService() => new(
        _database,
        new FakeTorrentEngine(),
        new FilesystemInspector(),
        new MediaServerSettings(),
        new HostyOptions { AppId = "test", CoreOrigin = "http://localhost", AppDataDir = _tempRoot },
        new PipelineQueue(),
        new DownloadDeletionService(_database, new FakeTorrentEngine(), NullLogger<DownloadDeletionService>.Instance),
        NullLogger<TorrentService>.Instance);

    [Fact]
    public async Task ListAsync_orders_by_added_at_newest_first()
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = _tempRoot,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _database.Catalogs.Add(catalog);
        _database.Downloads.AddRange(
            NewDownload(catalog.Id, "older", now.AddMinutes(-10)),
            NewDownload(catalog.Id, "newer", now));
        await _database.SaveChangesAsync();

        // Exercises the SQL ORDER BY that the DateTimeOffset value converter makes translatable.
        var downloads = await CreateService().ListAsync(CancellationToken.None);

        Assert.Equal(["newer", "older"], downloads.Select(download => download.Name));
    }

    [Fact]
    public async Task AddAsync_reclaims_a_stale_download_for_the_same_info_hash()
    {
        var now = DateTimeOffset.UtcNow;
        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = _tempRoot, CreatedAt = now, UpdatedAt = now };
        // An orphaned (no ingest), completed download for the info hash FakeTorrentEngine.Inspect returns.
        var stale = new Download
        {
            Id = Guid.NewGuid(), InfoHash = "hash", Name = "old", CatalogId = catalog.Id, SourceType = TorrentSourceType.Magnet,
            State = DownloadState.Completed, SavePath = Path.Combine(_tempRoot, ".incoming", "old"), AddedAt = now.AddMinutes(-5),
        };
        _database.AddRange(catalog, stale);
        await _database.SaveChangesAsync();

        // Re-adding the same torrent must not error — it reclaims the stale row and adds a fresh download.
        await CreateService().AddAsync(new AddTorrentRequest(catalog.Id, "magnet:?xt=urn:btih:hash", null, null), CancellationToken.None);

        Assert.False(await _database.Downloads.AnyAsync(download => download.Id == stale.Id)); // stale reclaimed
        var fresh = await _database.Downloads.SingleAsync(download => download.InfoHash == "hash");
        Assert.NotEqual(stale.Id, fresh.Id); // a brand-new download row
    }

    private Download NewDownload(Guid catalogId, string name, DateTimeOffset addedAt) => new()
    {
        Id = Guid.NewGuid(),
        InfoHash = Guid.NewGuid().ToString("N"),
        Name = name,
        CatalogId = catalogId,
        SourceType = TorrentSourceType.Magnet,
        State = DownloadState.Downloading,
        KeepSeeding = false,
        SavePath = _tempRoot,
        AddedAt = addedAt,
    };

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
