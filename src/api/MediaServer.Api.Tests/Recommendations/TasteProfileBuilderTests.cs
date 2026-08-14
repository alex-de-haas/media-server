using MediaServer.Api.Data;
using MediaServer.Api.Recommendations.Profile;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The taste profile: what a viewer's own history says about them, damped against what the library
/// says about everyone.
/// </summary>
public sealed class TasteProfileBuilderTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-08-14T12:00:00Z"));
    private readonly int _userId;
    private readonly int _otherUserId;
    private readonly Guid _catalogId = Guid.NewGuid();

    public TasteProfileBuilderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = new MediaServerDbContext(
            new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);
        _database.Database.Migrate();

        var user = NewUser("host-1", "alex@example.com");
        var other = NewUser("host-2", "sam@example.com");
        _database.AppUsers.AddRange(user, other);
        _database.SaveChanges();
        _userId = user.Id;
        _otherUserId = other.Id;

        _database.Catalogs.Add(new Catalog
        {
            Id = _catalogId, Name = "Library", Type = CatalogType.Movie, Root = "/m",
            CreatedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
    }

    [Fact]
    public async Task WithNoHistoryTheProfileIsEmpty()
    {
        AddMovie("Unwatched", genres: ["Drama"]);

        Assert.True((await Build()).IsEmpty);
    }

    [Fact]
    public async Task AWatchedTitlesFacetsBecomeTheProfile()
    {
        var movie = AddMovie("Arrival", genres: ["Science Fiction"], year: 2016, language: "en");
        AddPlay(movie.Id);

        var profile = await Build();

        Assert.True(profile.Liked(FacetFamily.Genre, "science fiction") > 0);
        Assert.True(profile.Liked(FacetFamily.Decade, "2010") > 0);
        Assert.True(profile.Liked(FacetFamily.Language, "en") > 0);
    }

    [Fact]
    public async Task AGenreTheWholeLibraryCarriesIsDampedBelowARareOne()
    {
        // Without IDF every profile reports that the viewer loves Drama — true, useless, and the same
        // for everyone. What distinguishes this viewer is the genre the library almost never holds.
        for (var index = 0; index < 20; index++)
        {
            AddMovie($"Drama {index}", genres: ["Drama"]);
        }

        var both = AddMovie("Both", genres: ["Drama", "Film Noir"]);
        AddPlay(both.Id);

        var profile = await Build();

        Assert.True(profile.Liked(FacetFamily.Genre, "film noir") > profile.Liked(FacetFamily.Genre, "drama"));
    }

    [Fact]
    public async Task ATitleWithManyKeywordsCannotOutvoteOneWithFew()
    {
        // Families are normalized separately for exactly this: pooled into one vector, the sixteen
        // keywords TMDb allows would drown three genres every time.
        var wordy = AddMovie("Wordy", genres: ["Drama"], keywords: [.. Enumerable.Range(0, 16).Select(index => $"kw{index}")]);
        AddPlay(wordy.Id);

        var profile = await Build();
        var genre = profile.Liked(FacetFamily.Genre, "drama");
        var keyword = profile.Liked(FacetFamily.Keyword, "kw0");

        // Each family is unit length on its own, so the lone genre carries the whole genre vector.
        Assert.Equal(1.0, genre, precision: 6);
        Assert.True(keyword < genre);
    }

    [Fact]
    public async Task ADirectorOutweighsTheEleventhBilledActor()
    {
        var movie = AddMovie("Dune", genres: ["Science Fiction"]);
        var director = AddPerson("Denis Villeneuve");
        var extra = AddPerson("Eleventh Billing");
        AddCredit(movie.Id, director, PersonRole.Crew, job: "Director", department: "Directing", order: 0);
        AddCredit(movie.Id, extra, PersonRole.Cast, job: null, department: null, order: 10);
        AddPlay(movie.Id);

        var profile = await Build();

        Assert.True(
            profile.Liked(FacetFamily.Person, director.ToString("N")) >
            profile.Liked(FacetFamily.Person, extra.ToString("N")));
    }

    [Fact]
    public async Task AFilmLovedCountsForMoreThanOneMerelyWatched()
    {
        var loved = AddMovie("Loved", genres: ["Heist"]);
        var seen = AddMovie("Seen", genres: ["Western"]);
        AddPlay(loved.Id);
        AddPlay(seen.Id);
        Rate(loved.Id, 5);

        var profile = await Build();

        Assert.True(profile.Liked(FacetFamily.Genre, "heist") > profile.Liked(FacetFamily.Genre, "western"));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ALowRatedTitleFeedsTheNegativeProfileAndNotThePositiveOne(int stars)
    {
        var disliked = AddMovie("Disliked", genres: ["Musical"]);
        AddPlay(disliked.Id);
        Rate(disliked.Id, stars);

        var profile = await Build();

        Assert.True(profile.Disliked(FacetFamily.Genre, "musical") > 0);
        Assert.Equal(0, profile.Liked(FacetFamily.Genre, "musical"));
    }

    [Fact]
    public async Task AOneStarWeighsMoreThanAHide()
    {
        // A hide judges a title never watched; one star is a verdict after watching, which is why it
        // needs no "enough of them exist" threshold to be trusted.
        var rated = AddMovie("Rated one", genres: ["Musical"], tmdbId: "10");
        var hidden = AddMovie("Hidden", genres: ["Slasher"], tmdbId: "20");
        AddPlay(rated.Id);
        Rate(rated.Id, 1);
        Hide("20");

        var profile = await Build();

        Assert.True(profile.Disliked(FacetFamily.Genre, "musical") > profile.Disliked(FacetFamily.Genre, "slasher"));
    }

    [Fact]
    public async Task ATitleStartedAndAbandonedIsANegative()
    {
        var abandoned = AddMovie("Abandoned", genres: ["Musical"], runtimeTicks: 1000);
        SetPosition(abandoned.Id, 50); // five per cent in and stopped

        var profile = await Build();

        Assert.True(profile.Disliked(FacetFamily.Genre, "musical") > 0);
    }

    [Fact]
    public async Task ATitleAbandonedOnceAndFinishedLaterIsNotARejection()
    {
        var movie = AddMovie("Second attempt", genres: ["Musical"], runtimeTicks: 1000);
        SetPosition(movie.Id, 50);
        AddPlay(movie.Id);

        var profile = await Build();

        Assert.Equal(0, profile.Disliked(FacetFamily.Genre, "musical"));
        Assert.True(profile.Liked(FacetFamily.Genre, "musical") > 0);
    }

    [Fact]
    public async Task ATrackedTitleCountsAsIntentButLessThanAWatch()
    {
        var watched = AddMovie("Watched", genres: ["Western"]);
        var tracked = AddMovie("Tracked", genres: ["Heist"]);
        AddPlay(watched.Id);
        Track(tracked);

        var profile = await Build();

        Assert.True(profile.Liked(FacetFamily.Genre, "heist") > 0);
        Assert.True(profile.Liked(FacetFamily.Genre, "western") > profile.Liked(FacetFamily.Genre, "heist"));
    }

    [Fact]
    public async Task AnotherUsersHistoryNeverReachesThisProfile()
    {
        var theirs = AddMovie("Theirs", genres: ["Musical"]);
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _otherUserId, MediaItemId = theirs.Id,
            CreatedAt = _time.GetUtcNow(), WatchedAt = _time.GetUtcNow(),
            Origin = PlaybackHistoryOrigin.LocalPlayback,
        });
        _database.SaveChanges();

        Assert.True((await Build()).IsEmpty);
    }

    [Fact]
    public async Task AffinityRanksACandidateThatLooksLikeTheProfileFirst()
    {
        var watched = AddMovie("Watched", genres: ["Heist"], year: 2016);
        AddPlay(watched.Id);
        var profile = await Build();

        var alike = new TitleFacets([new WeightedFacet(FacetFamily.Genre, "heist", 1)]);
        var unlike = new TitleFacets([new WeightedFacet(FacetFamily.Genre, "musical", 1)]);

        Assert.True(profile.Affinity(alike) > profile.Affinity(unlike));
        Assert.Equal(0, profile.Affinity(TitleFacets.Empty));
    }

    [Fact]
    public async Task ACandidateIsJudgedOnTheFamiliesItHasRatherThanPunishedForOnesItLacks()
    {
        // The same rule the scorer applies to a featureless candidate: absent evidence must never read
        // as evidence against, or every title whose keywords were never fetched sinks.
        var watched = AddMovie("Watched", genres: ["Heist"], keywords: ["caper"], year: 2016);
        AddPlay(watched.Id);
        var profile = await Build();

        var genreOnly = new TitleFacets([new WeightedFacet(FacetFamily.Genre, "heist", 1)]);
        var withKeyword = new TitleFacets([
            new WeightedFacet(FacetFamily.Genre, "heist", 1),
            new WeightedFacet(FacetFamily.Keyword, "caper", 1),
        ]);

        Assert.True(profile.Affinity(genreOnly) > 0);
        Assert.Equal(profile.Affinity(genreOnly), profile.Affinity(withKeyword), precision: 6);
    }

    private Task<TasteProfile> Build() =>
        new TasteProfileBuilder(_database, new TitleFacetReader(_database), new LibraryFacetIndexCache(), _time)
            .BuildAsync(_userId, CancellationToken.None);

    private AppUser NewUser(string hostUserId, string email) => new()
    {
        HostUserId = hostUserId, Email = email, DisplayName = email, Role = AppUserRole.User,
        CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
    };

    private MediaItem AddMovie(
        string title,
        IReadOnlyList<string>? genres = null,
        IReadOnlyList<string>? keywords = null,
        int? year = null,
        string? language = null,
        string? tmdbId = null,
        long? runtimeTicks = null)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = _catalogId, Kind = MediaKind.Movie, Title = title,
            Year = year, OriginalLanguage = language,
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        if (tmdbId is not null)
        {
            item.IdentityProvider = "tmdb";
            item.IdentityProviderId = tmdbId;
        }

        _database.MediaItems.Add(item);

        if (genres is not null || keywords is not null)
        {
            _database.MetadataRecords.Add(new MetadataRecord
            {
                Id = Guid.NewGuid(), MediaItemId = item.Id, Provider = "tmdb", Language = "en-US",
                Genres = [.. genres ?? []], FetchedAt = _time.GetUtcNow(),
                Raw = keywords is null ? null : RawWithKeywords(keywords),
            });
        }

        if (runtimeTicks is { } ticks)
        {
            _database.MediaSources.Add(new MediaSource
            {
                Id = Guid.NewGuid(), MediaItemId = item.Id, Container = "matroska",
                Path = $"{title}.mkv", SizeBytes = 1, DurationTicks = ticks, CreatedAt = _time.GetUtcNow(),
            });
        }

        _database.SaveChanges();
        return item;
    }

    /// <summary>A TMDb detail payload carrying only what the keyword parser reads.</summary>
    private static string RawWithKeywords(IReadOnlyList<string> keywords)
    {
        var entries = string.Join(',', keywords.Select(keyword => $$"""{"id":1,"name":"{{keyword}}"}"""));
        return """{"keywords":{"keywords":[""" + entries + "]}}";
    }

    private Guid AddPerson(string name)
    {
        var person = new Person
        {
            Id = Guid.NewGuid(), Name = name, Provider = "tmdb", ProviderId = Guid.NewGuid().ToString("N")[..8],
        };
        _database.Persons.Add(person);
        _database.SaveChanges();
        return person.Id;
    }

    private void AddCredit(Guid itemId, Guid personId, PersonRole role, string? job, string? department, int order)
    {
        _database.MediaItemPersons.Add(new MediaItemPerson
        {
            Id = Guid.NewGuid(), MediaItemId = itemId, PersonId = personId, Role = role,
            Job = job, Department = department, Order = order,
        });
        _database.SaveChanges();
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

    private void Rate(Guid itemId, int stars) => UpdateUserData(itemId, row => row.Rating = stars);

    private void SetPosition(Guid itemId, long ticks) =>
        UpdateUserData(itemId, row => row.PlaybackPositionTicks = ticks);

    private void UpdateUserData(Guid itemId, Action<UserItemData> apply)
    {
        var row = _database.UserItemData.FirstOrDefault(
            data => data.AppUserId == _userId && data.MediaItemId == itemId);
        if (row is null)
        {
            row = new UserItemData { Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = itemId };
            _database.UserItemData.Add(row);
        }

        apply(row);
        _database.SaveChanges();
    }

    private void Hide(string tmdbId)
    {
        _database.RecommendationHides.Add(new RecommendationHide
        {
            Id = Guid.NewGuid(), AppUserId = _userId, Kind = MediaServer.Api.Recommendations.RecommendationKind.Movie,
            TmdbId = tmdbId, CreatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
    }

    private void Track(MediaItem item)
    {
        var tracked = new TrackedTitle
        {
            Id = Guid.NewGuid(), Kind = MediaKind.Movie, IdentityProvider = "tmdb",
            IdentityProviderId = $"t{item.Id:N}"[..12], MediaItemId = item.Id, Title = item.Title,
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
