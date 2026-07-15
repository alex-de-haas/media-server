using MediaServer.Api.Data;
using MediaServer.Api.Torrents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Torrents;

public sealed class DownloadFileServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly Guid _downloadId = Guid.NewGuid();

    public DownloadFileServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        var catalogId = Guid.NewGuid();
        _database.Catalogs.Add(new Catalog { Id = catalogId, Name = "Movies", Type = CatalogType.Movie, Root = "/tmp/x", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _database.Downloads.Add(new Download { Id = _downloadId, InfoHash = "h", Name = "n", CatalogId = catalogId, SourceType = TorrentSourceType.Magnet, State = DownloadState.Completed, SavePath = "/tmp/x/.incoming", AddedAt = DateTimeOffset.UtcNow });
        // Source files are owned by the ingest; the upsert resolves it from the download id.
        _database.IngestItems.Add(new IngestItem { Id = Guid.NewGuid(), CatalogId = catalogId, DownloadId = _downloadId, Stage = IngestStage.Download, Status = IngestStatus.Pending, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        _database.SaveChanges();
    }

    private DownloadFileService Service() => new(_database);

    [Fact]
    public async Task Upsert_deduplicates_a_repeated_path_in_one_call()
    {
        // The same playable path twice must never become two rows.
        var files = new[]
        {
            new TorrentFileInfo(0, "Movie/Movie.mkv", 2_000_000_000),
            new TorrentFileInfo(1, "Movie/Movie.mkv", 2_000_000_000),
        };

        var result = await Service().UpsertSourceFilesAsync(_downloadId, files, CancellationToken.None);

        Assert.Single(result);
        await using var verify = Fresh();
        Assert.Equal(1, await verify.SourceFiles.CountAsync(file => file.DownloadId == _downloadId));
    }

    [Fact]
    public async Task Upsert_admits_external_audio_tracks_but_not_junk()
    {
        // External dubs ride along with the video for identify + mux; .nfo/.srt and the like never do.
        var files = new[]
        {
            new TorrentFileInfo(0, "Show/Show S01E01.mkv", 2_000_000_000),
            new TorrentFileInfo(1, "Show/Rus Sound/Show S01E01.mka", 100_000_000),
            new TorrentFileInfo(2, "Show/Show S01E01.srt", 50_000),
            new TorrentFileInfo(3, "Show/info.nfo", 1_000),
        };

        var result = await Service().UpsertSourceFilesAsync(_downloadId, files, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, file => file.RelativePath.EndsWith("Show S01E01.mka", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upsert_is_idempotent_across_calls_and_updates_in_place()
    {
        await Service().UpsertSourceFilesAsync(_downloadId, [new TorrentFileInfo(0, "Movie/Movie.mkv", 1_000)], CancellationToken.None);
        await Service().UpsertSourceFilesAsync(_downloadId, [new TorrentFileInfo(0, "Movie/Movie.mkv", 2_000_000_000)], CancellationToken.None);

        await using var verify = Fresh();
        var file = Assert.Single(await verify.SourceFiles.Where(f => f.DownloadId == _downloadId).ToListAsync());
        Assert.Equal(2_000_000_000, file.SizeBytes);
    }

    private MediaServerDbContext Fresh() =>
        new(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
