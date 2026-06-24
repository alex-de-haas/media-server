using MediaServer.Api.Data;
using MediaServer.Api.People;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.People;

/// <summary>
/// The one-off backfill populates people from credits already cached in <see cref="MetadataRecord.Raw"/>,
/// only touches items that have no persisted credits yet, and is safe to re-run.
/// </summary>
public sealed class PersonBackfillServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly Guid _catalogId;

    public PersonBackfillServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = Path.Combine(Path.GetTempPath(), "ms-people-backfill-" + Guid.NewGuid().ToString("N")),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.Catalogs.Add(catalog);
        _database.SaveChanges();
        _catalogId = catalog.Id;
    }

    private PersonBackfillService Service() =>
        new(_database, new PersonSyncService(_database), NullLogger<PersonBackfillService>.Instance);

    private Guid SeedItemWithMetadata(string raw, string language = "en-US")
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = _catalogId,
            Kind = MediaKind.Movie,
            Title = "A Movie",
            AddedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.MediaItems.Add(item);
        _database.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(),
            MediaItemId = item.Id,
            Provider = "tmdb",
            Language = language,
            Raw = raw,
            FetchedAt = DateTimeOffset.UtcNow,
        });
        _database.SaveChanges();
        return item.Id;
    }

    [Fact]
    public async Task Backfill_populates_people_from_cached_credits_then_no_ops_on_rerun()
    {
        var itemId = SeedItemWithMetadata("""
            { "credits": {
                "cast": [ { "id": 6193, "name": "Leonardo DiCaprio", "character": "Cobb", "profile_path": "/leo.jpg" } ],
                "crew": [ { "id": 525, "name": "Christopher Nolan", "job": "Director", "department": "Directing" } ]
            } }
            """);

        var first = await Service().BackfillAsync(CancellationToken.None);

        Assert.Equal(1, first.ItemsProcessed);
        Assert.Equal(2, first.CreditsWritten);
        Assert.Equal(2, await _database.MediaItemPersons.CountAsync(link => link.MediaItemId == itemId));

        // The item now has credits, so a second pass skips it entirely.
        var second = await Service().BackfillAsync(CancellationToken.None);
        Assert.Equal(0, second.ItemsProcessed);
        Assert.Equal(0, second.CreditsWritten);
    }

    [Fact]
    public async Task Backfill_prefers_a_language_record_that_actually_carries_credits()
    {
        // Two cached languages for one item; only the second has a credits object.
        var itemId = SeedItemWithMetadata("""{ "title": "No credits here" }""", language: "ru-RU");
        _database.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(),
            MediaItemId = itemId,
            Provider = "tmdb",
            Language = "en-US",
            Raw = """{ "credits": { "cast": [ { "id": 1, "name": "Actor", "character": "Role" } ], "crew": [] } }""",
            FetchedAt = DateTimeOffset.UtcNow,
        });
        _database.SaveChanges();

        var report = await Service().BackfillAsync(CancellationToken.None);

        Assert.Equal(1, report.ItemsProcessed);
        Assert.Equal(1, report.CreditsWritten);
        Assert.Equal("Role", (await _database.MediaItemPersons.SingleAsync(link => link.MediaItemId == itemId)).Character);
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
