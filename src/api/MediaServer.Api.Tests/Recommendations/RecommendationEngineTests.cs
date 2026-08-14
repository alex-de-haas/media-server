using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Recommendations.Generation;
using MediaServer.Api.Recommendations.Profile;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The engine end to end: what several seeds agreeing means, how the curve and the dials shape the
/// order, what a card says about itself, and what must never appear in the result.
/// </summary>
public sealed class RecommendationEngineTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-25T12:00:00Z"));
    private readonly StubSource _tmdb = new();
    private readonly int _userId;
    private readonly Guid _catalogId = Guid.NewGuid();

    public RecommendationEngineTests()
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

    /// <summary>Answers from a fixed table instead of the network, and records what was asked.</summary>
    private sealed class StubSource : ITmdbRecommendationSource
    {
        public Dictionary<string, List<TmdbRecommendedTitle>> Lists { get; } = [];

        public List<RecommendationIdentity> Asked { get; } = [];

        public List<TmdbRecommendationGenerator> AskedGenerators { get; } = [];

        public Task<IReadOnlyList<TmdbRecommendedTitle>> ForSeedAsync(
            RecommendationIdentity seed, TmdbRecommendationGenerator generator, CancellationToken cancellationToken)
        {
            Asked.Add(seed);
            AskedGenerators.Add(generator);
            return Task.FromResult<IReadOnlyList<TmdbRecommendedTitle>>(
                Lists.TryGetValue(seed.TmdbId, out var list) ? list : []);
        }

        /// <summary>Answers nothing: these tests are about the per-seed lists only.</summary>
        public Task<IReadOnlyList<TmdbRecommendedTitle>> ForListAsync(
            TmdbRecommendationGenerator generator,
            RecommendationKind kind,
            string cacheKey,
            string path,
            TimeSpan lifetime,
            IReadOnlyList<string> arrays,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TmdbRecommendedTitle>>([]);
    }

    [Fact]
    public async Task ATitleSeveralSeedsAgreeOnOutranksOneASingleSeedLoves()
    {
        // Breadth over depth: agreement across a viewer's own taste is the stronger signal, even
        // when the lone recommendation sits at the very top of its seed's list.
        SeedWatched("1");
        SeedWatched("2");
        _tmdb.Lists["1"] = [Title("shared"), Title("only-a")];
        _tmdb.Lists["2"] = [Title("only-b"), Title("shared")];

        var result = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;

        Assert.Equal("shared", result[0].Identity.TmdbId);
    }

    [Fact]
    public async Task BreadthIsAFactorNotAVeto()
    {
        // The bug the weighted score replaces: ranking by seed count first meant two weak, ancient
        // seeds agreeing on a title unconditionally outranked the top pick of a film loved last week.
        // Agreement still counts — it is just no longer allowed to win on its own.
        SeedRated("loved", 5);
        SeedWatchedLongAgo("old-a");
        SeedWatchedLongAgo("old-b");
        _tmdb.Lists["loved"] = [Title("loved-pick")];
        _tmdb.Lists["old-a"] = [Title("filler-1"), Title("filler-2"), Title("weak-agreed")];
        _tmdb.Lists["old-b"] = [Title("filler-3"), Title("filler-4"), Title("weak-agreed")];

        var result = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;

        Assert.Equal("loved-pick", result[0].Identity.TmdbId);
        Assert.Contains(result, entry => entry.Identity.TmdbId == "weak-agreed");
    }

    [Fact]
    public async Task AcclaimBeatsAPerfectScoreFromThreeVotes()
    {
        // TMDb reports 10.0 on three votes, and the raw number cannot tell that from real acclaim.
        // Both candidates sit at the same position of the same seed, so quality is the only difference.
        SeedWatched("1");
        _tmdb.Lists["1"] = [Title("thin", voteAverage: 10.0, voteCount: 3)];
        SeedWatched("2");
        _tmdb.Lists["2"] = [Title("acclaimed", voteAverage: 8.4, voteCount: 12_000)];

        var result = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;

        Assert.Equal("acclaimed", result[0].Identity.TmdbId);
    }

    [Fact]
    public async Task ACandidateWithNoVoteDataIsScoredOnTheTermsItHas()
    {
        // "No features" must mean "average", never "bad" — otherwise every candidate that arrives
        // before enrichment sinks, which is the path a connected source's suggestions take.
        SeedWatched("1");
        _tmdb.Lists["1"] = [Title("featureless"), Title("mediocre", voteAverage: 4.0, voteCount: 5_000)];

        var result = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;

        Assert.Equal("featureless", result[0].Identity.TmdbId);
    }

    [Fact]
    public async Task TheDeepCutsDialDemotesTheBlockbusterAndLeavesItAloneAtZero()
    {
        // Both are recommended equally well; only fame separates them. At zero the dial must not
        // reorder anything, or every existing feed would shift the day it shipped.
        SeedWatched("1");
        SeedWatched("2");
        _tmdb.Lists["1"] = [Title("blockbuster", popularity: 950)];
        _tmdb.Lists["2"] = [Title("obscure", popularity: 1.4)];

        var untouched = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;
        Assert.Equal("blockbuster", untouched[0].Identity.TmdbId); // ordinal tiebreak, not the dial

        SetPopularityBias(1.0);
        var deepCuts = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;

        Assert.Equal("obscure", deepCuts[0].Identity.TmdbId);
    }

    [Fact]
    public async Task ACardNamesTheSeedThatEarnedItAndTheStarsItGot()
    {
        // The most convincing sentence this feature can print, and free: the weight already knew.
        SeedRated("1", 5);
        _tmdb.Lists["1"] = [Title("because-of-that")];

        var card = Assert.Single((await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates);

        Assert.Equal(RecommendationReason.RatedSeed, card.Candidate.Reason!.Kind);
        Assert.Equal("Movie 1", card.Candidate.Reason.Detail);
        Assert.Equal(5, card.Candidate.Reason.Rating);
    }

    [Fact]
    public async Task AnUnratedSeedIsNamedWithoutInventingStars()
    {
        SeedWatched("1");
        _tmdb.Lists["1"] = [Title("because-of-that")];

        var card = Assert.Single((await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates);

        Assert.Equal(RecommendationReason.Seed, card.Candidate.Reason!.Kind);
        Assert.Null(card.Candidate.Reason.Rating);
    }

    [Fact]
    public async Task AFeedBuiltFromHistorySaysSo()
    {
        SeedWatched("1");
        _tmdb.Lists["1"] = [Title("something")];

        Assert.Equal(
            RecommendationRung.History,
            (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Rung);
    }

    [Fact]
    public async Task WithNoHistoryTheLibraryItselfAnswersAndTheFeedAdmitsIt()
    {
        // An operator chose to acquire these titles, and that is taste before anything is played —
        // a weaker answer than a viewing history, and a real one, which is why it is labelled.
        AddItem(MediaKind.Movie, "Held one", "10", genre: "crime");
        AddItem(MediaKind.Movie, "Held two", "11", genre: "crime");

        var result = await Engine().RankAsync(_userId, 10, CancellationToken.None);

        Assert.Equal(RecommendationRung.Library, result.Rung);
        Assert.NotEmpty(result.Candidates);
        Assert.All(result.Candidates, card => Assert.Equal(RecommendationReason.InLibrary, card.Candidate.Reason!.Kind));
    }

    [Fact]
    public async Task HeldTitlesReachTheFeedEvenWhenTmdbSaysNothing()
    {
        // What makes the Jellyfin shelf fill. It used to be the discovery feed intersected with the
        // library, so a local title could only appear when TMDb happened to link it to something
        // watched — after the filter the pool was a handful. Asking the library directly costs no
        // requests and every row is playable, which is the only verb that surface has.
        var watched = AddItem(MediaKind.Movie, "Heat", "1", genre: "crime");
        AddPlay(watched.Id);
        AddItem(MediaKind.Movie, "Sicario", "2", genre: "crime");
        // TMDb is asked and answers with nothing at all.

        var result = await Engine().RankAsync(_userId, 10, CancellationToken.None);

        Assert.Equal(RecommendationRung.History, result.Rung);
        Assert.Contains(result.Candidates, card => card.Identity.TmdbId == "2");
    }

    [Fact]
    public async Task WithNeitherHistoryNorLibraryTheEngineStillSaysNothing()
    {
        // The bottom of the ladder is silence, not filler. Trending would not be a recommendation.
        var result = await Engine().RankAsync(_userId, 10, CancellationToken.None);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task TmdbsOwnOrderBreaksTiesWithinASeed()
    {
        SeedWatched("1");
        _tmdb.Lists["1"] = [Title("first"), Title("second"), Title("third")];

        var result = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;

        Assert.Equal(["first", "second", "third"], result.Select(entry => entry.Identity.TmdbId));
    }

    [Fact]
    public async Task ASeedIsNeverRecommendedBackToTheUser()
    {
        // They already watched it — and one seed recommending another is not news either.
        SeedWatched("1");
        SeedWatched("2");
        _tmdb.Lists["1"] = [Title("2"), Title("fresh")];
        _tmdb.Lists["2"] = [Title("1")];

        var result = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;

        Assert.Equal("fresh", Assert.Single(result).Identity.TmdbId);
    }

    [Fact]
    public async Task WithNothingWatchedTheEngineSaysNothing()
    {
        // Filler (trending, popular) would not be a recommendation, and pretending otherwise is worse
        // than an empty row the UI can explain.
        var result = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;

        Assert.Empty(result);
        Assert.Empty(_tmdb.Asked);
    }

    [Fact]
    public async Task TheResultIsOrderedMostRelevantFirst()
    {
        // Position is the whole output — the surface turns it into a rank, and fusion is gone.
        SeedWatched("1");
        _tmdb.Lists["1"] = [Title("a"), Title("b"), Title("c")];

        var result = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;

        Assert.Equal(["a", "b", "c"], result.Select(entry => entry.Identity.TmdbId));
    }

    [Fact]
    public async Task TheLimitIsHonoured()
    {
        SeedWatched("1");
        _tmdb.Lists["1"] = [.. Enumerable.Range(0, 15).Select(index => Title($"c{index}"))];

        var result = (await Engine().RankAsync(_userId, 5, CancellationToken.None)).Candidates;

        Assert.Equal(5, result.Count);
    }

    [Fact]
    public async Task SeriesSeedsAskForSeriesRecommendations()
    {
        // A kind mix-up here would ask TMDb's movie endpoint about a show id and quietly return
        // someone else's film.
        var series = AddItem(MediaKind.Series, "Severance", "95396");
        var episode = AddItem(MediaKind.Episode, "S1E1", null, series.Id);
        AddPlay(episode.Id);
        _tmdb.Lists["95396"] = [Title("similar-show")];

        var result = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;

        Assert.Equal(RecommendationKind.Series, Assert.Single(_tmdb.Asked).Kind);
        Assert.Equal(RecommendationKind.Series, Assert.Single(result).Identity.Kind);
    }

    [Fact]
    public async Task ASeedThatTmdbCannotAnswerForIsSurvivable()
    {
        // An unknown title or a brief outage yields an empty list; the other seeds still carry the feed.
        SeedWatched("1");
        SeedWatched("2");
        _tmdb.Lists["2"] = [Title("from-the-other-seed")];

        var result = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;

        Assert.Equal("from-the-other-seed", Assert.Single(result).Identity.TmdbId);
    }

    [Fact]
    public async Task PosterPathsBecomeAbsoluteUrls()
    {
        SeedWatched("1");
        _tmdb.Lists["1"] = [new TmdbRecommendedTitle("a", "A Title", 2021, "/poster.jpg")];

        var result = (await Engine().RankAsync(_userId, 10, CancellationToken.None)).Candidates;

        // The engine carries the path TMDb gave; the feed service is what turns it into a URL.
        Assert.Equal("/poster.jpg", Assert.Single(result).Candidate.Title.PosterPath);
    }

    /// <summary>
    /// The behavioural seed generator plus the held one. The seed lists are what these tests are about;
    /// `held` is here because the cold-start ladder has nothing to fall to without it, and it stays
    /// quiet whenever the fixture's library is empty — which it is for every aggregation test.
    /// </summary>
    private RecommendationEngine Engine() => new(
        _database,
        new RecommendationSeedSelector(_database, _time),
        [SeedListGenerator.Recommendations(_tmdb), new HeldGenerator(_database, new TitleFacetReader(_database))],
        new TitleFacetReader(_database),
        new TasteProfileCache(),
        new TasteProfileBuilder(_database, new TitleFacetReader(_database), new LibraryFacetIndexCache(), _time),
        new RecommendationScorer(),
        new RecommendationReranker(),
        new RecommendationPreferenceStore(_database),
        NullLogger<RecommendationEngine>.Instance);

    private static TmdbRecommendedTitle Title(string tmdbId) => new(tmdbId, $"Title {tmdbId}", 2024, null);

    private static TmdbRecommendedTitle Title(
        string tmdbId, double? voteAverage = null, int? voteCount = null, double? popularity = null) =>
        new(tmdbId, $"Title {tmdbId}", 2024, null, null, voteAverage, voteCount, popularity);

    private void SeedWatched(string tmdbId) => AddPlay(AddItem(MediaKind.Movie, $"Movie {tmdbId}", tmdbId).Id);

    /// <summary>A seed watched long enough ago that its unrated weight has decayed to almost nothing.</summary>
    private void SeedWatchedLongAgo(string tmdbId) =>
        AddPlay(AddItem(MediaKind.Movie, $"Movie {tmdbId}", tmdbId).Id, _time.GetUtcNow().AddYears(-2));

    /// <summary>A seed the user gave stars to, which is what the weighted score is meant to respect.</summary>
    private void SeedRated(string tmdbId, int stars)
    {
        var item = AddItem(MediaKind.Movie, $"Movie {tmdbId}", tmdbId);
        AddPlay(item.Id);
        _database.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = item.Id, Rating = stars,
        });
        _database.SaveChanges();
    }

    private void SetPopularityBias(double bias)
    {
        _database.RecommendationPreferences.Add(new RecommendationPreference
        {
            Id = Guid.NewGuid(), AppUserId = _userId, PopularityBias = bias, UpdatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
    }

    private MediaItem AddItem(
        MediaKind kind, string title, string? tmdbId, Guid? seriesId = null, string? genre = null)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = _catalogId, Kind = kind, Title = title, SeriesId = seriesId,
            Year = 2016, AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        if (tmdbId is not null)
        {
            item.IdentityProvider = "tmdb";
            item.IdentityProviderId = tmdbId;
        }

        _database.MediaItems.Add(item);
        if (genre is not null)
        {
            _database.MetadataRecords.Add(new MetadataRecord
            {
                Id = Guid.NewGuid(), MediaItemId = item.Id, Provider = "tmdb", Language = "en-US",
                Genres = [genre], FetchedAt = _time.GetUtcNow(),
            });
        }

        _database.SaveChanges();
        return item;
    }

    private void AddPlay(Guid itemId, DateTimeOffset? watchedAt = null)
    {
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = itemId,
            CreatedAt = _time.GetUtcNow(), WatchedAt = watchedAt ?? _time.GetUtcNow(),
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
