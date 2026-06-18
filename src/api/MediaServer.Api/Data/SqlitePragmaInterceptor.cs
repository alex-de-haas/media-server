using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MediaServer.Api.Data;

/// <summary>
/// Applies the SQLite pragmas the storage model requires on every opened connection: WAL journaling
/// (so a hot directory copy is recoverable) and a busy timeout (so transient single-writer lock
/// contention retries instead of throwing). See <c>docs/planning/storage-and-data.md</c>.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    private const int BusyTimeoutMilliseconds = 8000;

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        => await ApplyAsync(connection, cancellationToken);

    private static void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}; PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds}; PRAGMA journal_mode = WAL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
