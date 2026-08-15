using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Library;

namespace MediaServer.Api.Tests.Jellyfin;

/// <summary>
/// The Jellyfin "Collections" surface (Phase 2): a synthetic boxsets view whose children are <c>BoxSet</c>
/// folders (movie franchises), each holding the owned member movies. Only franchises with at least the
/// threshold of owned movies are exposed.
/// </summary>
public sealed class JellyfinCollectionsTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerSettings _settings = new() { SupportedLanguages = ["en-US"] };
    private readonly JellyfinLibraryService _library;

    private Guid _movieCatalogId;
    private Guid _franchiseId;
    private Guid _soloFranchiseId;
    private string _firstMoviePublicId = string.Empty;

    public JellyfinCollectionsTests()
    {
        var hosty = new HostyOptions { AppId = "com.haas.media-server", CoreOrigin = "http://localhost:3001", AppDataDir = Path.GetTempPath() };
        var server = new JellyfinServerContext(hosty, _settings);
        _library = new JellyfinLibraryService(
            _db.Create(), new JellyfinItemMapper(server, TestSettings.English), new JellyfinCatalogArtwork(_db.Create(), TestSettings.English), new JellyfinShelfArtwork(_db.Create(), new EmptyShelf(), TestSettings.English),
            new JellyfinCollectionService(_db.Create()), new JellyfinPersonService(_db.Create()), new EmptyShelf(), new UserDataService(_db.Create(), TimeProvider.System), _settings);
        Seed();
    }

    [Fact]
    public async Task Views_append_a_boxsets_collections_view_when_a_franchise_qualifies()
    {
        var views = await _library.GetViewsAsync(null, CancellationToken.None);

        var collections = Assert.Single(views, view => view.Id == JellyfinIds.CollectionsView());
        Assert.Equal("CollectionFolder", collections.Type);
        Assert.Equal("boxsets", collections.CollectionType);
        Assert.Equal("Collections", collections.Name);
        Assert.True(collections.IsFolder);
    }

    [Fact]
    public async Task Collections_view_lists_only_franchises_above_the_threshold()
    {
        var result = await _library.ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.CollectionsView() }, appUserId: null, CancellationToken.None);

        // The two-movie franchise surfaces as a BoxSet; the one-movie "franchise" does not.
        var boxset = Assert.Single(result.Items);
        Assert.Equal(JellyfinIds.Collection(_franchiseId), boxset.Id);
        Assert.DoesNotContain(result.Items, item => item.Id == JellyfinIds.Collection(_soloFranchiseId));
    }

    [Fact]
    public async Task BoxSet_maps_as_a_boxset_folder_under_the_collections_view()
    {
        var boxset = await _library.GetItemAsync(
            JellyfinIds.Collection(_franchiseId), includeMediaSources: false, appUserId: null, CancellationToken.None);

        Assert.NotNull(boxset);
        Assert.Equal("BoxSet", boxset!.Type);
        Assert.Equal("boxsets", boxset.CollectionType);
        Assert.True(boxset.IsFolder);
        Assert.Equal(JellyfinIds.CollectionsView(), boxset.ParentId);
        Assert.Equal(4, boxset.ChildCount);
        // The BoxSet advertises the collection's own poster.
        Assert.NotNull(boxset.ImageTags);
        Assert.True(boxset.ImageTags!.ContainsKey("Primary"));
        // And tells a client that sorts for itself to sort the franchise the way it was released.
        Assert.Equal("PremiereDate", boxset.DisplayOrder);
    }

    [Fact]
    public async Task Browsing_a_boxset_returns_its_member_movies()
    {
        var result = await _library.ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.Collection(_franchiseId) }, appUserId: null, CancellationToken.None);

        Assert.Equal(4, result.Items.Count);
        Assert.All(result.Items, item => Assert.Equal("Movie", item.Type));
        Assert.Contains(result.Items, item => item.Id == _firstMoviePublicId);
    }

    [Fact]
    public async Task Browsing_a_boxset_orders_its_members_by_release_year()
    {
        // A franchise is watched in release order, and these titles are named so that the alphabet
        // disagrees with it: sorted by name, "The Return of the King" would come second.
        var result = await _library.ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.Collection(_franchiseId) }, appUserId: null, CancellationToken.None);

        Assert.Equal(
            ["The Fellowship of the Ring", "The Two Towers", "The Return of the King", "The Hunt for Gollum"],
            result.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task A_member_whose_year_is_only_in_its_metadata_still_sorts_by_it()
    {
        // "The Return of the King" carries no MediaItem.Year — its year comes from the metadata record,
        // exactly as the ProductionYear the client renders does. Sorting must read the same value, or the
        // list would contradict the years printed on it.
        var result = await _library.ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.Collection(_franchiseId) }, appUserId: null, CancellationToken.None);

        var returnOfTheKing = Assert.Single(result.Items, item => item.Name == "The Return of the King");
        Assert.Equal(2003, returnOfTheKing.ProductionYear);
        Assert.Equal(2, result.Items.ToList().IndexOf(returnOfTheKing));
    }

    [Fact]
    public async Task Catalog_listings_are_still_ordered_by_title()
    {
        // Chronology is a BoxSet rule, not a library-wide one: the movie catalog itself stays alphabetical.
        var result = await _library.ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.Catalog(_movieCatalogId) }, appUserId: null, CancellationToken.None);

        Assert.Equal(
            result.Items.Select(item => item.Name).OrderBy(name => name, StringComparer.CurrentCulture),
            result.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task Collections_view_is_absent_when_no_franchise_qualifies()
    {
        // A fresh library with only a single-movie collection has nothing to surface.
        using var bare = new JellyfinDatabase();
        var hosty = new HostyOptions { AppId = "com.haas.media-server", CoreOrigin = "http://localhost:3001", AppDataDir = Path.GetTempPath() };
        var server = new JellyfinServerContext(hosty, _settings);
        var library = new JellyfinLibraryService(
            bare.Create(), new JellyfinItemMapper(server, TestSettings.English), new JellyfinCatalogArtwork(bare.Create(), TestSettings.English), new JellyfinShelfArtwork(bare.Create(), new EmptyShelf(), TestSettings.English),
            new JellyfinCollectionService(bare.Create()), new JellyfinPersonService(bare.Create()), new EmptyShelf(),
            new UserDataService(bare.Create(), TimeProvider.System), _settings);

        using (var context = bare.Create())
        {
            var now = DateTimeOffset.UtcNow;
            var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/m", CreatedAt = now, UpdatedAt = now };
            var collection = new MovieCollection { Id = Guid.NewGuid(), Provider = "tmdb", ProviderId = "1", Name = "Solo", UpdatedAt = now };
            context.Catalogs.Add(catalog);
            context.MovieCollections.Add(collection);
            context.MediaItems.Add(new MediaItem
            {
                Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = catalog.Id, Kind = MediaKind.Movie,
                Title = "Lonely", Year = 2000, CollectionId = collection.Id, AddedAt = now, UpdatedAt = now,
            });
            context.SaveChanges();
        }

        var views = await library.GetViewsAsync(null, CancellationToken.None);
        Assert.DoesNotContain(views, view => view.Id == JellyfinIds.CollectionsView());

        var view = await library.GetViewAsync(JellyfinIds.CollectionsView(), null, CancellationToken.None);
        Assert.Null(view);
    }

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();

        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/movies", CreatedAt = now, UpdatedAt = now };
        _movieCatalogId = catalog.Id;
        context.Catalogs.Add(catalog);

        // A qualifying franchise with its own poster.
        var franchise = new MovieCollection
        {
            Id = Guid.NewGuid(),
            Provider = "tmdb",
            ProviderId = "119",
            Name = "The Lord of the Rings Collection",
            PosterPath = "/lotr.jpg",
            PosterUrl = "https://image.tmdb.org/t/p/original/lotr.jpg",
            BackdropUrl = "https://image.tmdb.org/t/p/original/lotr-bd.jpg",
            UpdatedAt = now,
        };
        _franchiseId = franchise.Id;

        // A one-movie "franchise" that must stay below the surfacing threshold.
        var solo = new MovieCollection { Id = Guid.NewGuid(), Provider = "tmdb", ProviderId = "999", Name = "Solo Franchise", UpdatedAt = now };
        _soloFranchiseId = solo.Id;
        context.MovieCollections.AddRange(franchise, solo);

        var first = NewMovie(catalog.Id, "The Fellowship of the Ring", 2001, franchise.Id, now);
        // No Year of its own: its 2003 comes from the metadata record below, like the rendered
        // ProductionYear does.
        var third = NewMovie(catalog.Id, "The Return of the King", null, franchise.Id, now);
        _firstMoviePublicId = first.PublicId!;
        context.MediaItems.AddRange(
            first,
            NewMovie(catalog.Id, "The Two Towers", 2002, franchise.Id, now),
            third,
            // Nobody knows when this one came out; it must not head the franchise because of it.
            NewMovie(catalog.Id, "The Hunt for Gollum", null, franchise.Id, now),
            NewMovie(catalog.Id, "Solo Movie", 2010, solo.Id, now));

        context.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(),
            MediaItemId = third.Id,
            Provider = "tmdb",
            Language = "en-US",
            Title = "The Return of the King",
            ReleaseDate = new DateTimeOffset(2003, 12, 17, 0, 0, 0, TimeSpan.Zero),
            FetchedAt = now,
        });

        context.SaveChanges();
    }

    private static MediaItem NewMovie(Guid catalogId, string title, int? year, Guid collectionId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        PublicId = Guid.NewGuid().ToString("N"),
        CatalogId = catalogId,
        Kind = MediaKind.Movie,
        Title = title,
        Year = year,
        IdentityProvider = "tmdb",
        CollectionId = collectionId,
        AddedAt = now,
        UpdatedAt = now,
    };

    public void Dispose() => _db.Dispose();
}
