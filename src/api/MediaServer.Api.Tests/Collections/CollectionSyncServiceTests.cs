using MediaServer.Api.Collections;
using MediaServer.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Collections;

/// <summary>
/// Persistence behaviour of <see cref="CollectionSyncService"/>: a movie's <c>belongs_to_collection</c> becomes
/// a shared <see cref="MovieCollection"/> plus a <see cref="MediaItem.CollectionId"/> link, the collection is
/// deduplicated across movies by its provider identity, non-movies are ignored, and a re-fetch re-points or
/// clears the link.
/// </summary>
public sealed class CollectionSyncServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly Guid _catalogId;

    public CollectionSyncServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        _catalogId = SeedCatalog();
    }

    private CollectionSyncService Service() => new(_database);

    private static string Raw(int id, string name, string? poster = "/p.jpg", string? backdrop = "/b.jpg")
    {
        static string Json(string? value) => value is null ? "null" : $"\"{value}\"";
        return $$"""{ "belongs_to_collection": { "id": {{id}}, "name": "{{name}}", "poster_path": {{Json(poster)}}, "backdrop_path": {{Json(backdrop)}} } }""";
    }

    [Fact]
    public async Task Sync_links_a_movie_to_a_newly_created_collection()
    {
        var movie = SeedItem(MediaKind.Movie);

        var linked = await Service().SyncAsync(movie, "tmdb", Raw(119, "The Lord of the Rings Collection"), CancellationToken.None);

        Assert.True(linked);
        await using var fresh = Fresh();
        var collection = Assert.Single(await fresh.MovieCollections.ToListAsync());
        Assert.Equal("tmdb", collection.Provider);
        Assert.Equal("119", collection.ProviderId);
        Assert.Equal("The Lord of the Rings Collection", collection.Name);
        Assert.Equal("https://image.tmdb.org/t/p/original/p.jpg", collection.PosterUrl);
        Assert.Equal("https://image.tmdb.org/t/p/original/b.jpg", collection.BackdropUrl);

        var item = await fresh.MediaItems.SingleAsync(entity => entity.Id == movie);
        Assert.Equal(collection.Id, item.CollectionId);
    }

    [Fact]
    public async Task Sync_deduplicates_a_collection_shared_by_two_movies()
    {
        var first = SeedItem(MediaKind.Movie, "Fellowship");
        var second = SeedItem(MediaKind.Movie, "Two Towers");
        var raw = Raw(119, "The Lord of the Rings Collection");

        await Service().SyncAsync(first, "tmdb", raw, CancellationToken.None);
        await Service().SyncAsync(second, "tmdb", raw, CancellationToken.None);

        await using var fresh = Fresh();
        var collection = Assert.Single(await fresh.MovieCollections.ToListAsync());
        var movies = await fresh.MediaItems.Where(item => item.CollectionId != null).ToListAsync();
        Assert.Equal(2, movies.Count);
        Assert.All(movies, movie => Assert.Equal(collection.Id, movie.CollectionId));
    }

    [Fact]
    public async Task Sync_ignores_non_movies()
    {
        var series = SeedItem(MediaKind.Series, "A Series");

        var linked = await Service().SyncAsync(series, "tmdb", Raw(1, "Should Not Link"), CancellationToken.None);

        Assert.False(linked);
        await using var fresh = Fresh();
        Assert.Empty(await fresh.MovieCollections.ToListAsync());
        Assert.Null((await fresh.MediaItems.SingleAsync(item => item.Id == series)).CollectionId);
    }

    [Fact]
    public async Task Sync_for_a_movie_in_no_collection_leaves_it_unlinked()
    {
        var movie = SeedItem(MediaKind.Movie);

        var linked = await Service().SyncAsync(movie, "tmdb", "{}", CancellationToken.None);

        Assert.False(linked);
        await using var fresh = Fresh();
        Assert.Null((await fresh.MediaItems.SingleAsync(item => item.Id == movie)).CollectionId);
    }

    [Fact]
    public async Task Sync_is_idempotent_and_repoints_or_clears_on_refetch()
    {
        var movie = SeedItem(MediaKind.Movie);
        await Service().SyncAsync(movie, "tmdb", Raw(119, "LOTR"), CancellationToken.None);
        await Service().SyncAsync(movie, "tmdb", Raw(119, "LOTR"), CancellationToken.None); // re-run: no churn

        Assert.Equal(1, await _database.MovieCollections.CountAsync());

        // A re-fetch into a different franchise re-points the link; the old collection row is left in place.
        await Service().SyncAsync(movie, "tmdb", Raw(120, "The Hobbit Collection"), CancellationToken.None);
        await using (var afterRepoint = Fresh())
        {
            var hobbit = await afterRepoint.MovieCollections.SingleAsync(collection => collection.ProviderId == "120");
            Assert.Equal(hobbit.Id, (await afterRepoint.MediaItems.SingleAsync(item => item.Id == movie)).CollectionId);
            Assert.Equal(2, await afterRepoint.MovieCollections.CountAsync());
        }

        // A re-fetch that drops the collection clears the link.
        var linked = await Service().SyncAsync(movie, "tmdb", "{}", CancellationToken.None);
        Assert.False(linked);
        await using var fresh = Fresh();
        Assert.Null((await fresh.MediaItems.SingleAsync(item => item.Id == movie)).CollectionId);
    }

    private Guid SeedCatalog()
    {
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = Path.Combine(Path.GetTempPath(), "ms-collsync-" + Guid.NewGuid().ToString("N")),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.Catalogs.Add(catalog);
        _database.SaveChanges();
        return catalog.Id;
    }

    private Guid SeedItem(MediaKind kind, string title = "A Movie")
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            CatalogId = _catalogId,
            Kind = kind,
            Title = title,
            AddedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.MediaItems.Add(item);
        _database.SaveChanges();
        return item.Id;
    }

    private MediaServerDbContext Fresh() =>
        new(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
