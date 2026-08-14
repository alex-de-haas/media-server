using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Recommendations.Generation;
using MediaServer.Api.Recommendations.Profile;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The two generators that cost no TMDb requests at all: the library ranked by the profile, and the
/// next film in a franchise already started.
/// </summary>
public sealed class LocalGeneratorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-08-14T12:00:00Z"));
    private readonly int _userId;
    private readonly Guid _catalogId = Guid.NewGuid();

    public LocalGeneratorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        var user = new AppUser
        {
            HostUserId = "host-1", Email = "alex@example.com", DisplayName = "Alex",
            Role = AppUserRole.User, CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
        };
        _database.AppUsers.Add(user);
        _database.SaveChanges();
        _userId = user.Id;

        _database.Catalogs.Add(new Catalog
        {
            Id = _catalogId, Name = "Library", Type = CatalogType.Movie, Root = "/m",
            CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
    }

    [Fact]
    public async Task HeldOffersUnwatchedLibraryTitlesThatMatchTheProfile()
    {
        var watched = AddMovie("Heat", "crime", tmdbId: "1");
        AddPlay(watched.Id);
        AddMovie("Sicario", "crime", tmdbId: "2");
        // A different genre *and* a different decade, so it shares nothing with the profile at all.
        AddMovie("Bambi", "family", tmdbId: "3", year: 1942);

        var produced = await Held();

        var identities = produced.Select(candidate => candidate.Identity.TmdbId).ToList();
        Assert.Contains("2", identities);
        Assert.DoesNotContain("1", identities); // already watched, and a seed besides
        Assert.DoesNotContain("3", identities); // nothing the profile recognizes
    }

    [Fact]
    public async Task HeldOffersAWeakMatchAndLeavesTheRankingToTheScorer()
    {
        // Sharing only a decade is a thin claim, but it is a claim, and the generator's job is to
        // propose rather than to decide. Filtering here would hide candidates the scorer might rank
        // above a strong match the viewer has already seen too much of.
        var watched = AddMovie("Heat", "crime", tmdbId: "1");
        AddPlay(watched.Id);
        AddMovie("Paddington", "family", tmdbId: "3");

        Assert.Contains(await Held(), candidate => candidate.Identity.TmdbId == "3");
    }

    [Fact]
    public async Task HeldMakesNoCollaborativeClaim()
    {
        // Its candidates earn their place from the profile alone. Inventing a collaborative weight
        // would let the library outrank titles several of the viewer's own seeds actually agreed on.
        var watched = AddMovie("Heat", "crime", tmdbId: "1");
        AddPlay(watched.Id);
        AddMovie("Sicario", "crime", tmdbId: "2");

        Assert.All(await Held(), candidate => Assert.Equal(0, candidate.Contribution));
    }

    [Fact]
    public async Task HeldCarriesTheLocalItemIdSoTheCardCanLinkToIt()
    {
        var watched = AddMovie("Heat", "crime", tmdbId: "1");
        AddPlay(watched.Id);
        var unwatched = AddMovie("Sicario", "crime", tmdbId: "2");

        var candidate = Assert.Single(await Held());

        Assert.Equal(unwatched.Id, candidate.MediaItemId);
    }

    [Fact]
    public async Task HeldSaysNothingWithoutAProfile()
    {
        // The library in arbitrary order is a list, not a recommendation; the cold-start ladder is
        // where a user with no history belongs.
        AddMovie("Sicario", "crime", tmdbId: "2");

        Assert.Empty(await Held(TasteProfile.Empty));
    }

    [Fact]
    public async Task HeldSkipsATitleMarkedPlayedEvenWithNoHistoryRow()
    {
        // A watched mark and a play are both "seen"; only one of them writes history.
        var watched = AddMovie("Heat", "crime", tmdbId: "1");
        AddPlay(watched.Id);
        var seen = AddMovie("Sicario", "crime", tmdbId: "2");
        _database.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = seen.Id, Played = true,
        });
        _database.SaveChanges();

        Assert.Empty(await Held());
    }

    [Fact]
    public async Task CollectionsOffersTheNextFilmInAFranchiseAlreadyStarted()
    {
        var collection = AddCollection("Mission: Impossible");
        var seen = AddMovie("M:I", "action", tmdbId: "1", collectionId: collection);
        AddMovie("M:I 2", "action", tmdbId: "2", collectionId: collection);
        AddMovie("Unrelated", "action", tmdbId: "3");
        AddPlay(seen.Id);

        var produced = await Collections();

        Assert.Equal("2", Assert.Single(produced).Identity.TmdbId);
    }

    [Fact]
    public async Task CollectionsNeverOffersATitleAlreadyTracked()
    {
        // It is already wanted. Suggesting it tells the viewer something they told the instance.
        var collection = AddCollection("Mission: Impossible");
        var seen = AddMovie("M:I", "action", tmdbId: "1", collectionId: collection);
        var next = AddMovie("M:I 2", "action", tmdbId: "2", collectionId: collection);
        AddPlay(seen.Id);
        Track(next, "2");

        Assert.Empty(await Collections());
    }

    [Fact]
    public async Task CollectionsSaysNothingWhenNoFranchiseHasBeenStarted()
    {
        var collection = AddCollection("Mission: Impossible");
        AddMovie("M:I", "action", tmdbId: "1", collectionId: collection);

        Assert.Empty(await Collections());
    }

    private async Task<IReadOnlyList<GeneratedCandidate>> Held(TasteProfile? profile = null) =>
        await new HeldGenerator(_database, new TitleFacetReader(_database))
            .GenerateAsync(await ContextAsync(profile), CancellationToken.None);

    private async Task<IReadOnlyList<GeneratedCandidate>> Collections() =>
        await new CollectionsGenerator(_database).GenerateAsync(await ContextAsync(null), CancellationToken.None);

    private async Task<GeneratorContext> ContextAsync(TasteProfile? profile)
    {
        var seeds = await new RecommendationSeedSelector(_database, _time)
            .SelectAsync(_userId, CancellationToken.None);
        var built = profile ?? await new TasteProfileBuilder(
                _database, new TitleFacetReader(_database), new LibraryFacetIndexCache(), _time)
            .BuildAsync(_userId, CancellationToken.None);

        return new GeneratorContext(
            _userId, seeds, seeds.Select(seed => seed.Identity).ToHashSet(), built, 20);
    }

    private Guid AddCollection(string name)
    {
        var collection = new MovieCollection
        {
            Id = Guid.NewGuid(), Provider = "tmdb", ProviderId = Guid.NewGuid().ToString("N")[..8], Name = name,
        };
        _database.MovieCollections.Add(collection);
        _database.SaveChanges();
        return collection.Id;
    }

    private MediaItem AddMovie(string title, string genre, string tmdbId, Guid? collectionId = null, int year = 2016)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = _catalogId, Kind = MediaKind.Movie, Title = title, Year = year,
            IdentityProvider = "tmdb", IdentityProviderId = tmdbId, CollectionId = collectionId,
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(item);
        _database.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(), MediaItemId = item.Id, Provider = "tmdb", Language = "en-US",
            Genres = [genre], FetchedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
        return item;
    }

    private void AddPlay(Guid itemId)
    {
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = itemId,
            CreatedAt = _time.GetUtcNow(), WatchedAt = _time.GetUtcNow(),
            Origin = PlaybackHistoryOrigin.LocalPlayback,
        });
        _database.SaveChanges();
    }

    private void Track(MediaItem item, string tmdbId)
    {
        var tracked = new TrackedTitle
        {
            Id = Guid.NewGuid(), Kind = MediaKind.Movie, IdentityProvider = "tmdb",
            IdentityProviderId = tmdbId, MediaItemId = item.Id, Title = item.Title,
        };
        _database.TrackedTitles.Add(tracked);
        _database.WatchlistEntries.Add(new WatchlistEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, TrackedTitleId = tracked.Id, CreatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
    }

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
