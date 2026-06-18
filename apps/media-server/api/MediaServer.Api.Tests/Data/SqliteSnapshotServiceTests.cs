using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Data;

public sealed class SqliteSnapshotServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ms-snapshot-" + Guid.NewGuid().ToString("N"));

    public SqliteSnapshotServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task CreateSnapshot_produces_a_consistent_readable_copy()
    {
        var databasePath = Path.Combine(_directory, "source.db");
        var snapshotPath = Path.Combine(_directory, "source.db.snapshot");

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE items(id INTEGER PRIMARY KEY, name TEXT); INSERT INTO items(name) VALUES ('alpha'), ('beta');";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var service = new SqliteSnapshotService(Options(databasePath), NullLogger<SqliteSnapshotService>.Instance);
        await service.CreateSnapshotAsync(databasePath, snapshotPath, CancellationToken.None);

        Assert.True(File.Exists(snapshotPath));

        await using var snapshot = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadOnly");
        await snapshot.OpenAsync();
        await using var read = snapshot.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM items;";
        var count = (long)(await read.ExecuteScalarAsync())!;
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CreateSnapshot_is_a_noop_when_the_database_is_missing()
    {
        var databasePath = Path.Combine(_directory, "missing.db");
        var snapshotPath = Path.Combine(_directory, "missing.db.snapshot");
        var service = new SqliteSnapshotService(Options(databasePath), NullLogger<SqliteSnapshotService>.Instance);

        await service.CreateSnapshotAsync(databasePath, snapshotPath, CancellationToken.None);

        Assert.False(File.Exists(snapshotPath));
    }

    [Fact]
    public async Task CreateSnapshot_overwrites_a_previous_snapshot()
    {
        var databasePath = Path.Combine(_directory, "rotate.db");
        var snapshotPath = Path.Combine(_directory, "rotate.db.snapshot");
        await File.WriteAllTextAsync(snapshotPath, "stale");

        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();

        var service = new SqliteSnapshotService(Options(databasePath), NullLogger<SqliteSnapshotService>.Instance);
        await service.CreateSnapshotAsync(databasePath, snapshotPath, CancellationToken.None);

        await using var snapshot = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadOnly");
        await snapshot.OpenAsync(); // A valid SQLite file (not the stale text) opens without error.
    }

    private static HostyOptions Options(string databasePath) => new()
    {
        AppId = "com.haas.media-server",
        CoreOrigin = "http://core.local",
        AppDataDir = Path.GetDirectoryName(databasePath)!,
    };

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }
}
