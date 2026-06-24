using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Metadata;
using MediaServer.Api.People;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.People;

/// <summary>
/// Read behaviour of <see cref="PersonReadService"/>: the filmography is assembled from the
/// <see cref="MediaItemPerson"/> join (limited to published library items, split into cast and crew grouped
/// by department, reusing the library poster/title projection), and the long-form person details are fetched
/// lazily from the provider and cached on the row with a staleness window.
/// </summary>
public sealed class PersonReadServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly Guid _catalogId;

    public PersonReadServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();
        _catalogId = SeedCatalog();
    }

    private PersonReadService Service(StubMetadataProvider provider)
    {
        var settings = new MediaServerSettings { SupportedLanguages = ["en-US"] };
        var library = new LibraryReadService(_database, new UserDataService(_database, TimeProvider.System), settings);
        return new PersonReadService(_database, library, provider, settings, TimeProvider.System, NullLogger<PersonReadService>.Instance);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_person()
    {
        var person = await Service(new StubMetadataProvider()).GetAsync("tmdb", "404", CancellationToken.None);
        Assert.Null(person);
    }

    [Fact]
    public async Task Get_assembles_filmography_from_the_join_split_into_cast_and_crew()
    {
        var nolan = SeedPerson("525", "Christopher Nolan", detailsFetchedAt: DateTimeOffset.UtcNow);
        var inception = SeedItem(MediaKind.Movie, "Inception", 2010);
        var interstellar = SeedItem(MediaKind.Movie, "Interstellar", 2014);
        var westworld = SeedItem(MediaKind.Series, "Westworld", 2016);
        var unpublished = SeedItem(MediaKind.Movie, "Unreleased Draft", 2030, published: false);
        var episode = SeedItem(MediaKind.Episode, "Some Episode", 2016); // leaves never carry credits; must be ignored

        // Crew (director) across two movies + a series, plus a writing credit and a one-off cast cameo.
        SeedCredit(nolan, inception, PersonRole.Crew, job: "Director", department: "Directing", order: 0);
        SeedCredit(nolan, interstellar, PersonRole.Crew, job: "Director", department: "Directing", order: 0);
        SeedCredit(nolan, westworld, PersonRole.Crew, job: "Executive Producer", department: "Production", order: 0);
        SeedCredit(nolan, interstellar, PersonRole.Crew, job: "Writer", department: "Writing", order: 1);
        SeedCredit(nolan, inception, PersonRole.Cast, character: "Cameo", order: 3);
        SeedCredit(nolan, unpublished, PersonRole.Crew, job: "Director", department: "Directing", order: 0);
        SeedCredit(nolan, episode, PersonRole.Crew, job: "Director", department: "Directing", order: 0);

        var person = await Service(new StubMetadataProvider()).GetAsync("tmdb", "525", CancellationToken.None);

        Assert.NotNull(person);
        Assert.Equal("Christopher Nolan", person!.Name);

        // Cast: only the cameo, and only the published movie.
        var cast = Assert.Single(person.Cast);
        Assert.Equal(inception, cast.Id);
        Assert.Equal("Movie", cast.Kind);
        Assert.Equal("Cameo", cast.Character);
        Assert.Null(cast.Job);
        Assert.Equal(2010, cast.Year);

        // Crew grouped by department, departments alphabetical, entries newest first. The unpublished movie
        // and the episode credit are excluded.
        Assert.Collection(
            person.Crew,
            directing =>
            {
                Assert.Equal("Directing", directing.Department);
                Assert.Collection(
                    directing.Credits,
                    first => Assert.Equal(interstellar, first.Id),  // 2014 before 2010
                    second => Assert.Equal(inception, second.Id));
                Assert.All(directing.Credits, credit => Assert.Equal("Director", credit.Job));
            },
            production =>
            {
                Assert.Equal("Production", production.Department);
                var only = Assert.Single(production.Credits);
                Assert.Equal(westworld, only.Id);
                Assert.Equal("Series", only.Kind);
                Assert.Equal("Executive Producer", only.Job);
            },
            writing =>
            {
                Assert.Equal("Writing", writing.Department);
                Assert.Equal(interstellar, Assert.Single(writing.Credits).Id);
            });
    }

    [Fact]
    public async Task Get_reuses_localized_title_and_poster_from_the_library_projection()
    {
        var actor = SeedPerson("1", "Some Actor", detailsFetchedAt: DateTimeOffset.UtcNow);
        var movie = SeedItem(MediaKind.Movie, "Untitled.2010.1080p", 2010); // raw item title
        SeedLocalizedTitle(movie, "Inception");
        SeedPoster(movie, "https://image.tmdb.org/p.jpg");
        SeedCredit(actor, movie, PersonRole.Cast, character: "Cobb", order: 0);

        var person = await Service(new StubMetadataProvider()).GetAsync("tmdb", "1", CancellationToken.None);

        var entry = Assert.Single(person!.Cast);
        Assert.Equal("Inception", entry.Title);                       // localized metadata title, not the raw item title
        Assert.Equal("https://image.tmdb.org/p.jpg", entry.PosterUrl); // poster from the shared library projection
    }

    [Fact]
    public async Task Get_lazily_fetches_and_caches_person_details_then_serves_from_cache()
    {
        SeedPerson("6193", "Leonardo DiCaprio", detailsFetchedAt: null); // never fetched
        var provider = new StubMetadataProvider
        {
            PersonResult = new PersonDetails(
                "Leonardo DiCaprio", "An American actor.", "/leo.jpg", "Acting", "1974-11-11", null, "Los Angeles, California, USA"),
        };

        var first = await Service(provider).GetAsync("tmdb", "6193", CancellationToken.None);

        Assert.Equal("An American actor.", first!.Biography);
        Assert.Equal("Acting", first.KnownForDepartment);
        Assert.Equal("1974-11-11", first.Birthday);
        Assert.Equal("Los Angeles, California, USA", first.PlaceOfBirth);
        Assert.Equal("https://image.tmdb.org/t/p/original/leo.jpg", first.ProfileUrl); // derived from the raw profile path
        Assert.Equal(1, provider.PersonFetches);

        // Persisted on the row, with the staleness marker stamped.
        await using (var fresh = Fresh())
        {
            var row = await fresh.Persons.SingleAsync(person => person.ProviderId == "6193");
            Assert.Equal("An American actor.", row.Biography);
            Assert.NotNull(row.DetailsFetchedAt);
        }

        // A second view is served from cache — no further provider call.
        var second = await Service(provider).GetAsync("tmdb", "6193", CancellationToken.None);
        Assert.Equal("An American actor.", second!.Biography);
        Assert.Equal(1, provider.PersonFetches);
    }

    [Fact]
    public async Task Get_refetches_details_once_the_staleness_window_passes()
    {
        SeedPerson("525", "Christopher Nolan", detailsFetchedAt: DateTimeOffset.UtcNow.AddDays(-40)); // stale
        var provider = new StubMetadataProvider
        {
            PersonResult = new PersonDetails("Christopher Nolan", "British-American filmmaker.", null, "Directing", "1970-07-30", null, "London, England"),
        };

        var person = await Service(provider).GetAsync("tmdb", "525", CancellationToken.None);

        Assert.Equal("British-American filmmaker.", person!.Biography);
        Assert.Equal(1, provider.PersonFetches); // stale → refetched
    }

    [Fact]
    public async Task Get_does_not_refetch_details_within_the_staleness_window()
    {
        SeedPerson("525", "Christopher Nolan", detailsFetchedAt: DateTimeOffset.UtcNow.AddDays(-1)); // fresh
        var provider = new StubMetadataProvider { PersonResult = new PersonDetails("x", "x", null, null, null, null, null) };

        await Service(provider).GetAsync("tmdb", "525", CancellationToken.None);

        Assert.Equal(0, provider.PersonFetches); // within TTL → not fetched
    }

    [Fact]
    public async Task Get_survives_a_provider_miss_and_retries_next_time()
    {
        SeedPerson("525", "Christopher Nolan", detailsFetchedAt: null);
        var provider = new StubMetadataProvider { PersonResult = null }; // miss

        var person = await Service(provider).GetAsync("tmdb", "525", CancellationToken.None);

        Assert.NotNull(person);
        Assert.Null(person!.Biography);            // nothing cached, but the page still renders
        Assert.Equal(1, provider.PersonFetches);

        // The miss did not stamp the staleness marker, so the next view retries.
        await using var fresh = Fresh();
        Assert.Null((await fresh.Persons.SingleAsync(p => p.ProviderId == "525")).DetailsFetchedAt);
    }

    private Guid SeedCatalog()
    {
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Movies",
            Type = CatalogType.Movie,
            Root = Path.Combine(Path.GetTempPath(), "ms-personread-" + Guid.NewGuid().ToString("N")),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.Catalogs.Add(catalog);
        _database.SaveChanges();
        return catalog.Id;
    }

    private Guid SeedItem(MediaKind kind, string title, int year, bool published = true)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = published ? Guid.NewGuid().ToString("N") : null,
            CatalogId = _catalogId,
            Kind = kind,
            Title = title,
            Year = year,
            AddedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.MediaItems.Add(item);
        _database.SaveChanges();
        return item.Id;
    }

    private Guid SeedPerson(string providerId, string name, DateTimeOffset? detailsFetchedAt)
    {
        var person = new Person
        {
            Id = Guid.NewGuid(),
            Provider = "tmdb",
            ProviderId = providerId,
            Name = name,
            DetailsFetchedAt = detailsFetchedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _database.Persons.Add(person);
        _database.SaveChanges();
        return person.Id;
    }

    private void SeedCredit(
        Guid personId, Guid mediaItemId, PersonRole role,
        string? character = null, string? job = null, string? department = null, int order = 0)
    {
        _database.MediaItemPersons.Add(new MediaItemPerson
        {
            Id = Guid.NewGuid(),
            PersonId = personId,
            MediaItemId = mediaItemId,
            Role = role,
            Character = character,
            Job = job,
            Department = department,
            Order = order,
        });
        _database.SaveChanges();
    }

    private void SeedLocalizedTitle(Guid mediaItemId, string title)
    {
        _database.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(),
            MediaItemId = mediaItemId,
            Provider = "tmdb",
            Language = "en-US",
            Title = title,
            FetchedAt = DateTimeOffset.UtcNow,
        });
        _database.SaveChanges();
    }

    private void SeedPoster(Guid mediaItemId, string url)
    {
        _database.ImageAssets.Add(new ImageAsset
        {
            Id = Guid.NewGuid(),
            MediaItemId = mediaItemId,
            ImageType = ImageType.Primary,
            Provider = "tmdb",
            RemotePath = url,
            Tag = "primary" + Guid.NewGuid().ToString("N")[..8],
            SortOrder = 0,
        });
        _database.SaveChanges();
    }

    private MediaServerDbContext Fresh() =>
        new(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// A metadata provider that only answers person-detail fetches (the rest are unused by the read service),
    /// recording how many times the fetch ran so the lazy-cache and staleness behaviour can be asserted.
    /// </summary>
    private sealed class StubMetadataProvider : IMetadataProvider
    {
        public string Key => "tmdb";

        public PersonDetails? PersonResult { get; set; }

        public int PersonFetches { get; private set; }

        public Task<PersonDetails?> FetchPersonAsync(ProviderRef reference, string language, CancellationToken cancellationToken)
        {
            PersonFetches++;
            return Task.FromResult(PersonResult);
        }

        public Task<IReadOnlyList<MetadataCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ProviderMetadata>> FetchAsync(ProviderRef reference, MediaKind kind, IReadOnlyList<string> languages, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteImage>> GetImagesAsync(ProviderRef reference, MediaKind kind, IReadOnlyList<string> languages, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
