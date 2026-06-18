using MediaServer.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Data;

/// <summary>
/// Guardrail for <see cref="UtcDateTimeOffsetConverter"/>: it stores every <c>DateTimeOffset</c> as a
/// fixed-width UTC string so SQLite can <c>ORDER BY</c> and compare timestamps in SQL. Without the
/// converter the EF Core SQLite provider throws on those translations, which is what previously forced
/// the client-side workarounds across the services.
/// </summary>
public sealed class DateTimeOffsetConverterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;

    public DateTimeOffsetConverterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
    }

    [Fact]
    public async Task Orders_by_non_null_datetimeoffset_in_sql()
    {
        var catalog = await SeedCatalogAsync();
        var origin = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _database.IngestItems.AddRange(
            NewIngest(catalog.Id, "b", createdAt: origin.AddMinutes(2)),
            NewIngest(catalog.Id, "a", createdAt: origin),
            NewIngest(catalog.Id, "c", createdAt: origin.AddMinutes(1)));
        await _database.SaveChangesAsync();

        // Translates to ORDER BY in SQL — would throw before the converter.
        var ordered = await _database.IngestItems
            .AsNoTracking()
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => item.LastError)
            .ToListAsync();

        Assert.Equal(["b", "c", "a"], ordered);
    }

    [Fact]
    public async Task Compares_nullable_datetimeoffset_in_sql()
    {
        var catalog = await SeedCatalogAsync();
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        _database.IngestItems.AddRange(
            NewIngest(catalog.Id, "null-due", nextAttemptAt: null),
            NewIngest(catalog.Id, "past-due", nextAttemptAt: now.AddMinutes(-5)),
            NewIngest(catalog.Id, "future", nextAttemptAt: now.AddMinutes(5)));
        await _database.SaveChangesAsync();

        // The reconciler's exact shape: a nullable DateTimeOffset compared with <= in SQL.
        var due = await _database.IngestItems
            .AsNoTracking()
            .Where(item => item.NextAttemptAt == null || item.NextAttemptAt <= now)
            .Select(item => item.LastError)
            .ToListAsync();

        Assert.Equal(["null-due", "past-due"], due.OrderBy(value => value).ToList());
    }

    [Fact]
    public async Task Normalizes_to_fixed_width_utc_z_string_on_disk()
    {
        // A non-UTC offset must be normalized to UTC before storage so lexical order stays chronological.
        var catalog = await SeedCatalogAsync(createdAt: new DateTimeOffset(2026, 6, 18, 16, 34, 42, TimeSpan.FromHours(5)));
        await _database.SaveChangesAsync();

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT CreatedAt FROM Catalogs LIMIT 1";
        var stored = (string)(await command.ExecuteScalarAsync())!;

        Assert.Equal("2026-06-18T11:34:42.0000000Z", stored);

        // And it round-trips back to the same instant.
        var reloaded = await _database.Catalogs.AsNoTracking().SingleAsync();
        Assert.Equal(catalog.CreatedAt.ToUniversalTime(), reloaded.CreatedAt);
        Assert.Equal(TimeSpan.Zero, reloaded.CreatedAt.Offset);
    }

    private async Task<Catalog> SeedCatalogAsync(DateTimeOffset? createdAt = null)
    {
        var stamp = createdAt ?? DateTimeOffset.UtcNow;
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = Path.Combine(Path.GetTempPath(), "ms-dto-" + Guid.NewGuid().ToString("N")),
            CreatedAt = stamp,
            UpdatedAt = stamp,
        };
        _database.Catalogs.Add(catalog);
        await _database.SaveChangesAsync();
        return catalog;
    }

    private static IngestItem NewIngest(
        Guid catalogId, string label, DateTimeOffset? createdAt = null, DateTimeOffset? nextAttemptAt = null)
    {
        var stamp = createdAt ?? DateTimeOffset.UtcNow;
        return new IngestItem
        {
            Id = Guid.NewGuid(),
            CatalogId = catalogId,
            Stage = IngestStage.Intake,
            Status = IngestStatus.Pending,
            // Reuse LastError as a cheap label to assert ordering/filtering without extra columns.
            LastError = label,
            NextAttemptAt = nextAttemptAt,
            CreatedAt = stamp,
            UpdatedAt = stamp,
        };
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
