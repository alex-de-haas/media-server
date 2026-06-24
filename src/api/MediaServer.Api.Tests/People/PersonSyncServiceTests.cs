using MediaServer.Api.Data;
using MediaServer.Api.People;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.People;

/// <summary>
/// Persistence behaviour of <see cref="PersonSyncService"/>: credits become shared <see cref="Person"/>
/// rows plus per-item <see cref="MediaItemPerson"/> credits, a person is deduplicated across items by its
/// provider identity, null character/profile survive the round-trip, and a re-sync replaces the item's
/// credits in place.
/// </summary>
public sealed class PersonSyncServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly Guid _catalogId;

    public PersonSyncServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        _catalogId = SeedCatalog();
    }

    private PersonSyncService Service() => new(_database);

    private Guid SeedCatalog()
    {
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = Path.Combine(Path.GetTempPath(), "ms-people-" + Guid.NewGuid().ToString("N")),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.Catalogs.Add(catalog);
        _database.SaveChanges();
        return catalog.Id;
    }

    private Guid SeedItem(string title = "A Movie")
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = _catalogId,
            Kind = MediaKind.Movie,
            Title = title,
            AddedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.MediaItems.Add(item);
        _database.SaveChanges();
        return item.Id;
    }

    private static string Credits(string castAndCrew) => $$"""{ "credits": { {{castAndCrew}} } }""";

    [Fact]
    public async Task Sync_creates_persons_and_credits_from_cast_and_crew()
    {
        var itemId = SeedItem();
        var raw = Credits("""
            "cast": [ { "id": 6193, "name": "Leonardo DiCaprio", "character": "Cobb", "profile_path": "/leo.jpg", "order": 0, "known_for_department": "Acting" } ],
            "crew": [ { "id": 525, "name": "Christopher Nolan", "job": "Director", "department": "Directing" } ]
            """);

        var written = await Service().SyncAsync(itemId, "tmdb", raw, CancellationToken.None);

        Assert.Equal(2, written);
        await using var fresh = Fresh();
        var people = await fresh.Persons.OrderBy(person => person.ProviderId).ToListAsync();
        Assert.Equal(2, people.Count);

        var leo = people.Single(person => person.ProviderId == "6193");
        Assert.Equal("tmdb", leo.Provider);
        Assert.Equal("Leonardo DiCaprio", leo.Name);
        Assert.Equal("/leo.jpg", leo.ProfilePath);
        Assert.Equal("https://image.tmdb.org/t/p/original/leo.jpg", leo.ProfileUrl);
        Assert.Equal("Acting", leo.KnownForDepartment);

        var credits = await fresh.MediaItemPersons.Where(link => link.MediaItemId == itemId).ToListAsync();
        Assert.Equal(2, credits.Count);
        Assert.Contains(credits, credit => credit.Role == PersonRole.Cast && credit.Character == "Cobb" && credit.PersonId == leo.Id);
        Assert.Contains(credits, credit => credit.Role == PersonRole.Crew && credit.Job == "Director" && credit.Department == "Directing");
    }

    [Fact]
    public async Task Sync_deduplicates_a_person_shared_by_two_items()
    {
        var first = SeedItem("First");
        var second = SeedItem("Second");
        var raw = Credits("""
            "cast": [ { "id": 6193, "name": "Leonardo DiCaprio", "character": "Cobb", "profile_path": "/leo.jpg" } ],
            "crew": []
            """);

        await Service().SyncAsync(first, "tmdb", raw, CancellationToken.None);
        await Service().SyncAsync(second, "tmdb", raw, CancellationToken.None);

        await using var fresh = Fresh();
        var person = Assert.Single(await fresh.Persons.ToListAsync());
        Assert.Equal("6193", person.ProviderId);

        var credits = await fresh.MediaItemPersons.ToListAsync();
        Assert.Equal(2, credits.Count);
        Assert.All(credits, credit => Assert.Equal(person.Id, credit.PersonId));
        Assert.Equal(new[] { first, second }.OrderBy(id => id), credits.Select(credit => credit.MediaItemId).OrderBy(id => id));
    }

    [Fact]
    public async Task Sync_persists_null_profile_and_character()
    {
        var itemId = SeedItem();
        var raw = Credits("""
            "cast": [ { "id": 24045, "name": "Joseph Gordon-Levitt", "character": "", "profile_path": null } ],
            "crew": []
            """);

        await Service().SyncAsync(itemId, "tmdb", raw, CancellationToken.None);

        await using var fresh = Fresh();
        var person = Assert.Single(await fresh.Persons.ToListAsync());
        Assert.Null(person.ProfilePath);
        Assert.Null(person.ProfileUrl);

        var credit = Assert.Single(await fresh.MediaItemPersons.ToListAsync());
        Assert.Null(credit.Character);
    }

    [Fact]
    public async Task Sync_is_idempotent_and_replaces_existing_credits_on_refetch()
    {
        var itemId = SeedItem();
        var initial = Credits("""
            "cast": [ { "id": 1, "name": "First Actor", "character": "Old Role" } ],
            "crew": []
            """);
        await Service().SyncAsync(itemId, "tmdb", initial, CancellationToken.None);

        // Re-running the same payload must not duplicate join rows.
        await Service().SyncAsync(itemId, "tmdb", initial, CancellationToken.None);
        Assert.Equal(1, await _database.MediaItemPersons.CountAsync(link => link.MediaItemId == itemId));

        // A re-fetch with changed credits replaces the item's join rows but keeps shared persons around.
        var changed = Credits("""
            "cast": [ { "id": 2, "name": "Second Actor", "character": "New Role" } ],
            "crew": []
            """);
        await Service().SyncAsync(itemId, "tmdb", changed, CancellationToken.None);

        await using var fresh = Fresh();
        var credit = Assert.Single(await fresh.MediaItemPersons.Where(link => link.MediaItemId == itemId).ToListAsync());
        Assert.Equal("New Role", credit.Character);
        // The first actor's Person row is not deleted by the re-sync (people are shared, only joins are replaced).
        Assert.Equal(2, await fresh.Persons.CountAsync());
    }

    [Fact]
    public async Task Sync_with_no_credits_clears_the_items_existing_credits()
    {
        var itemId = SeedItem();
        await Service().SyncAsync(itemId, "tmdb", Credits("""
            "cast": [ { "id": 1, "name": "Actor", "character": "Role" } ],
            "crew": []
            """), CancellationToken.None);

        var written = await Service().SyncAsync(itemId, "tmdb", "{}", CancellationToken.None);

        Assert.Equal(0, written);
        await using var fresh = Fresh();
        Assert.Empty(await fresh.MediaItemPersons.Where(link => link.MediaItemId == itemId).ToListAsync());
    }

    private MediaServerDbContext Fresh() =>
        new(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
