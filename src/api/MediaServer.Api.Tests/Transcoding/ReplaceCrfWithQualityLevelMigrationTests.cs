using MediaServer.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MediaServer.Api.Tests.Transcoding;

/// <summary>
/// Covers the CRF → quality level conversion. A finished job's settings are the only record of what produced
/// the file sitting beside it, so the migration has to carry them rather than drop the column — and the
/// buckets have to read an H.264 job's CRF on the H.264 scale, which is two points below x265's for the same
/// picture.
/// </summary>
public sealed class ReplaceCrfWithQualityLevelMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260805073242_AddGlobalPreferenceUniqueIndex";
    private const string ThisMigration = "20260806180455_ReplaceCrfWithQualityLevel";

    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly Guid _catalogId = Guid.NewGuid();
    private readonly Guid _itemId = Guid.NewGuid();
    private readonly Guid _sourceId = Guid.NewGuid();

    public ReplaceCrfWithQualityLevelMigrationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        // Stop one migration short, so the rows below exist with a Crf column for the conversion to read.
        _database.Database.GetService<IMigrator>().Migrate(PreviousMigration);

        // A job hangs off a catalog, a media item and a source, and the table rebuild SQLite performs would
        // trip over missing parents. Seeded as raw INSERTs against the schema *of that migration* rather than
        // through the context: the entities describe today's model, so any column added to one of these tables
        // afterwards — none of which this migration is about — would otherwise break this fixture.
        var now = DateTimeOffset.UtcNow.ToString("O");
        _database.Database.ExecuteSqlRaw(
            """
            INSERT INTO "Catalogs" ("Id", "Name", "Type", "Root", "NamingTemplate", "DefaultKeepSeeding", "CreatedAt", "UpdatedAt")
            VALUES ({0}, 'Movies', 0, '/movies', '{{Title}} ({{Year}})', 0, {1}, {1});

            INSERT INTO "MediaItems" ("Id", "CatalogId", "Kind", "Title", "Providers", "AddedAt", "UpdatedAt")
            VALUES ({2}, {0}, 0, 'Inception', '{{}}', {1}, {1});

            INSERT INTO "MediaSources" ("Id", "MediaItemId", "Container", "Path", "SizeBytes", "DurationTicks", "CreatedAt")
            VALUES ({3}, {2}, 'mkv', 'in.mkv', 0, 0, {1});
            """,
            _catalogId.ToString(), now, _itemId.ToString(), _sourceId.ToString());
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }

    private void SeedJob(string name, string videoCodec, int? crf)
    {
        using var command = _connection.CreateCommand();
        // The parent ids come back out of the tables rather than being formatted here: how EF writes a Guid
        // into a TEXT column is its business, and a mismatched spelling would only surface as a foreign-key
        // failure with nothing pointing at the cause.
        command.CommandText = """
            INSERT INTO "TranscodeJobs"
                ("Id", "EngineJobId", "MediaSourceId", "MediaItemId", "CatalogId", "Name", "InputPath",
                 "OutputPath", "VideoCodec", "HardwareAcceleration", "Crf", "State", "PercentComplete",
                 "CreatedAt")
            SELECT $id, $engineJobId,
                   (SELECT "Id" FROM "MediaSources" LIMIT 1),
                   (SELECT "Id" FROM "MediaItems" LIMIT 1),
                   (SELECT "Id" FROM "Catalogs" LIMIT 1),
                   $name, 'in.mkv', 'out.mkv', $videoCodec, 'auto', $crf, 3, 100,
                   '2026-08-01T00:00:00+00:00';
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$engineJobId", Guid.NewGuid().ToString("n"));
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$videoCodec", videoCodec);
        command.Parameters.AddWithValue("$crf", crf.HasValue ? crf.Value : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private Dictionary<string, string?> LevelsByName()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """SELECT "Name", "QualityLevel" FROM "TranscodeJobs";""";
        using var reader = command.ExecuteReader();
        var levels = new Dictionary<string, string?>();
        while (reader.Read())
        {
            levels[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        return levels;
    }

    [Fact]
    public void Up_MapsEachCrfOntoTheLevelItMeans()
    {
        SeedJob("hevc-18", "hevc", 18);
        SeedJob("hevc-20", "hevc", 20);
        SeedJob("hevc-22", "hevc", 22);
        SeedJob("hevc-24", "hevc", 24);
        // Off the defined points: 21 sits inside "high", 30 is well past the last bucket's edge.
        SeedJob("hevc-21", "hevc", 21);
        SeedJob("hevc-30", "hevc", 30);

        _database.Database.GetService<IMigrator>().Migrate(ThisMigration);

        var levels = LevelsByName();
        Assert.Equal("highest", levels["hevc-18"]);
        Assert.Equal("high", levels["hevc-20"]);
        Assert.Equal("balanced", levels["hevc-22"]);
        Assert.Equal("small", levels["hevc-24"]);
        Assert.Equal("high", levels["hevc-21"]);
        Assert.Equal("small", levels["hevc-30"]);
    }

    [Fact]
    public void Up_ReadsAnH264JobOnItsOwnScale()
    {
        // x264 needs a CRF two points below x265 for the same picture. Sharing one set of buckets would
        // credit every H.264 job with a level better than it actually ran at.
        SeedJob("h264-18", "h264", 18);
        SeedJob("hevc-18", "hevc", 18);

        _database.Database.GetService<IMigrator>().Migrate(ThisMigration);

        var levels = LevelsByName();
        Assert.Equal("high", levels["h264-18"]);
        Assert.Equal("highest", levels["hevc-18"]);
    }

    [Fact]
    public void Up_LeavesAJobWithNoCrfAlone()
    {
        // A copy, or an encode from before levels existed that took the encoder's own default. Naming a
        // level for either would invent a choice nobody made.
        SeedJob("copied", "copy", null);
        SeedJob("hardware", "hevc", null);

        _database.Database.GetService<IMigrator>().Migrate(ThisMigration);

        var levels = LevelsByName();
        Assert.Null(levels["copied"]);
        Assert.Null(levels["hardware"]);
    }

    [Fact]
    public void Down_RestoresTheCrfEachLevelIsDefinedAs()
    {
        SeedJob("hevc-22", "hevc", 22);
        SeedJob("h264-16", "h264", 16);
        SeedJob("copied", "copy", null);

        var migrator = _database.Database.GetService<IMigrator>();
        migrator.Migrate(ThisMigration);
        migrator.Migrate(PreviousMigration);

        using var command = _connection.CreateCommand();
        command.CommandText = """SELECT "Name", "Crf" FROM "TranscodeJobs";""";
        using var reader = command.ExecuteReader();
        var crfs = new Dictionary<string, int?>();
        while (reader.Read())
        {
            crfs[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        }

        // Lossy in general — a level is a bucket — but a job that sat on a level's defining CRF round-trips.
        Assert.Equal(22, crfs["hevc-22"]);
        Assert.Equal(16, crfs["h264-16"]);
        Assert.Null(crfs["copied"]);
    }
}
