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
