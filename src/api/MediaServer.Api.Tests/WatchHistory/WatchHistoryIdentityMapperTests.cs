using MediaServer.Api.Data;
using MediaServer.Api.WatchHistory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.WatchHistory;

public sealed class WatchHistoryIdentityMapperTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly Guid _movieCatalogId = Guid.NewGuid();
    private readonly Guid _seriesCatalogId = Guid.NewGuid();
    private readonly Guid _seriesId = Guid.NewGuid();
    private readonly Guid _seasonId = Guid.NewGuid();

    public WatchHistoryIdentityMapperTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        _database.Catalogs.AddRange(
            new Catalog { Id = _movieCatalogId, Name = "Movies", Type = CatalogType.Movie, Root = "/m", CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch },
            new Catalog { Id = _seriesCatalogId, Name = "Shows", Type = CatalogType.Series, Root = "/s", CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch });

        _database.MediaItems.AddRange(
            Item(_seriesId, MediaKind.Series, _seriesCatalogId, "Futurama", providers: new() { ["tmdb"] = "615" }),
            Item(_seasonId, MediaKind.Season, _seriesCatalogId, "Season 1", parentId: _seriesId, seriesId: _seriesId, index: 1));
        _database.SaveChanges();
    }

    private static MediaItem Item(
        Guid id, MediaKind kind, Guid catalogId, string title,
        Dictionary<string, string>? providers = null, Guid? parentId = null, Guid? seriesId = null,
        int? index = null, int? parentIndex = null, int? indexEnd = null) => new()
        {
            Id = id,
            PublicId = Guid.NewGuid().ToString("N"),
            CatalogId = catalogId,
            Kind = kind,
            Title = title,
            Providers = providers ?? [],
            ParentId = parentId,
            // Season membership is SeasonId across the codebase (see DescendantEpisodeIdsAsync);
            // ParentId is the generic tree link and the two must agree for episodes.
            SeasonId = kind == MediaKind.Episode ? parentId : null,
            SeriesId = seriesId,
            IndexNumber = index,
            ParentIndexNumber = parentIndex,
            IndexNumberEnd = indexEnd,
            AddedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

    private MediaItem AddMovie(
        Dictionary<string, string>? providers, Guid? catalogId = null,
        string? identityProvider = null, string? identityProviderId = null)
    {
        var movie = Item(Guid.NewGuid(), MediaKind.Movie, catalogId ?? _movieCatalogId, "Inception", providers);
        movie.IdentityProvider = identityProvider;
        movie.IdentityProviderId = identityProviderId;
        _database.MediaItems.Add(movie);
        _database.SaveChanges();
        return movie;
    }

    private MediaItem AddEpisode(
        int? index = 1, int? parentIndex = 1, int? indexEnd = null, Dictionary<string, string>? providers = null)
    {
        var episode = Item(
            Guid.NewGuid(), MediaKind.Episode, _seriesCatalogId, $"Episode {index}",
            providers ?? new() { ["tmdb"] = "615" },
            parentId: _seasonId, seriesId: _seriesId, index: index, parentIndex: parentIndex, indexEnd: indexEnd);
        _database.MediaItems.Add(episode);
        _database.SaveChanges();
        return episode;
    }

    private WatchHistoryIdentityMapper Mapper() => new(_database);

    [Fact]
    public async Task AMovieMapsFromItsTmdbId()
    {
        var result = await Mapper().MapAsync(AddMovie(new() { ["tmdb"] = "27205" }), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal(WatchHistoryMediaKind.Movie, result.Identity!.Kind);
        Assert.Equal(27205, result.Identity.TmdbId);
    }

    [Fact]
    public async Task AnImdbOnlyMovieStillMaps()
    {
        var result = await Mapper().MapAsync(AddMovie(new() { ["imdb"] = "tt1375666" }), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal("tt1375666", result.Identity!.ImdbId);
    }

    [Fact]
    public async Task ProviderKeysAreMatchedWithoutRegardToCase()
    {
        // A hand-edited or older row must not silently stop resolving.
        var result = await Mapper().MapAsync(AddMovie(new() { ["TMDB"] = "27205" }), CancellationToken.None);

        Assert.Equal(27205, result.Identity!.TmdbId);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public async Task AnUnusableTmdbValueIsNotTreatedAsAnId(string value)
    {
        // Sending a wrong identity writes a viewing onto someone else's title, so a junk value must
        // report as missing rather than be coerced.
        var result = await Mapper().MapAsync(AddMovie(new() { ["tmdb"] = value }), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(WatchHistoryIdentityIssue.MissingExternalId, result.Issue);
    }

    [Fact]
    public async Task AnUnidentifiedMovieReportsAMissingId()
    {
        var result = await Mapper().MapAsync(AddMovie(providers: null), CancellationToken.None);

        Assert.Equal(WatchHistoryIdentityIssue.MissingExternalId, result.Issue);
    }

    [Fact]
    public async Task AnEpisodeMapsToItsSeriesIdPlusCoordinates()
    {
        // Providers have no per-episode external id; the series plus season/episode is the address.
        var result = await Mapper().MapAsync(AddEpisode(index: 4, parentIndex: 2), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal(615, result.Identity!.TmdbId);
        Assert.Equal(2, result.Identity.SeasonNumber);
        Assert.Equal(4, result.Identity.EpisodeNumber);
    }

    [Fact]
    public async Task AnEpisodeWithoutItsOwnIdsFallsBackToTheSeries()
    {
        // Older or partially identified libraries did not copy the ids down onto each episode.
        var result = await Mapper().MapAsync(AddEpisode(providers: []), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal(615, result.Identity!.TmdbId);
    }

    [Fact]
    public async Task AMultiEpisodeFileCarriesItsRange()
    {
        var result = await Mapper().MapAsync(AddEpisode(index: 1, indexEnd: 2), CancellationToken.None);

        Assert.Equal(1, result.Identity!.EpisodeNumber);
        Assert.Equal(2, result.Identity.EpisodeNumberEnd);
        Assert.Equal([1, 2], result.Identity.Expand().Select(identity => identity.EpisodeNumber));
    }

    [Fact]
    public async Task ASingleEpisodeLeavesTheRangeEndNull()
    {
        // Never a degenerate range, so nothing downstream has to re-interpret it.
        var result = await Mapper().MapAsync(AddEpisode(index: 3, indexEnd: 3), CancellationToken.None);

        Assert.Null(result.Identity!.EpisodeNumberEnd);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(1, null)]
    public async Task AnEpisodeMissingCoordinatesReportsIt(int? index, int? parentIndex)
    {
        var result = await Mapper().MapAsync(AddEpisode(index: index, parentIndex: parentIndex), CancellationToken.None);

        Assert.False(result.Resolved);
        Assert.Equal(WatchHistoryIdentityIssue.MissingEpisodeNumbering, result.Issue);
    }

    [Fact]
    public async Task SeasonZeroMapsBecauseItIsTheSpecialsSeason()
    {
        var result = await Mapper().MapAsync(AddEpisode(index: 1, parentIndex: 0), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal(0, result.Identity!.SeasonNumber);
    }

    [Fact]
    public async Task FoldersAndExtrasAreNotSyncable()
    {
        var series = await _database.MediaItems.FirstAsync(item => item.Id == _seriesId);
        var season = await _database.MediaItems.FirstAsync(item => item.Id == _seasonId);

        Assert.Equal(WatchHistoryIdentityIssue.UnsupportedKind, (await Mapper().MapAsync(series, CancellationToken.None)).Issue);
        Assert.Equal(WatchHistoryIdentityIssue.UnsupportedKind, (await Mapper().MapAsync(season, CancellationToken.None)).Issue);
    }

    [Fact]
    public async Task ASeasonExpandsToItsEpisodesInOrder()
    {
        AddEpisode(index: 2);
        AddEpisode(index: 1);
        var season = await _database.MediaItems.FirstAsync(item => item.Id == _seasonId);

        var mapped = await Mapper().MapDescendantsAsync(season, CancellationToken.None);

        Assert.Equal(2, mapped.Count);
        Assert.Equal([1, 2], mapped.Select(entry => entry.Result.Identity!.EpisodeNumber));
    }

    [Fact]
    public async Task ASeriesExpandsAcrossItsSeasons()
    {
        AddEpisode(index: 1, parentIndex: 1);
        AddEpisode(index: 1, parentIndex: 2);
        var series = await _database.MediaItems.FirstAsync(item => item.Id == _seriesId);

        var mapped = await Mapper().MapDescendantsAsync(series, CancellationToken.None);

        Assert.Equal([1, 2], mapped.Select(entry => entry.Result.Identity!.SeasonNumber));
    }

    [Fact]
    public async Task AnUnmappableDescendantIsReportedRatherThanDroppedSilently()
    {
        // The caller has to be able to tell the user which episodes could not be synced.
        AddEpisode(index: 1);
        AddEpisode(index: null);
        var season = await _database.MediaItems.FirstAsync(item => item.Id == _seasonId);

        var mapped = await Mapper().MapDescendantsAsync(season, CancellationToken.None);

        Assert.Equal(2, mapped.Count);
        Assert.Contains(mapped, entry => !entry.Result.Resolved);
    }

    [Fact]
    public async Task TheCanonicalIdentityWinsOverTheProviderMap()
    {
        // Identification records the id it actually chose; the provider map is display-facing and can
        // lag behind it.
        var movie = AddMovie(new() { ["tmdb"] = "111" }, identityProvider: "tmdb", identityProviderId: "27205");

        var result = await Mapper().MapAsync(movie, CancellationToken.None);

        Assert.Equal(27205, result.Identity!.TmdbId);
    }

    [Fact]
    public async Task CanonicalEpisodeNumbersWinOverTheDisplayNumbering()
    {
        // Identification re-maps some releases — anime absolute numbering, for instance — onto the
        // provider's season and episode, and that is what a provider must be told.
        var episode = AddEpisode(index: 37, parentIndex: 1);
        episode.IdentitySeasonNumber = 2;
        episode.IdentityEpisodeNumber = 11;
        _database.SaveChanges();

        var result = await Mapper().MapAsync(episode, CancellationToken.None);

        Assert.Equal(2, result.Identity!.SeasonNumber);
        Assert.Equal(11, result.Identity.EpisodeNumber);
    }

    [Fact]
    public async Task ARangeIsOffsetFromTheCanonicalFirstEpisode()
    {
        // The range width is a fact about the file; its start is the canonical number.
        var episode = AddEpisode(index: 37, parentIndex: 1, indexEnd: 38);
        episode.IdentitySeasonNumber = 2;
        episode.IdentityEpisodeNumber = 11;
        _database.SaveChanges();

        var result = await Mapper().MapAsync(episode, CancellationToken.None);

        Assert.Equal(11, result.Identity!.EpisodeNumber);
        Assert.Equal(12, result.Identity.EpisodeNumberEnd);
    }

    [Fact]
    public async Task ADeletedItemIsDistinguishedFromAnUnidentifiedOne()
    {
        // Different remedies: one is "re-identify this file", the other is "this row is gone".
        var result = await Mapper().MapAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(WatchHistoryIdentityIssue.ItemNotFound, result.Issue);
    }

    [Fact]
    public async Task TwoEditionsAreFlaggedThroughTheCanonicalIdentityToo()
    {
        var first = AddMovie(null, identityProvider: "tmdb", identityProviderId: "27205");
        AddMovie(null, identityProvider: "tmdb", identityProviderId: "27205");

        Assert.True(await Mapper().HasAmbiguousLocalIdentityAsync(first, catalogScope: null, CancellationToken.None));
    }

    [Fact]
    public async Task ARowWithoutACanonicalIdentityIsStillCompared()
    {
        // Older rows carry only the provider map; declaring them unique would miss a real duplicate.
        var first = AddMovie(new() { ["tmdb"] = "27205" }, identityProvider: "tmdb", identityProviderId: "27205");
        AddMovie(new() { ["tmdb"] = "27205" });

        Assert.True(await Mapper().HasAmbiguousLocalIdentityAsync(first, catalogScope: null, CancellationToken.None));
    }

    [Fact]
    public async Task TwoEditionsOfOneFilmAreFlaggedAsAmbiguous()
    {
        // One work to a provider, two rows locally: applying a remote state to one is arbitrary, and
        // to both can clear an edition's resume point.
        var first = AddMovie(new() { ["tmdb"] = "27205" });
        AddMovie(new() { ["tmdb"] = "27205" });

        Assert.True(await Mapper().HasAmbiguousLocalIdentityAsync(first, catalogScope: null, CancellationToken.None));
    }

    [Fact]
    public async Task ADifferentFilmIsNotAmbiguous()
    {
        var first = AddMovie(new() { ["tmdb"] = "27205" });
        AddMovie(new() { ["tmdb"] = "1375666" });

        Assert.False(await Mapper().HasAmbiguousLocalIdentityAsync(first, catalogScope: null, CancellationToken.None));
    }

    [Fact]
    public async Task ACatalogScopeCanDisambiguateEditions()
    {
        // Selecting one catalog is the user's way of saying which copy they mean.
        var otherCatalog = Guid.NewGuid();
        _database.Catalogs.Add(new Catalog
        {
            Id = otherCatalog, Name = "4K", Type = CatalogType.Movie, Root = "/4k",
            CreatedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        _database.SaveChanges();

        var first = AddMovie(new() { ["tmdb"] = "27205" });
        AddMovie(new() { ["tmdb"] = "27205" }, catalogId: otherCatalog);

        Assert.False(await Mapper().HasAmbiguousLocalIdentityAsync(first, [_movieCatalogId], CancellationToken.None));
        Assert.True(await Mapper().HasAmbiguousLocalIdentityAsync(first, [_movieCatalogId, otherCatalog], CancellationToken.None));
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
