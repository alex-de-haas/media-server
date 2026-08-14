using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Recommendations.Generation;
using MediaServer.Api.Recommendations.Profile;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The generators that spend TMDb requests: more from a person the profile is loud about, and the
/// long tail reached by describing a taste rather than by following a link.
/// </summary>
public sealed class RemoteGeneratorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-08-14T12:00:00Z"));
    private readonly StubSource _tmdb = new();
    private readonly int _userId;
    private readonly Guid _catalogId = Guid.NewGuid();

    public RemoteGeneratorTests()
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

    private sealed class StubSource : ITmdbRecommendationSource
    {
        public List<string> Paths { get; } = [];

        public Dictionary<string, List<TmdbRecommendedTitle>> Lists { get; } = [];

        public Task<IReadOnlyList<TmdbRecommendedTitle>> ForSeedAsync(
            RecommendationIdentity seed, TmdbRecommendationGenerator generator, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TmdbRecommendedTitle>>([]);

        public Task<IReadOnlyList<TmdbRecommendedTitle>> ForListAsync(
            TmdbRecommendationGenerator generator,
            RecommendationKind kind,
            string cacheKey,
            string path,
            TimeSpan lifetime,
            IReadOnlyList<string> arrays,
            CancellationToken cancellationToken)
        {
            Paths.Add(path);
            return Task.FromResult<IReadOnlyList<TmdbRecommendedTitle>>(
                Lists.TryGetValue(path, out var list) ? list : []);
        }
    }

    [Fact]
    public async Task PeopleAsksAboutTheDirectorTheProfileIsLoudestAbout()
    {
        var director = AddPersonWithCredit("Denis Villeneuve", providerId: "137427", watched: true);
        _tmdb.Lists["person/137427/movie_credits"] = [Title("dune")];

        var produced = await People();

        Assert.Contains("person/137427/movie_credits", _tmdb.Paths);
        Assert.Contains("person/137427/tv_credits", _tmdb.Paths);
        Assert.Contains(produced, candidate => candidate.Identity.TmdbId == "dune");
        Assert.NotEqual(Guid.Empty, director);
    }

    [Fact]
    public async Task PeopleSkipsAPersonWithNoTmdbIdRatherThanGuessingOne()
    {
        AddPersonWithCredit("Local Only", providerId: "x1", watched: true, provider: "imdb");

        Assert.Empty(await People());
        Assert.Empty(_tmdb.Paths);
    }

    [Fact]
    public async Task PeopleSaysNothingWithoutAProfile()
    {
        AddPersonWithCredit("Denis Villeneuve", providerId: "137427", watched: false);

        Assert.Empty(await People(TasteProfile.Empty));
        Assert.Empty(_tmdb.Paths);
    }

    [Fact]
    public async Task DiscoverAsksForTheProfilesOwnGenresAndNotForWhatIsPopular()
    {
        // Sorting by popularity would hand back the same blockbusters every other path already found,
        // which is the bias this generator exists to walk around.
        AddWatchedMovie("Heat", "crime", "thriller");

        await Discover();

        var path = Assert.Single(_tmdb.Paths, candidate => candidate.StartsWith("discover/movie", StringComparison.Ordinal));
        Assert.Contains("with_genres=", path, StringComparison.Ordinal);
        Assert.Contains("sort_by=vote_count.desc", path, StringComparison.Ordinal);
        Assert.DoesNotContain("popularity", path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverSaysNothingWithoutAProfile()
    {
        Assert.Empty(await Discover(TasteProfile.Empty));
        Assert.Empty(_tmdb.Paths);
    }

    [Fact]
    public void ADiscoverSignatureIsShortStableAndDistinct()
    {
        // The cache column holds a title id's worth of characters. Truncating a query instead of
        // hashing it would collide two different tastes onto one cached answer.
        var first = DiscoverGenerator.Signature("with_genres=28,878&sort_by=vote_count.desc");
        var second = DiscoverGenerator.Signature("with_genres=18,80&sort_by=vote_count.desc");

        Assert.Equal(32, first.Length);
        Assert.Equal(first, DiscoverGenerator.Signature("with_genres=28,878&sort_by=vote_count.desc"));
        Assert.NotEqual(first, second);
    }

    private async Task<IReadOnlyList<GeneratedCandidate>> People(TasteProfile? profile = null) =>
        await new PeopleGenerator(_database, _tmdb).GenerateAsync(await ContextAsync(profile), CancellationToken.None);

    private async Task<IReadOnlyList<GeneratedCandidate>> Discover(TasteProfile? profile = null) =>
        await new DiscoverGenerator(_tmdb).GenerateAsync(await ContextAsync(profile), CancellationToken.None);

    private async Task<GeneratorContext> ContextAsync(TasteProfile? profile)
    {
        var seeds = await new RecommendationSeedSelector(_database, _time).SelectAsync(_userId, CancellationToken.None);
        var built = profile ?? await new TasteProfileBuilder(
                _database, new TitleFacetReader(_database), new LibraryFacetIndexCache(), _time)
            .BuildAsync(_userId, CancellationToken.None);

        return new GeneratorContext(_userId, seeds, seeds.Select(seed => seed.Identity).ToHashSet(), built, 20);
    }

    private static TmdbRecommendedTitle Title(string tmdbId) => new(tmdbId, $"Title {tmdbId}", 2024, null);

    private Guid AddPersonWithCredit(string name, string providerId, bool watched, string provider = "tmdb")
    {
        var movie = AddMovie("Sicario", "crime");
        var person = new Person { Id = Guid.NewGuid(), Name = name, Provider = provider, ProviderId = providerId };
        _database.Persons.Add(person);
        _database.MediaItemPersons.Add(new MediaItemPerson
        {
            Id = Guid.NewGuid(), MediaItemId = movie.Id, PersonId = person.Id, Role = PersonRole.Crew,
            Job = "Director", Department = "Directing", Order = 0,
        });
        _database.SaveChanges();

        if (watched)
        {
            AddPlay(movie.Id);
        }

        return person.Id;
    }

    private void AddWatchedMovie(string title, params string[] genres) => AddPlay(AddMovie(title, genres).Id);

    private MediaItem AddMovie(string title, params string[] genres)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = _catalogId, Kind = MediaKind.Movie, Title = title, Year = 2016,
            IdentityProvider = "tmdb", IdentityProviderId = Guid.NewGuid().ToString("N")[..8],
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(item);
        _database.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(), MediaItemId = item.Id, Provider = "tmdb", Language = "en-US",
            Genres = [.. genres], FetchedAt = _time.GetUtcNow(),
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

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
