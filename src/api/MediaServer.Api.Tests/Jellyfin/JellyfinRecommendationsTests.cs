using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Library;
using MediaServer.Api.Recommendations;

namespace MediaServer.Api.Tests.Jellyfin;

/// <summary>
/// The Jellyfin "Recommended" surface: a synthetic mixed-content view holding the part of the
/// recommendation feed this instance actually has, so a suggestion is something the user can play.
/// </summary>
/// <remarks>
/// The shelf itself is stubbed here. What this suite is about is the wiring — whether the view is
/// advertised, whether the row and the grid agree, and whether rank survives both — and the shelf's
/// own rules are covered by <c>RecommendationShelfServiceTests</c>.
/// </remarks>
public sealed class JellyfinRecommendationsTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerSettings _settings = new() { SupportedLanguages = ["en-US"] };
    private readonly StubShelf _shelf = new();
    private readonly JellyfinLibraryService _library;
    private const int UserId = 7;

    private MediaItem _first = null!;
    private MediaItem _second = null!;
    private MediaItem _series = null!;

    public JellyfinRecommendationsTests()
    {
        var hosty = new HostyOptions { AppId = "com.haas.media-server", CoreOrigin = "http://localhost:3001", AppDataDir = Path.GetTempPath() };
        var server = new JellyfinServerContext(hosty, _settings);
        _library = new JellyfinLibraryService(
            _db.Create(), new JellyfinItemMapper(server), new JellyfinCatalogArtwork(_db.Create()),
            new JellyfinCollectionService(_db.Create()), _shelf, new UserDataService(_db.Create(), TimeProvider.System), _settings);
        Seed();
    }

    [Fact]
    public async Task TheViewIsAdvertisedOnlyWhenTheShelfHasSomething()
    {
        Assert.DoesNotContain(
            await _library.GetViewsAsync(UserId, CancellationToken.None),
            view => view.Id == JellyfinIds.RecommendationsView());

        _shelf.Items = [_first];

        var view = Assert.Single(
            await _library.GetViewsAsync(UserId, CancellationToken.None),
            candidate => candidate.Id == JellyfinIds.RecommendationsView());
        Assert.Equal("CollectionFolder", view.Type);
        Assert.Equal("Recommended", view.Name);
        Assert.True(view.IsFolder);
    }

    [Fact]
    public async Task TheViewIsMixedContentRatherThanAMoviesOrBoxsetsLibrary()
    {
        // A null CollectionType is Jellyfin's mixed library; the shelf holds series as well as films,
        // so anything more specific would be a lie. Verified against Infuse 8.x.
        _shelf.Items = [_first, _series];

        var view = Assert.Single(
            await _library.GetViewsAsync(UserId, CancellationToken.None),
            candidate => candidate.Id == JellyfinIds.RecommendationsView());

        Assert.Null(view.CollectionType);
    }

    [Fact]
    public async Task AnAnonymousCallerIsNeverOfferedTheView()
    {
        // The shelf is personal; without an acting user there is no shelf to advertise.
        _shelf.Items = [_first];

        Assert.DoesNotContain(
            await _library.GetViewsAsync(null, CancellationToken.None),
            view => view.Id == JellyfinIds.RecommendationsView());
    }

    [Fact]
    public async Task TheViewIdResolvesAsAView()
    {
        _shelf.Items = [_first];

        var view = await _library.GetViewAsync(JellyfinIds.RecommendationsView(), UserId, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal("Recommended", view!.Name);
    }

    [Fact]
    public async Task TheViewIdDoesNotResolveWhileTheShelfIsEmpty()
    {
        Assert.Null(await _library.GetViewAsync(JellyfinIds.RecommendationsView(), UserId, CancellationToken.None));
    }

    [Fact]
    public async Task LatestReturnsTheShelfInRankOrder()
    {
        // This row is how the shelf reaches the home screen at all, which is why it returns the
        // selection rather than the empty result the Collections view gives.
        _shelf.Items = [_second, _first];

        var latest = await _library.GetLatestAsync(
            JellyfinIds.RecommendationsView(), limit: 20, UserId, CancellationToken.None);

        Assert.Equal([_second.PublicId, _first.PublicId], latest.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task LatestStillReturnsNothingForTheCollectionsView()
    {
        // The two synthetic views differ deliberately: "recently added to a franchise" means nothing.
        _shelf.Items = [_first];

        var latest = await _library.GetLatestAsync(
            JellyfinIds.CollectionsView(), limit: 20, UserId, CancellationToken.None);

        Assert.Empty(latest.Items);
    }

    [Fact]
    public async Task LatestPassesItsLimitToTheShelf()
    {
        _shelf.Items = [_first, _second, _series];

        await _library.GetLatestAsync(JellyfinIds.RecommendationsView(), limit: 2, UserId, CancellationToken.None);

        Assert.Equal(2, _shelf.LastLimit);
    }

    [Fact]
    public async Task BrowsingTheViewKeepsRankRatherThanSortingByTitle()
    {
        // The regular browse path ends in an alphabetical sort, which would replace rank with the
        // alphabet before the client ever saw it. "Zulu" first is the whole assertion.
        _shelf.Items = [_second, _first];

        var page = await _library.ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.RecommendationsView() }, UserId, CancellationToken.None);

        Assert.Equal([_second.PublicId, _first.PublicId], page.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task BrowsingHonorsAnExplicitTypeFilter()
    {
        // Infuse sends none for an untyped view, but other clients do, and a mixed view must not
        // answer a request for movies with series.
        _shelf.Items = [_first, _series];

        var page = await _library.ListItemsAsync(
            new JellyfinItemsQuery
            {
                ParentId = JellyfinIds.RecommendationsView(),
                IncludeItemTypes = new HashSet<string> { "Movie" },
            },
            UserId,
            CancellationToken.None);

        Assert.Equal([_first.PublicId], page.Items.Select(item => item.Id));
        Assert.Equal(1, page.TotalRecordCount);
    }

    [Fact]
    public async Task BrowsingPagesWithoutLosingTheTotal()
    {
        _shelf.Items = [_first, _second, _series];

        var page = await _library.ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.RecommendationsView(), StartIndex = 1, Limit = 1 },
            UserId,
            CancellationToken.None);

        Assert.Equal([_second.PublicId], page.Items.Select(item => item.Id));
        Assert.Equal(3, page.TotalRecordCount);
        Assert.Equal(1, page.StartIndex);
    }

    [Fact]
    public async Task TheViewItselfNeverAppearsAsAnItemInAFlatScan()
    {
        // Views are libraries, not content; a recursive scan returns Movie/Series/Episode only.
        _shelf.Items = [_first];

        var all = await _library.ListItemsAsync(
            new JellyfinItemsQuery { Recursive = true }, UserId, CancellationToken.None);

        Assert.DoesNotContain(all.Items, item => item.Id == JellyfinIds.RecommendationsView());
    }

    [Fact]
    public async Task AnAnonymousCallerBrowsingTheViewGetsNothingRatherThanTheWholeLibrary()
    {
        _shelf.Items = [_first, _second];

        var page = await _library.ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.RecommendationsView() }, null, CancellationToken.None);

        Assert.Empty(page.Items);
    }

    private sealed class StubShelf : IRecommendationShelf
    {
        public IReadOnlyList<MediaItem> Items { get; set; } = [];

        public int? LastLimit { get; private set; }

        public Task<IReadOnlyList<MediaItem>> GetAsync(int appUserId, int? limit, CancellationToken cancellationToken)
        {
            LastLimit = limit;
            return Task.FromResult<IReadOnlyList<MediaItem>>(
                limit is { } wanted ? [.. Items.Take(wanted)] : Items);
        }

        public Task<bool> AnyAsync(int appUserId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.Count > 0);
    }

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();

        var catalog = new Catalog
        {
            Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/movies",
            CreatedAt = now, UpdatedAt = now,
        };
        context.Catalogs.Add(catalog);

        // Named so that rank order and alphabetical order disagree.
        _first = NewItem(catalog.Id, MediaKind.Movie, "Zulu", now);
        _second = NewItem(catalog.Id, MediaKind.Movie, "Alpha", now);
        _series = NewItem(catalog.Id, MediaKind.Series, "Middle", now);
        context.MediaItems.AddRange(_first, _second, _series);

        context.SaveChanges();
    }

    private static MediaItem NewItem(Guid catalogId, MediaKind kind, string title, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        PublicId = Guid.NewGuid().ToString("N"),
        CatalogId = catalogId,
        Kind = kind,
        Title = title,
        Year = 2024,
        IdentityProvider = "tmdb",
        AddedAt = now,
        UpdatedAt = now,
    };

    public void Dispose() => _db.Dispose();
}
