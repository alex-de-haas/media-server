using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Hosty;
using MediaServer.Api.Jellyfin;
using MediaServer.Api.Library;

namespace MediaServer.Api.Tests.Jellyfin;

/// <summary>
/// The people surface: credits on the item detail, person items, and browsing by person. The data behind
/// it (<see cref="Person"/> + <see cref="MediaItemPerson"/>) is populated by the metadata pipeline; these
/// tests only cover how the Jellyfin layer projects it.
/// </summary>
public sealed class JellyfinPeopleTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerSettings _settings = new() { SupportedLanguages = ["en-US"] };
    private readonly JellyfinLibraryService _library;

    private Guid _catalogId;
    private string _moviePublicId = string.Empty;
    private string _sequelPublicId = string.Empty;
    private string _crowdedPublicId = string.Empty;
    private string _uncreditedPublicId = string.Empty;
    private Person _lead = null!;
    private Person _writer = null!;
    private Person _stranger = null!;

    public JellyfinPeopleTests()
    {
        var hosty = new HostyOptions
        {
            AppId = "com.haas.media-server",
            CoreOrigin = "http://localhost:3001",
            AppDataDir = Path.GetTempPath(),
        };
        var server = new JellyfinServerContext(hosty, _settings);
        _library = new JellyfinLibraryService(
            _db.Create(), new JellyfinItemMapper(server), new JellyfinCatalogArtwork(_db.Create()),
            new JellyfinCollectionService(_db.Create()), new JellyfinPersonService(_db.Create()), new EmptyShelf(),
            new UserDataService(_db.Create(), TimeProvider.System), _settings);
        Seed();
    }

    [Fact]
    public async Task Item_detail_lists_cast_in_billing_order_then_crew()
    {
        var movie = await _library.GetItemAsync(_moviePublicId, includeMediaSources: false, appUserId: null, CancellationToken.None);

        Assert.NotNull(movie!.People);
        var people = movie.People!;

        // Cast first, in billing order, each carrying the portrayed character.
        Assert.Equal("Leonardo DiCaprio", people[0].Name);
        Assert.Equal("Actor", people[0].Type);
        Assert.Equal("Dom Cobb", people[0].Role);
        Assert.Equal("Elliot Page", people[1].Name);
        Assert.Equal("Ariadne", people[1].Role);

        // Then the crew, director first, with the provider's own job as the role.
        var crew = people.Where(person => person.Type != "Actor").ToList();
        Assert.Equal(["Director", "Writer", "Producer"], crew.Select(person => person.Type));
        Assert.Equal(["Director", "Screenplay", "Producer"], crew.Select(person => person.Role));
    }

    [Fact]
    public async Task Crew_jobs_outside_the_mapped_set_are_dropped()
    {
        var movie = await _library.GetItemAsync(_moviePublicId, includeMediaSources: false, appUserId: null, CancellationToken.None);

        // The animator and the stunt performer are seeded on the same movie and must not reach the client.
        Assert.DoesNotContain(movie!.People!, person => person.Name is "Ann Animator" or "Stu Stunts");
    }

    [Fact]
    public async Task A_person_is_listed_once_per_kind()
    {
        var movie = await _library.GetItemAsync(_moviePublicId, includeMediaSources: false, appUserId: null, CancellationToken.None);
        var people = movie!.People!;

        // Two writing credits (Screenplay + Story) collapse to the first.
        var writing = people.Where(person => person.Name == _writer.Name).ToList();
        Assert.Equal("Screenplay", Assert.Single(writing).Role);

        // But acting and directing are different kinds, so the same person appears under each.
        var lead = people.Where(person => person.Name == _lead.Name).ToList();
        Assert.Equal(2, lead.Count);
        Assert.Equal(["Actor", "Director"], lead.Select(person => person.Type));
        Assert.All(lead, person => Assert.Equal(JellyfinIds.Person("tmdb", _lead.ProviderId), person.Id));
    }

    [Fact]
    public async Task Cast_and_crew_are_capped()
    {
        var movie = await _library.GetItemAsync(_crowdedPublicId, includeMediaSources: false, appUserId: null, CancellationToken.None);
        var people = movie!.People!;

        Assert.Equal(JellyfinItemMapper.MaxCastCredits, people.Count(person => person.Type == "Actor"));
        Assert.Equal(JellyfinItemMapper.MaxCrewCredits, people.Count(person => person.Type != "Actor"));

        // The cap keeps the top of the billing order, not an arbitrary slice.
        Assert.Equal("Extra 00", people[0].Name);
        Assert.Equal($"Extra {JellyfinItemMapper.MaxCastCredits - 1:00}", people[JellyfinItemMapper.MaxCastCredits - 1].Name);
    }

    [Fact]
    public async Task An_item_without_credits_carries_no_people()
    {
        var movie = await _library.GetItemAsync(_uncreditedPublicId, includeMediaSources: false, appUserId: null, CancellationToken.None);

        Assert.Null(movie!.People);
    }

    [Fact]
    public async Task List_responses_carry_no_people()
    {
        // A credit query per row is what the detail path pays; a library listing must not.
        var result = await _library.ListItemsAsync(
            new JellyfinItemsQuery { ParentId = JellyfinIds.Catalog(_catalogId) }, appUserId: null, CancellationToken.None);

        Assert.NotEmpty(result.Items);
        Assert.All(result.Items, item => Assert.Null(item.People));
    }

    [Fact]
    public void Person_ids_are_stable_and_scoped_to_the_provider_identity()
    {
        var id = JellyfinIds.Person("tmdb", "6193");

        Assert.Equal(id, JellyfinIds.Person("tmdb", "6193"));
        Assert.Equal(id, JellyfinIds.Person("TMDB", "6193")); // Provider casing is not part of the identity.
        Assert.NotEqual(id, JellyfinIds.Person("tmdb", "6194"));
        Assert.NotEqual(id, JellyfinIds.Person("imdb", "6193"));
        Assert.Equal(32, id.Length);
        Assert.True(id.All(character => char.IsAsciiDigit(character) || (character is >= 'a' and <= 'f')));
    }

    [Fact]
    public async Task A_person_with_a_photo_advertises_an_image_tag_that_tracks_the_photo()
    {
        var movie = await _library.GetItemAsync(_moviePublicId, includeMediaSources: false, appUserId: null, CancellationToken.None);
        var lead = Assert.Single(movie!.People!, person => person.Type == "Actor" && person.Name == _lead.Name);

        Assert.Equal(JellyfinPersonService.PrimaryTag(_lead), lead.PrimaryImageTag);
        Assert.NotNull(lead.PrimaryImageTag);

        // A replaced photo yields a new tag, so a client's cached image is not served forever.
        var replaced = new Person { Id = _lead.Id, Provider = "tmdb", ProviderId = _lead.ProviderId, Name = _lead.Name, ProfileUrl = "https://image.tmdb.org/t/p/original/new.jpg" };
        Assert.NotEqual(JellyfinPersonService.PrimaryTag(_lead), JellyfinPersonService.PrimaryTag(replaced));

        // And a person the provider has no photo for advertises none rather than a broken tag.
        Assert.Null(JellyfinPersonService.PrimaryTag(_stranger));
    }

    [Fact]
    public async Task A_person_id_serves_the_profile_photo()
    {
        var appData = Path.Combine(Path.GetTempPath(), "ms-people-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(appData, "images"));
        try
        {
            // Pre-cache the bytes under the name the service writes, so serving needs no HTTP.
            var tag = JellyfinPersonService.PrimaryTag(_lead)!;
            File.WriteAllBytes(Path.Combine(appData, "images", $"person-{_lead.Id:N}-{tag}.jpg"), [7, 7, 7]);

            var images = ImageService(appData);
            var personId = JellyfinIds.Person("tmdb", _lead.ProviderId);

            var payload = await images.GetImageAsync(personId, ImageType.Primary, tag: null, index: 0, CancellationToken.None);

            Assert.NotNull(payload);
            Assert.Equal(tag, payload!.Tag);
            Assert.Equal([7, 7, 7], payload.Content);

            // A person has exactly one image: a backdrop request must not be answered with the portrait.
            Assert.Null(await images.GetImageAsync(personId, ImageType.Backdrop, tag: null, index: 0, CancellationToken.None));

            // And a person the provider has no photo for serves nothing at all.
            Assert.Null(await images.GetImageAsync(
                JellyfinIds.Person("tmdb", _stranger.ProviderId), ImageType.Primary, tag: null, index: 0, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(appData, recursive: true);
        }
    }

    [Fact]
    public async Task Persons_lists_only_people_credited_on_a_published_item()
    {
        var result = await _library.ListPeopleAsync(searchTerm: null, startIndex: null, limit: null, CancellationToken.None);

        Assert.All(result.Items, person => Assert.Equal("Person", person.Type));
        Assert.Contains(result.Items, person => person.Name == _lead.Name);
        // Seeded with no credits at all: it exists as a row but is not part of this library's people.
        Assert.DoesNotContain(result.Items, person => person.Name == _stranger.Name);
        Assert.Equal(result.Items.Count, result.TotalRecordCount);
    }

    [Fact]
    public async Task Persons_honors_the_search_term_and_paging()
    {
        var matches = await _library.ListPeopleAsync("Extra", startIndex: null, limit: null, CancellationToken.None);
        Assert.Equal(40, matches.TotalRecordCount);
        Assert.All(matches.Items, person => Assert.StartsWith("Extra ", person.Name));

        var page = await _library.ListPeopleAsync("Extra", startIndex: 5, limit: 2, CancellationToken.None);
        Assert.Equal(40, page.TotalRecordCount); // The total is the match count, not the page size.
        Assert.Equal(5, page.StartIndex);
        Assert.Equal(["Extra 05", "Extra 06"], page.Items.Select(person => person.Name));
    }

    [Fact]
    public async Task A_person_id_resolves_to_a_person_item()
    {
        var personId = JellyfinIds.Person("tmdb", _lead.ProviderId);

        var person = await _library.GetItemAsync(personId, includeMediaSources: false, appUserId: null, CancellationToken.None);

        Assert.NotNull(person);
        Assert.Equal("Person", person!.Type);
        Assert.Equal(_lead.Name, person.Name);
        Assert.Equal(personId, person.Id);
        Assert.Equal("Virtual", person.LocationType);
    }

    [Fact]
    public async Task Person_ids_narrow_a_listing_to_that_persons_titles()
    {
        var query = new JellyfinItemsQuery
        {
            PersonIds = [JellyfinIds.Person("tmdb", _writer.ProviderId)],
            Recursive = true,
        };

        var result = await _library.ListItemsAsync(query, appUserId: null, CancellationToken.None);

        // The writer is credited on the first movie only, not on its sequel.
        var item = Assert.Single(result.Items);
        Assert.Equal(_moviePublicId, item.Id);

        // The lead acted in both, so both come back.
        var forLead = await _library.ListItemsAsync(
            query with { PersonIds = [JellyfinIds.Person("tmdb", _lead.ProviderId)] }, appUserId: null, CancellationToken.None);
        Assert.Equal(new[] { _moviePublicId, _sequelPublicId }.Order(), forLead.Items.Select(entry => entry.Id).Order());
    }

    [Fact]
    public async Task An_unresolvable_person_id_yields_nothing_rather_than_the_whole_library()
    {
        var query = new JellyfinItemsQuery { PersonIds = ["ffffffffffffffffffffffffffffffff"], Recursive = true };

        var result = await _library.ListItemsAsync(query, appUserId: null, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalRecordCount);
    }

    private JellyfinImageService ImageService(string appDataDir) => new(
        _db.Create(), new JellyfinCatalogArtwork(_db.Create()), new JellyfinCollectionService(_db.Create()),
        new JellyfinPersonService(_db.Create()), new StubHttpClientFactory(),
        new HostyOptions { AppId = "com.haas.media-server", CoreOrigin = "http://localhost:3001", AppDataDir = appDataDir });

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        // Never invoked: the photo test serves from a pre-written cache file.
        public HttpClient CreateClient(string name) => new();
    }

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
        using var context = _db.Create();

        var catalog = new Catalog { Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/movies", CreatedAt = now, UpdatedAt = now };
        _catalogId = catalog.Id;
        context.Catalogs.Add(catalog);

        MediaItem Movie(string title)
        {
            var movie = new MediaItem
            {
                Id = Guid.NewGuid(),
                PublicId = Guid.NewGuid().ToString("N"),
                CatalogId = catalog.Id,
                Kind = MediaKind.Movie,
                Title = title,
                Year = 2010,
                AddedAt = now,
                UpdatedAt = now,
            };
            context.MediaItems.Add(movie);
            return movie;
        }

        var movie = Movie("Inception");
        var sequel = Movie("Inception II");
        var crowded = Movie("Cast of Thousands");
        var uncredited = Movie("Unidentified");
        _moviePublicId = movie.PublicId!;
        _sequelPublicId = sequel.PublicId!;
        _crowdedPublicId = crowded.PublicId!;
        _uncreditedPublicId = uncredited.PublicId!;

        Person NewPerson(string providerId, string name, string? profileUrl = null)
        {
            var person = new Person
            {
                Id = Guid.NewGuid(),
                Provider = "tmdb",
                ProviderId = providerId,
                Name = name,
                ProfileUrl = profileUrl,
                UpdatedAt = now,
            };
            context.Persons.Add(person);
            return person;
        }

        void Cast(MediaItem item, Person person, string character, int order) =>
            context.MediaItemPersons.Add(new MediaItemPerson
            {
                Id = Guid.NewGuid(),
                MediaItemId = item.Id,
                PersonId = person.Id,
                Role = PersonRole.Cast,
                Character = character,
                Order = order,
            });

        void Crew(MediaItem item, Person person, string job, string department, int order) =>
            context.MediaItemPersons.Add(new MediaItemPerson
            {
                Id = Guid.NewGuid(),
                MediaItemId = item.Id,
                PersonId = person.Id,
                Role = PersonRole.Crew,
                Job = job,
                Department = department,
                Order = order,
            });

        _lead = NewPerson("6193", "Leonardo DiCaprio", "https://image.tmdb.org/t/p/original/lead.jpg");
        var second = NewPerson("27578", "Elliot Page", "https://image.tmdb.org/t/p/original/second.jpg");
        _writer = NewPerson("525", "Christopher Nolan");
        var producer = NewPerson("10850", "Emma Thomas");
        var animator = NewPerson("99001", "Ann Animator");
        var stunts = NewPerson("99002", "Stu Stunts");
        _stranger = NewPerson("99003", "Never Credited");

        // Deliberately seeded out of order: the projection sorts, the storage order must not matter.
        Cast(movie, second, "Ariadne", 1);
        Cast(movie, _lead, "Dom Cobb", 0);

        // The lead also directed, so the same person holds a cast and a crew credit on one item.
        Crew(movie, _lead, "Director", "Directing", 0);
        // Two writing credits for one person collapse to the first.
        Crew(movie, _writer, "Screenplay", "Writing", 0);
        Crew(movie, _writer, "Story", "Writing", 1);
        Crew(movie, producer, "Producer", "Production", 0);
        // Noise that dominates a real TMDb crew list.
        Crew(movie, animator, "Animation", "Visual Effects", 0);
        Crew(movie, stunts, "Stunts", "Crew", 1);

        // The sequel shares the lead but not the writer, so a person filter can tell them apart.
        Cast(sequel, _lead, "Dom Cobb", 0);

        // More credits than either cap emits.
        for (var index = 0; index < 40; index++)
        {
            var person = NewPerson($"5{index:0000}", $"Extra {index:00}");
            Cast(crowded, person, $"Face {index:00}", index);
            Crew(crowded, person, "Producer", "Production", index);
        }

        context.SaveChanges();
    }

    public void Dispose() => _db.Dispose();
}
