using MediaServer.Api.Hosty;
using Microsoft.Data.Sqlite;

namespace MediaServer.Api.Data;

/// <summary>
/// Produces a consistent, point-in-time copy of the live SQLite database using the online-backup API,
/// written atomically to a fixed snapshot file beside the database. Because Hosty Core has no
/// pre-backup quiesce hook, a scheduled host backup of <c>HOSTY_APP_DATA_DIR</c> could otherwise
/// capture the live DB mid-write; this snapshot (plus WAL journaling) gives the host a recoverable copy.
/// See <c>docs/planning/storage-and-data.md</c> and the M4 plan.
/// </summary>
public sealed class SqliteSnapshotService(HostyOptions options, ILogger<SqliteSnapshotService> logger)
{
    /// <summary>Fixed path so the snapshot stays bounded (one file, atomically replaced each run).</summary>
    public string SnapshotPath => options.DatabasePath + ".snapshot";

    public Task CreateSnapshotAsync(CancellationToken cancellationToken) =>
        CreateSnapshotAsync(options.DatabasePath, SnapshotPath, cancellationToken);

    /// <summary>
    /// Backs up <paramref name="databasePath"/> to <paramref name="snapshotPath"/>. Exposed with explicit
    /// paths so it is unit-testable against a temp database. Writes to a temp file first, then atomically
    /// replaces the destination, so a host backup never sees a half-written snapshot.
    /// </summary>
    public async Task CreateSnapshotAsync(string databasePath, string snapshotPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath))
        {
            logger.LogDebug("No database at {DatabasePath} yet; skipping snapshot.", databasePath);
            return;
        }

        var temporaryPath = snapshotPath + ".tmp";
        Delete(temporaryPath);

        await using var source = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        await source.OpenAsync(cancellationToken);

        // Fold committed WAL frames back into the main DB file and keep the WAL from growing unbounded.
        await using (var checkpoint = source.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = temporaryPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // No pooling on the throwaway temp file, so its handle is fully released on dispose and the
            // move below cannot race a pooled handle on Windows — without disturbing the main DB's pool.
            Pooling = false,
        }.ToString()))
        {
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination); // Online backup: consistent even under concurrent writers.
        }

        File.Move(temporaryPath, snapshotPath, overwrite: true);
        logger.LogDebug("Wrote database snapshot to {SnapshotPath}.", snapshotPath);
    }

    private static void Delete(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>Runs <see cref="SqliteSnapshotService"/> on a timer so scheduled host backups capture a consistent copy.</summary>
public sealed class DatabaseSnapshotWorker(SqliteSnapshotService snapshots, ILogger<DatabaseSnapshotWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            await RunOnceAsync(stoppingToken);

            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await snapshots.CreateSnapshotAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Scheduled database snapshot failed.");
        }
    }
}
