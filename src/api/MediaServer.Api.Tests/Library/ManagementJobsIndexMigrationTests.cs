using MediaServer.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MediaServer.Api.Tests.Library;

public sealed class ManagementJobsIndexMigrationTests
{
    private const string PreviousMigration = "20261012145801_AddTranscodeVersionName";
    private const string IndexName = "IX_Jobs_Type_Status_RelatedId";

    [Fact]
    public async Task Upgrade_and_rollback_preserve_existing_job_history()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>()
            .UseSqlite(connection).Options);
        var migrator = database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        var jobId = Guid.NewGuid();
        var relatedId = Guid.NewGuid();
        var storedNow = (string)new UtcDateTimeOffsetConverter()
            .ConvertToProvider(DateTimeOffset.Parse("2026-10-13T00:00:00Z"))!;
        // Seed the previous schema directly so later Job properties cannot change this upgrade fixture.
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "Jobs" ("Id", "Type", "RelatedType", "RelatedId", "Status", "Progress", "AttemptCount", "UpdatedAt")
            VALUES ({jobId}, 'library:move', 'media-item', {relatedId}, {(int)JobStatus.Completed}, 100, 2, {storedNow});
            """);
        Assert.False(await IndexExists());

        await database.Database.MigrateAsync();

        Assert.True(await IndexExists());
        Assert.False(database.Database.HasPendingModelChanges());
        var upgraded = await database.Jobs.AsNoTracking().SingleAsync();
        Assert.Equal(jobId, upgraded.Id);
        Assert.Equal(relatedId, upgraded.RelatedId);
        Assert.Equal("library:move", upgraded.Type);
        Assert.Equal(JobStatus.Completed, upgraded.Status);
        Assert.Equal(100, upgraded.Progress);
        Assert.Equal(2, upgraded.AttemptCount);

        await migrator.MigrateAsync(PreviousMigration);

        Assert.False(await IndexExists());
        var rolledBack = await database.Jobs.AsNoTracking().SingleAsync();
        Assert.Equivalent(upgraded, rolledBack);

        async Task<bool> IndexExists()
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name";
            command.Parameters.AddWithValue("$name", IndexName);
            return (long)(await command.ExecuteScalarAsync())! == 1;
        }
    }
}
