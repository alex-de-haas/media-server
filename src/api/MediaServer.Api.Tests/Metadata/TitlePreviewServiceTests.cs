using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Metadata;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Metadata;

/// <summary>
/// The preview answers "what is this title" for something the instance may not hold. What matters here is
/// where the answer comes from — the library when it is held, the provider once and then a cache when it
/// is not — and that an outage or an unknown id degrades honestly.
/// </summary>
public sealed class TitlePreviewServiceTests : IDisposable
{
    private const string MovieRaw = """
    {
      "id": 27205,
      "title": "Inception",
      "original_title": "Inception",
      "overview": "A thief who steals corporate secrets.",
      "tagline": "Your mind is the scene of the crime.",
      "genres": [{ "id": 28, "name": "Action" }, { "id": 878, "name": "Science Fiction" }],
      "release_date": "2010-07-15",
      "runtime": 148,
      "status": "Released",
      "vote_average": 8.4,
      "vote_count": 34000,
      "homepage": "https://www.inceptionmovie.com",
      "poster_path": "/poster.jpg",
      "backdrop_path": "/backdrop.jpg",
      "belongs_to_collection": { "id": 1, "name": "Nolan Collection" },
      "credits": {
        "cast": [
          { "id": 6193, "name": "Leonardo DiCaprio", "character": "Cobb", "profile_path": "/leo.jpg" },
          { "id": 24045, "name": "Joseph Gordon-Levitt", "character": "Arthur", "profile_path": null }
        ],
        "crew": [{ "id": 525, "name": "Christopher Nolan", "job": "Director" }]
      },
      "external_ids": { "imdb_id": "tt1375666" },
      "videos": { "results": [{ "site": "YouTube", "type": "Trailer", "official": true, "key": "YoHD9XEInc0" }] },
      "release_dates": { "results": [{ "iso_3166_1": "US", "release_dates": [{ "certification": "PG-13" }] }] },
      "keywords": { "keywords": [{ "id": 1, "name": "dream" }] }
    }
    """;

    private const string SeriesRaw = """
    {
      "id": 1396,
      "name": "Breaking Bad",
      "overview": "A chemistry teacher turns to crime.",
      "genres": [{ "id": 18, "name": "Drama" }],
      "first_air_date": "2008-01-20",
      "episode_run_time": [47],
      "status": "Ended",
      "number_of_seasons": 5,
      "number_of_episodes": 62,
      "vote_count": 12000,
      "poster_path": "/bb.jpg",
      "created_by": [{ "id": 66633, "name": "Vince Gilligan" }],
      "networks": [{ "id": 174, "name": "AMC", "logo_path": "/amc.png" }],
      "credits": { "cast": [{ "id": 17419, "name": "Bryan Cranston", "character": "Walter White", "profile_path": "/bc.jpg" }] },
      "content_ratings": { "results": [{ "iso_3166_1": "US", "rating": "TV-MA" }] }
    }
    """;

    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-25T12:00:00Z"));
    private readonly MediaServerSettings _settings = new() { SupportedLanguages = ["en-US"] };
    private readonly Guid _catalogId = Guid.NewGuid();

    public TitlePreviewServiceTests()
    {
        _database = _db.Context;
        _database.Catalogs.Add(new Catalog
        {
            Id = _catalogId, Name = "Movies", Type = CatalogType.Movie, Root = "/movies",
            CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private TitlePreviewService Service(StubProvider provider) => new(
        _database,
        new LibraryReadService(_database, new UserDataService(_database, TimeProvider.System), _settings),
        provider,
        _settings,
        _time,
        NullLogger<TitlePreviewService>.Instance);

    [Fact]
    public async Task A_held_title_is_answered_from_the_library_without_asking_the_provider()
    {
        var itemId = SeedLibraryMovie("27205", title: "Inception", localizedTitle: "Начало");
        var provider = new StubProvider();

        var preview = await Service(provider).GetAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.True(preview!.InLibrary);
        Assert.Equal(itemId, preview.MediaItemId);
        // The localized title the detail page shows, not the raw item title — a preview must not contradict
        // the page it links to.
        Assert.Equal("Начало", preview.Title);
        Assert.Empty(provider.Fetches);
    }

    [Fact]
    public async Task An_unheld_title_is_projected_from_the_provider_payload()
    {
        var provider = new StubProvider { [("27205", MediaKind.Movie)] = MovieRaw };

        var preview = await Service(provider).GetAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.False(preview!.InLibrary);
        Assert.Null(preview.MediaItemId);
        Assert.Equal("Inception", preview.Title);
        Assert.Equal(2010, preview.Year);
        Assert.Equal("A thief who steals corporate secrets.", preview.Overview);
        Assert.Equal("Your mind is the scene of the crime.", preview.Tagline);
        Assert.Contains("Science Fiction", preview.Genres);
        Assert.Equal("PG-13", preview.OfficialRating);
        Assert.Equal(8.4, preview.CommunityRating);
        Assert.Equal(34000, preview.VoteCount);
        Assert.Equal(TimeSpan.FromMinutes(148).Ticks, preview.RuntimeTicks);
        Assert.Equal("Released", preview.Status);
        Assert.Equal("https://image.tmdb.org/t/p/original/poster.jpg", preview.PosterUrl);
        Assert.Equal("https://image.tmdb.org/t/p/original/backdrop.jpg", preview.BackdropUrl);
        Assert.Equal("Christopher Nolan", Assert.Single(preview.Directors));
        Assert.Equal("https://www.youtube.com/watch?v=YoHD9XEInc0", preview.TrailerUrl);
        Assert.Equal("tt1375666", preview.ImdbId);
        Assert.Equal("https://www.inceptionmovie.com", preview.Homepage);
    }

    [Fact]
    public async Task Cast_comes_from_the_payload_credits_because_an_unheld_title_has_no_local_people()
    {
        var provider = new StubProvider { [("27205", MediaKind.Movie)] = MovieRaw };

        var preview = await Service(provider).GetAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, CancellationToken.None);

        Assert.Equal(2, preview!.Cast.Count);
        var lead = preview.Cast[0];
        Assert.Equal("tmdb", lead.Provider);
        Assert.Equal("6193", lead.ProviderId);
        Assert.Equal("Leonardo DiCaprio", lead.Name);
        Assert.Equal("Cobb", lead.Character);
        Assert.Equal("https://image.tmdb.org/t/p/original/leo.jpg", lead.ProfileUrl);
        Assert.Null(preview.Cast[1].ProfileUrl); // no profile_path: a missing photo, not a missing person
    }

    [Fact]
    public async Task A_series_carries_its_creators_status_and_totals()
    {
        var provider = new StubProvider { [("1396", MediaKind.Series)] = SeriesRaw };

        var preview = await Service(provider).GetAsync(new ProviderRef("tmdb", "1396"), MediaKind.Series, CancellationToken.None);

        Assert.Equal("Series", preview!.Kind);
        Assert.Equal("Breaking Bad", preview.Title);
        Assert.Equal("Vince Gilligan", Assert.Single(preview.Creators));
        Assert.Equal("Ended", preview.Status);
        Assert.Equal(5, preview.SeasonCount);
        Assert.Equal(62, preview.EpisodeCount);
        Assert.Equal("TV-MA", preview.OfficialRating);
        Assert.Equal(TimeSpan.FromMinutes(47).Ticks, preview.RuntimeTicks);
    }

    [Fact]
    public async Task A_second_look_at_the_same_title_costs_no_request()
    {
        var provider = new StubProvider { [("27205", MediaKind.Movie)] = MovieRaw };
        var service = Service(provider);

        await service.GetAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, CancellationToken.None);
        var again = await service.GetAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, CancellationToken.None);

        Assert.Equal("Inception", again!.Title);
        Assert.Single(provider.Fetches);
        Assert.Single(_database.TmdbTitleDetailCache);
    }

    [Fact]
    public async Task A_stale_row_is_refreshed_in_place_rather_than_piling_up()
    {
        var provider = new StubProvider { [("27205", MediaKind.Movie)] = MovieRaw };
        var service = Service(provider);
        await service.GetAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, CancellationToken.None);

        _time.Advance(TitlePreviewService.CacheLifetime + TimeSpan.FromHours(1));
        await service.GetAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, CancellationToken.None);

        Assert.Equal(2, provider.Fetches.Count);
        var row = Assert.Single(_database.TmdbTitleDetailCache);
        Assert.Equal(_time.GetUtcNow(), row.FetchedAt);
    }

    [Fact]
    public async Task An_outage_serves_the_stale_payload_rather_than_nothing()
    {
        var provider = new StubProvider { [("27205", MediaKind.Movie)] = MovieRaw };
        var service = Service(provider);
        await service.GetAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, CancellationToken.None);

        _time.Advance(TitlePreviewService.CacheLifetime + TimeSpan.FromDays(30));
        provider.Throws = true;
        var preview = await service.GetAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, CancellationToken.None);

        Assert.Equal("Inception", preview!.Title);
    }

    [Fact]
    public async Task A_title_the_provider_does_not_know_has_no_preview()
    {
        var provider = new StubProvider();

        var preview = await Service(provider).GetAsync(new ProviderRef("tmdb", "999999"), MediaKind.Movie, CancellationToken.None);

        Assert.Null(preview);
        Assert.Empty(_database.TmdbTitleDetailCache);
    }

    [Fact]
    public async Task The_kind_is_part_of_the_identity_because_movie_and_tv_ids_collide()
    {
        // TMDb id 95480 is a film and a series at once; each must be asked for and cached separately.
        var provider = new StubProvider
        {
            [("95480", MediaKind.Movie)] = MovieRaw,
            [("95480", MediaKind.Series)] = SeriesRaw,
        };
        var service = Service(provider);

        var movie = await service.GetAsync(new ProviderRef("tmdb", "95480"), MediaKind.Movie, CancellationToken.None);
        var series = await service.GetAsync(new ProviderRef("tmdb", "95480"), MediaKind.Series, CancellationToken.None);

        Assert.Equal("Inception", movie!.Title);
        Assert.Equal("Breaking Bad", series!.Title);
        Assert.Equal(2, _database.TmdbTitleDetailCache.Count());
    }

    [Fact]
    public async Task An_unpublished_item_is_previewed_as_a_discovery()
    {
        // Identified but not yet published: nothing to link to, so the preview is the provider's.
        SeedLibraryMovie("27205", title: "Inception", localizedTitle: "Начало", published: false);
        var provider = new StubProvider { [("27205", MediaKind.Movie)] = MovieRaw };

        var preview = await Service(provider).GetAsync(new ProviderRef("tmdb", "27205"), MediaKind.Movie, CancellationToken.None);

        Assert.False(preview!.InLibrary);
        Assert.Null(preview.MediaItemId);
        Assert.Single(provider.Fetches);
    }

    private Guid SeedLibraryMovie(string tmdbId, string title, string localizedTitle, bool published = true)
    {
        var id = Guid.NewGuid();
        _database.MediaItems.Add(new MediaItem
        {
            Id = id,
            PublicId = published ? id.ToString("N") : null,
            CatalogId = _catalogId,
            Kind = MediaKind.Movie,
            Title = title,
            Year = 2010,
            IdentityProvider = "tmdb",
            IdentityProviderId = tmdbId,
            AddedAt = _time.GetUtcNow(),
            UpdatedAt = _time.GetUtcNow(),
        });
        _database.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(),
            MediaItemId = id,
            Provider = "tmdb",
            Language = "en-US",
            Title = localizedTitle,
            Overview = "A thief who steals corporate secrets.",
            Raw = MovieRaw,
            FetchedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
        return id;
    }

    /// <summary>A provider that answers from a canned payload table and records what it was asked for.</summary>
    private sealed class StubProvider : IMetadataProvider
    {
        private readonly Dictionary<(string Id, MediaKind Kind), string> _payloads = [];

        public string Key => "tmdb";

        public bool Throws { get; set; }

        public List<(string Id, MediaKind Kind, string Language)> Fetches { get; } = [];

        public string this[(string Id, MediaKind Kind) key] { set => _payloads[key] = value; }

        public Task<IReadOnlyList<ProviderMetadata>> FetchAsync(
            ProviderRef reference, MediaKind kind, IReadOnlyList<string> languages, CancellationToken cancellationToken)
        {
            if (Throws)
            {
                throw new HttpRequestException("TMDb is unreachable.");
            }

            var language = languages[0];
            Fetches.Add((reference.Id, kind, language));
            if (!_payloads.TryGetValue((reference.Id, kind), out var raw))
            {
                return Task.FromResult<IReadOnlyList<ProviderMetadata>>([]);
            }

            using var document = System.Text.Json.JsonDocument.Parse(raw);
            return Task.FromResult<IReadOnlyList<ProviderMetadata>>(
                [TmdbPayload.MapDetails(reference, language, kind, document.RootElement)]);
        }

        public Task<IReadOnlyList<MetadataCandidate>> SearchAsync(MediaQuery query, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MetadataCandidate>>([]);

        public Task<IReadOnlyList<RemoteImage>> GetImagesAsync(
            ProviderRef reference, MediaKind kind, IReadOnlyList<string> languages, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteImage>>([]);

        public Task<PersonDetails?> FetchPersonAsync(ProviderRef reference, string language, CancellationToken cancellationToken) =>
            Task.FromResult<PersonDetails?>(null);
    }
}
