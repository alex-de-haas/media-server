using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// A catalog listing must come back in the order of the names it renders. Both read surfaces show the
/// localized <see cref="MetadataRecord.Title"/> while <see cref="MediaItem.Title"/> keeps whatever
/// language the item matched in at ingest, so ordering the raw column leaves the grid in an order the
/// visible names do not explain. These tests pin the two surfaces to the same, localized order.
/// </summary>
public sealed class CatalogOrderingTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private Guid _catalogId;

    public CatalogOrderingTests() => _catalogId = SeedCatalog();

    /// <summary>
    /// Raw titles ascend A→Z while the Russian titles they render as do not, so a listing ordered by the
    /// raw column comes back visibly shuffled.
    /// </summary>
    private static readonly (string Raw, string? Localized)[] RussianLibrary =
    [
        ("Arrival.2016.1080p", "Прибытие"),
        ("Back.to.the.Future.1985", "Назад в будущее"),
        ("Casablanca.1942", "Касабланка"),
    ];

    private static readonly string[] RussianOrder = ["Касабланка", "Назад в будущее", "Прибытие"];

    [Fact]
    public async Task Web_listing_orders_by_the_localized_title()
    {
        Seed(RussianLibrary, "ru-RU");

        var titles = (await WebLibrary("ru-RU").ListAsync(_catalogId, kind: null, appUserId: null, CancellationToken.None))
            .Select(item => item.Title)
            .ToList();

        Assert.Equal(RussianOrder, titles);
    }

    [Fact]
    public async Task Jellyfin_listing_orders_by_the_localized_title()
    {
        Seed(RussianLibrary, "ru-RU");

        var result = await JellyfinLibrary("ru-RU").ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.Catalog(_catalogId) }, appUserId: null, CancellationToken.None);

        Assert.Equal(RussianOrder, result.Items.Select(item => item.Name!).ToList());
    }

    /// <summary>
    /// Paging is applied after ordering, so a page must be a slice of the localized order rather than of
    /// the raw one — the bug is invisible on an unpaged list but reorders every page boundary.
    /// </summary>
    [Fact]
    public async Task Jellyfin_paging_slices_the_localized_order()
    {
        Seed(RussianLibrary, "ru-RU");

        var result = await JellyfinLibrary("ru-RU").ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.Catalog(_catalogId), StartIndex = 0, Limit = 2 },
            appUserId: null, CancellationToken.None);

        Assert.Equal(3, result.TotalRecordCount);
        Assert.Equal(["Касабланка", "Назад в будущее"], result.Items.Select(item => item.Name!).ToList());
    }

    /// <summary>
    /// SQLite's default <c>BINARY</c> collation orders by code point, filing every lowercase initial after
    /// every uppercase one ("the Matrix" after "Zulu"). Ordering runs under the display language's
    /// collation instead.
    /// </summary>
    [Fact]
    public async Task Listing_ignores_case_rather_than_ordering_by_code_point()
    {
        Seed([("a", "the Matrix"), ("b", "Zulu"), ("c", "Alien")], "en-US");

        var titles = (await WebLibrary("en-US").ListAsync(_catalogId, kind: null, appUserId: null, CancellationToken.None))
            .Select(item => item.Title)
            .ToList();

        Assert.Equal(["Alien", "the Matrix", "Zulu"], titles);
    }

    /// <summary>
    /// An item enriched before a language was added has no record in it. It still has to sort somewhere,
    /// and the only name either surface can render for it is the raw title — so that is what it sorts by.
    /// </summary>
    [Fact]
    public async Task An_item_without_a_localized_title_sorts_under_its_raw_title()
    {
        Seed(RussianLibrary, "ru-RU");
        Seed([("Остров", null)], "ru-RU");

        var titles = (await WebLibrary("ru-RU").ListAsync(_catalogId, kind: null, appUserId: null, CancellationToken.None))
            .Select(item => item.Title)
            .ToList();

        Assert.Equal(["Касабланка", "Назад в будущее", "Остров", "Прибытие"], titles);
    }

    /// <summary>Episodes stay in broadcast order — the index numbers still outrank the title.</summary>
    [Fact]
    public async Task Episodes_keep_their_index_order()
    {
        var seriesCatalog = SeedCatalog(CatalogType.Series);
        var series = AddItem(seriesCatalog, MediaKind.Series, "Breaking Bad", localized: null);
        var season = AddItem(seriesCatalog, MediaKind.Season, "Season 1", localized: null,
            parent: series, series: series, season: 1);
        AddItem(seriesCatalog, MediaKind.Episode, "S01E02", "Кошачья лапа", parent: season, series: series, season: 1, episode: 2);
        AddItem(seriesCatalog, MediaKind.Episode, "S01E01", "Явление", parent: season, series: series, season: 1, episode: 1);

        var result = await JellyfinLibrary("ru-RU").ListItemsAsync(
            new JellyfinItemsQuery { ParentId = season.PublicId }, appUserId: null, CancellationToken.None);

        Assert.Equal(["Явление", "Кошачья лапа"], result.Items.Select(item => item.Name!).ToList());
    }

    private LibraryReadService WebLibrary(string language) =>
        new(_db.Create(), new UserDataService(_db.Create(), TimeProvider.System), Settings(language));

    private JellyfinLibraryService JellyfinLibrary(string language)
    {
        var settings = Settings(language);
        var hosty = new HostyOptions
        {
            AppId = "com.haas.media-server",
            CoreOrigin = "http://localhost:3001",
            AppDataDir = Path.GetTempPath(),
        };
        return new JellyfinLibraryService(
            _db.Create(), new JellyfinItemMapper(new JellyfinServerContext(hosty, settings), settings),
            new JellyfinCatalogArtwork(_db.Create(), settings), new JellyfinCollectionService(_db.Create()),
            new JellyfinPersonService(_db.Create()), new EmptyShelf(),
            new UserDataService(_db.Create(), TimeProvider.System), settings);
    }

    private static MediaServerSettings Settings(string language) => new() { SupportedLanguages = [language] };

    private Guid SeedCatalog(CatalogType type = CatalogType.Movie)
    {
        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = type.ToString(),
            Type = type,
            Root = "/" + type.ToString().ToLowerInvariant(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.Catalogs.Add(catalog);
        context.SaveChanges();
        return _catalogId = catalog.Id;
    }

    private void Seed(IReadOnlyList<(string Raw, string? Localized)> movies, string language)
    {
        foreach (var (raw, localized) in movies)
        {
            AddItem(_catalogId, MediaKind.Movie, raw, localized, language);
        }
    }

    private MediaItem AddItem(
        Guid catalogId, MediaKind kind, string raw, string? localized, string language = "ru-RU",
        MediaItem? parent = null, MediaItem? series = null, int? season = null, int? episode = null)
    {
        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = catalogId,
            Kind = kind,
            Title = raw,
            ParentId = parent?.Id,
            SeriesId = series?.Id,
            SeasonId = kind == MediaKind.Episode ? parent?.Id : null,
            ParentIndexNumber = kind == MediaKind.Episode ? season : null,
            IndexNumber = kind == MediaKind.Season ? season : episode,
            AddedAt = now,
            UpdatedAt = now,
        };
        context.MediaItems.Add(item);
        if (localized is not null)
        {
            context.MetadataRecords.Add(new MetadataRecord
            {
                Id = Guid.NewGuid(),
                MediaItemId = item.Id,
                Provider = "tmdb",
                Language = language,
                Title = localized,
                FetchedAt = now,
            });
        }

        context.SaveChanges();
        return item;
    }

    public void Dispose() => _db.Dispose();
}
