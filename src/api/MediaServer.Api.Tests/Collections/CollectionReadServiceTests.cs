using MediaServer.Api.Collections;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Collections;

/// <summary>
/// Read behaviour of <see cref="CollectionReadService"/>: the grid surfaces only franchises with at least two
/// owned (published) movies, ordered by name, falling back to a member poster when the collection has none; the
/// detail lists a collection's in-library movies chronologically and 404s when it has none.
/// </summary>
public sealed class CollectionReadServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly Guid _catalogId;

    public CollectionReadServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        _catalogId = SeedCatalog();
    }

    private CollectionReadService Service()
    {
        var settings = new MediaServerSettings { SupportedLanguages = ["en-US"] };
        var library = new LibraryReadService(_database, new UserDataService(_database, TimeProvider.System), settings);
        return new CollectionReadService(_database, library);
    }

    [Fact]
    public async Task List_surfaces_only_collections_with_at_least_two_owned_movies()
    {
        var surfaced = SeedCollection("1", "Surfaced", posterUrl: "https://cdn/own.jpg");
        SeedMovie("A", 2001, surfaced);
        SeedMovie("B", 2002, surfaced);

        var tooFew = SeedCollection("2", "Single");
        SeedMovie("Only", 2010, tooFew);

        var oneUnpublished = SeedCollection("3", "Mostly Unpublished");
        SeedMovie("Published", 2011, oneUnpublished);
        SeedMovie("Draft", 2012, oneUnpublished, published: false);

        var result = await Service().ListAsync(CancellationToken.None);

        var only = Assert.Single(result);
        Assert.Equal(surfaced, only.Id);
        Assert.Equal("Surfaced", only.Name);
        Assert.Equal(2, only.ItemCount);
        Assert.Equal("https://cdn/own.jpg", only.PosterUrl);
    }

    [Fact]
    public async Task List_orders_by_name_and_falls_back_to_a_member_poster()
    {
        var zeta = SeedCollection("9", "Zeta", posterUrl: "https://cdn/zeta.jpg");
        SeedMovie("Z1", 2001, zeta);
        SeedMovie("Z2", 2002, zeta);

        var alpha = SeedCollection("1", "Alpha", posterUrl: null); // no own artwork → member fallback
        SeedMovie("Earliest", 2000, alpha, posterUrl: "https://cdn/early.jpg");
        SeedMovie("Later", 2005, alpha);

        var result = await Service().ListAsync(CancellationToken.None);

        Assert.Collection(
            result,
            first =>
            {
                Assert.Equal("Alpha", first.Name);
                Assert.Equal("https://cdn/early.jpg", first.PosterUrl); // earliest member's poster
            },
            second =>
            {
                Assert.Equal("Zeta", second.Name);
                Assert.Equal("https://cdn/zeta.jpg", second.PosterUrl); // its own poster
            });
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_collection()
    {
        Assert.Null(await Service().GetAsync(Guid.NewGuid(), appUserId: null, CancellationToken.None));
    }

    [Fact]
    public async Task Get_lists_members_chronologically_with_the_collection_poster()
    {
        var collection = SeedCollection("119", "LOTR", posterUrl: "https://cdn/lotr.jpg");
        SeedMovie("Return of the King", 2003, collection);
        SeedMovie("Fellowship", 2001, collection);
        SeedMovie("Two Towers", 2002, collection);

        var detail = await Service().GetAsync(collection, appUserId: null, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("LOTR", detail!.Name);
        Assert.Equal("https://cdn/lotr.jpg", detail.PosterUrl);
        Assert.Collection(
            detail.Items,
            first => Assert.Equal("Fellowship", first.Title),
            second => Assert.Equal("Two Towers", second.Title),
            third => Assert.Equal("Return of the King", third.Title));
    }

    [Fact]
    public async Task Get_returns_null_when_no_member_is_published()
    {
        var collection = SeedCollection("404", "Hidden");
        SeedMovie("Draft", 2030, collection, published: false);

        Assert.Null(await Service().GetAsync(collection, appUserId: null, CancellationToken.None));
    }

    private Guid SeedCatalog()
    {
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = Path.Combine(Path.GetTempPath(), "ms-collread-" + Guid.NewGuid().ToString("N")),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.Catalogs.Add(catalog);
        _database.SaveChanges();
        return catalog.Id;
    }

    private Guid SeedCollection(string providerId, string name, string? posterUrl = null)
    {
        var collection = new MovieCollection
        {
            Id = Guid.NewGuid(),
            Provider = "tmdb",
            ProviderId = providerId,
            Name = name,
            PosterUrl = posterUrl,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.MovieCollections.Add(collection);
        _database.SaveChanges();
        return collection.Id;
    }

    private Guid SeedMovie(string title, int year, Guid collectionId, bool published = true, string? posterUrl = null)
    {
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = published ? Guid.NewGuid().ToString("N") : null,
            CatalogId = _catalogId,
            Kind = MediaKind.Movie,
            Title = title,
            Year = year,
            CollectionId = collectionId,
            AddedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.MediaItems.Add(movie);
        _database.SaveChanges();

        if (posterUrl is not null)
        {
            _database.ImageAssets.Add(new ImageAsset
            {
                Id = Guid.NewGuid(),
                MediaItemId = movie.Id,
                ImageType = ImageType.Primary,
                Provider = "tmdb",
                RemotePath = posterUrl,
                Tag = "primary" + Guid.NewGuid().ToString("N")[..8],
                SortOrder = 0,
            });
            _database.SaveChanges();
        }

        return movie.Id;
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
