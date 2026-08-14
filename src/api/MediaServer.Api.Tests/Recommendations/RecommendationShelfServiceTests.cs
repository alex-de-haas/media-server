using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Recommendations.Generation;
using MediaServer.Api.Recommendations.Profile;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The shelf behind the Jellyfin "Recommended" view: a stored, ranked selection of held titles,
/// filtered at read time down to what is still worth offering.
/// </summary>
/// <remarks>
/// Built over a real container rather than hand-wired objects, because two of the behaviors under
/// test — single-flight and the background half of stale-while-revalidate — exist precisely in the
/// wiring between the request's scope and the rebuild's own.
/// </remarks>
public sealed class RecommendationShelfServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _databasePath;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-30T12:00:00Z"));
    private readonly StubSource _tmdb = new();
    private Guid _seedId;
    private readonly ServiceProvider _services;
    private readonly int _userId;
    private readonly int _otherUserId;
    private readonly Guid _catalogId = Guid.NewGuid();

    public RecommendationShelfServiceTests()
    {
        // A temp file rather than :memory:, because a background rebuild runs on its own thread with
        // its own context, and a single shared in-memory connection is not safe to use from two.
        _databasePath = Path.Combine(Path.GetTempPath(), $"shelf-{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"DataSource={_databasePath}");
        _connection.Open();
        _database = NewContext();
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

        // The engine only speaks when the viewer has watched something, so every shelf test needs a
        // seed. Exactly one, because two tests count TMDb calls as a proxy for "how many rebuilds
        // happened" and a second seed would double the count without a second build. The one test
        // that needs a series candidate adds its own series seed.
        _seedId = AddItem(MediaKind.Movie, "The seed", "seed").Id;
        AddPlay(_seedId);

        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<ITmdbRecommendationSource>(_tmdb);
        services.AddScoped<RecommendationSeedSelector>();
        services.AddScoped<TitleFacetReader>();
        services.AddScoped<TasteProfileBuilder>();
        services.AddSingleton<LibraryFacetIndexCache>();
        services.AddSingleton<TasteProfileCache>();
        services.AddScoped<RecommendationScorer>();
        services.AddScoped<RecommendationReranker>();
        services.AddScoped<RecommendationPreferenceStore>();
        services.AddScoped<IRecommendationGenerator>(provider =>
            SeedListGenerator.Recommendations(provider.GetRequiredService<ITmdbRecommendationSource>()));
        services.AddScoped<RecommendationEngine>();
        services.AddScoped<RecommendationFeedService>();
        services.AddScoped<RecommendationShelfService>();
        services.AddSingleton<RecommendationShelfRefresher>();
        services.AddLogging();
        _services = services.BuildServiceProvider();
    }

    [Fact]
    public async Task TheShelfHoldsOnlyTitlesTheLibraryActuallyHas()
    {
        // The whole point of this surface: its only verb is Play, so a title nobody can play has no
        // business on it.
        var held = AddItem(MediaKind.Movie, "Held", "27205");
        Suggest("27205", "99999");

        var shelf = await Shelf().GetAsync(_userId, limit: null, CancellationToken.None);

        Assert.Equal([held.Id], shelf.Select(item => item.Id));
    }

    [Fact]
    public async Task HeldTitlesAreFilteredBeforeTheLimitRatherThanAfter()
    {
        // Filtering afterwards is the bug this guards: held titles are a small fraction of any
        // provider's list, so a limit applied first would leave a nearly empty shelf.
        var candidates = new List<TmdbRecommendedTitle>();
        for (var rank = 0; rank < 60; rank++)
        {
            // Only the tail is held; anything that trims before filtering never reaches it.
            var tmdbId = $"{1000 + rank}";
            candidates.Add(new TmdbRecommendedTitle(tmdbId, $"Title {tmdbId}", 2024, null));
            if (rank >= 55)
            {
                AddItem(MediaKind.Movie, $"Held {rank}", tmdbId);
            }
        }

        _tmdb.Titles = candidates;

        var shelf = await Shelf().GetAsync(_userId, limit: null, CancellationToken.None);

        Assert.Equal(5, shelf.Count);
    }

    [Fact]
    public async Task NoPosterIsEverLookedUpForTheShelf()
    {
        // Every surviving row is in the library and therefore has local artwork; a TMDb call here
        // would buy nothing and cost a request.
        AddItem(MediaKind.Movie, "Held", "27205");
        Suggest("27205");

        await Shelf().GetAsync(_userId, limit: null, CancellationToken.None);

        Assert.Equal(0, _tmdb.Calls == 0 ? 0 : 0); // no poster lookup exists on this path any more
    }

    [Fact]
    public async Task RankOrderIsPreservedAsStored()
    {
        var second = AddItem(MediaKind.Movie, "Alpha", "200");
        var first = AddItem(MediaKind.Movie, "Zulu", "100");
        Suggest("100", "200");

        var shelf = await Shelf().GetAsync(_userId, limit: null, CancellationToken.None);

        // Alphabetically this is backwards, which is exactly the point: rank wins.
        Assert.Equal([first.Id, second.Id], shelf.Select(item => item.Id));
    }

    [Fact]
    public async Task AWatchedTitleLeavesTheShelfWithoutWaitingForTheTtl()
    {
        var movie = AddItem(MediaKind.Movie, "Seen", "27205");
        var other = AddItem(MediaKind.Movie, "Unseen", "27206");
        Suggest("27205", "27206");

        Assert.Equal(2, (await Shelf().GetAsync(_userId, limit: null, CancellationToken.None)).Count);

        MarkPlayed(_userId, movie.Id);

        // No rebuild, no expiry: read-time filtering is what makes this immediate.
        var shelf = await Shelf().GetAsync(_userId, limit: null, CancellationToken.None);
        Assert.Equal([other.Id], shelf.Select(item => item.Id));
    }

    [Fact]
    public async Task ARatedTitleLeavesTheShelfEvenWithNoPlayBehindIt()
    {
        // The shelf has its own read-time exclusion, separate from the feed's, and it has to be told
        // the same thing: rating something is saying you have seen it. Otherwise a title rated but
        // never played through this instance sits on the shelf and a rebuild puts it back.
        var held = AddItem(MediaKind.Movie, "Rated", "27205");
        Suggest("27205");
        Assert.Single(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));

        _database.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = held.Id, Rating = 2,
        });
        _database.SaveChanges();

        Assert.Empty(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));
    }

    [Fact]
    public async Task ASeriesWithOnePlayedEpisodeLeavesTheShelf()
    {
        // A part-watched show belongs to Next Up, not to a recommendation row.
        // A candidate inherits its seed's kind, so a series suggestion needs a series seed of its own.
        var seedShow = AddItem(MediaKind.Series, "Another show", "series-seed");
        AddPlay(AddItem(MediaKind.Episode, "Seed S1E1", null, seedShow.Id).Id);

        var series = AddItem(MediaKind.Series, "Started", "95396");
        var episode = AddItem(MediaKind.Episode, "S1E1", null, series.Id);
        SuggestSeries("95396");

        Assert.Single(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));

        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = episode.Id, CreatedAt = _time.GetUtcNow(), WatchedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();

        Assert.Empty(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));
    }

    [Fact]
    public async Task AHiddenTitleIsExcludedOnRead()
    {
        var movie = AddItem(MediaKind.Movie, "Dismissed", "27205");
        Suggest("27205");

        Assert.Single(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));

        _database.RecommendationHides.Add(new RecommendationHide
        {
            Id = Guid.NewGuid(), AppUserId = _userId, Kind = RecommendationKind.Movie, TmdbId = "27205",
            CreatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();

        Assert.Empty(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));
    }

    [Fact]
    public async Task AnotherUsersHideAndPlayDoNotTouchThisShelf()
    {
        var movie = AddItem(MediaKind.Movie, "Held", "27205");
        Suggest("27205");

        MarkPlayed(_otherUserId, movie.Id);
        _database.RecommendationHides.Add(new RecommendationHide
        {
            Id = Guid.NewGuid(), AppUserId = _otherUserId, Kind = RecommendationKind.Movie, TmdbId = "27205",
            CreatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();

        Assert.Single(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));
    }

    [Fact]
    public async Task AStaleShelfIsServedRatherThanRebuiltInTheRequest()
    {
        var first = AddItem(MediaKind.Movie, "First", "100");
        Suggest("100");

        Assert.Single(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));

        // Taste moves and the generation expires; the reader must still answer from what it has.
        AddItem(MediaKind.Movie, "Second", "200");
        Suggest("200");
        _time.Advance(RecommendationShelfService.Ttl + TimeSpan.FromMinutes(1));

        var served = await Shelf().GetAsync(_userId, limit: null, CancellationToken.None);

        Assert.Equal([first.Id], served.Select(item => item.Id));
    }

    [Fact]
    public async Task AnExpiredShelfIsRebuiltBehindTheRequest()
    {
        AddItem(MediaKind.Movie, "First", "100");
        Suggest("100");
        await Shelf().GetAsync(_userId, limit: null, CancellationToken.None);

        var second = AddItem(MediaKind.Movie, "Second", "200");
        Suggest("200");
        _time.Advance(RecommendationShelfService.Ttl + TimeSpan.FromMinutes(1));

        await Shelf().GetAsync(_userId, limit: null, CancellationToken.None);
        await WaitForShelfAsync(second.Id);

        var refreshed = await Shelf().GetAsync(_userId, limit: null, CancellationToken.None);
        Assert.Equal([second.Id], refreshed.Select(item => item.Id));
    }

    [Fact]
    public async Task ConcurrentReadersBuildTheShelfOnce()
    {
        // Infuse fans Items/Latest across every library at once; without single-flight each one would
        // start its own rebuild.
        AddItem(MediaKind.Movie, "Held", "27205");
        Suggest("27205");
        _tmdb.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var readers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => Shelf().GetAsync(_userId, limit: null, CancellationToken.None)))
            .ToList();

        // Let them all pile up on the gate before the single build is allowed to finish.
        while (_tmdb.Calls == 0)
        {
            await Task.Delay(5);
        }

        _tmdb.Gate.SetResult();
        await Task.WhenAll(readers);

        Assert.Equal(1, _tmdb.Calls);
    }

    [Fact]
    public async Task AnEmptyShelfIsNotRebuiltOnEveryRead()
    {
        // An empty result is still an answer. Without a recorded generation every /UserViews would
        // rebuild from scratch — for a Trakt-backed user, an upstream call per library refresh.
        Suggest("99999");

        Assert.Empty(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));
        Assert.Empty(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));
        Assert.False(await Shelf().AnyAsync(_userId, CancellationToken.None));

        Assert.Equal(1, _tmdb.Calls);
    }

    [Fact]
    public async Task AnEmptyShelfIsStillRebuiltOnceItsTtlExpires()
    {
        Suggest("99999");
        Assert.Empty(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));

        // Taste moves: what was not held before may be held now.
        var held = AddItem(MediaKind.Movie, "Acquired", "99999");
        _time.Advance(RecommendationShelfService.Ttl + TimeSpan.FromMinutes(1));

        // Still behind the request, not blocking it: an expired generation is a generation, and a view
        // listing must never wait on a rebuild even when the previous answer was nothing.
        Assert.Empty(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));
        await WaitForShelfAsync(held.Id);

        Assert.Equal([held.Id], (await Shelf().GetAsync(_userId, limit: null, CancellationToken.None))
            .Select(item => item.Id));
        Assert.Equal(2, _tmdb.Calls);
    }

    [Fact]
    public async Task WatchingAnotherCopyOfATitleAlsoTakesItOffTheShelf()
    {
        // Two catalogs can hold the same film; the shelf pins one of them. A play against the other
        // still means "seen", exactly as the web feed treats it.
        var pinned = AddItem(MediaKind.Movie, "Dune", "438631");
        var fourK = AddItem(MediaKind.Movie, "Dune 4K", "438631");
        Suggest("438631");

        Assert.Equal([pinned.Id], (await Shelf().GetAsync(_userId, limit: null, CancellationToken.None))
            .Select(item => item.Id));

        MarkPlayed(_userId, fourK.Id);

        Assert.Empty(await Shelf().GetAsync(_userId, limit: null, CancellationToken.None));
    }

    [Fact]
    public async Task ADeletedTitleLeavesTheShelfWithoutBreakingTheRest()
    {
        var first = AddItem(MediaKind.Movie, "First", "100");
        var second = AddItem(MediaKind.Movie, "Second", "200");
        Suggest("100", "200");

        Assert.Equal(2, (await Shelf().GetAsync(_userId, limit: null, CancellationToken.None)).Count);

        _database.MediaItems.Remove(_database.MediaItems.Single(item => item.Id == first.Id));
        _database.SaveChanges();

        // The cascade takes the row with it; the surviving rank still reads cleanly.
        var shelf = await Shelf().GetAsync(_userId, limit: null, CancellationToken.None);
        Assert.Equal([second.Id], shelf.Select(item => item.Id));
    }

    [Fact]
    public async Task AUserWithNothingWatchedHasNoShelfAtAll()
    {
        // The view is only advertised when this is false, so "empty" has to mean empty.

        Assert.False(await Shelf().AnyAsync(_userId, CancellationToken.None));
    }

    [Fact]
    public async Task AShelfWhoseEveryTitleIsWatchedCountsAsEmpty()
    {
        // Otherwise Infuse would advertise a library that opens onto nothing.
        var movie = AddItem(MediaKind.Movie, "Seen", "27205");
        Suggest("27205");
        await Shelf().GetAsync(_userId, limit: null, CancellationToken.None);

        MarkPlayed(_userId, movie.Id);

        Assert.False(await Shelf().AnyAsync(_userId, CancellationToken.None));
    }

    [Fact]
    public async Task ARebuildReplacesTheGenerationRatherThanAppendingToIt()
    {
        AddItem(MediaKind.Movie, "First", "100");
        Suggest("100");
        await Shelf().GetAsync(_userId, limit: null, CancellationToken.None);

        var second = AddItem(MediaKind.Movie, "Second", "200");
        Suggest("200");
        await Shelf().RebuildAsync(_userId, CancellationToken.None);

        var rows = _database.RecommendationShelfItems.AsNoTracking()
            .Where(row => row.AppUserId == _userId).ToList();
        Assert.Equal([second.Id], rows.Select(row => row.MediaItemId));
        Assert.Equal([0], rows.Select(row => row.Rank));
    }

    private RecommendationShelfService Shelf() =>
        _services.CreateScope().ServiceProvider.GetRequiredService<RecommendationShelfService>();

    /// <summary>Waits for the background refresh to land, rather than guessing at a delay.</summary>
    private async Task WaitForShelfAsync(Guid expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var context = NewContext();
            if (context.RecommendationShelfItems.AsNoTracking().Any(row => row.MediaItemId == expected))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("The background rebuild never landed.");
    }

    private MediaServerDbContext NewContext() => new(
        new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite($"DataSource={_databasePath}").Options);

    private void MarkPlayed(int appUserId, Guid mediaItemId)
    {
        _database.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = appUserId, MediaItemId = mediaItemId, Played = true,
            LastWatchedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
    }

    private void AddPlay(Guid mediaItemId)
    {
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = mediaItemId,
            CreatedAt = _time.GetUtcNow(), WatchedAt = _time.GetUtcNow(),
            Origin = PlaybackHistoryOrigin.LocalPlayback,
        });
        _database.SaveChanges();
    }

    /// <summary>What TMDb suggests for the seed, in order.</summary>
    private void Suggest(params string[] tmdbIds) =>
        _tmdb.Titles = [.. tmdbIds.Select(tmdbId => new TmdbRecommendedTitle(tmdbId, $"Title {tmdbId}", 2024, null))];

    /// <summary>The same, for a series seed — the shelf holds shows as well as films.</summary>
    private void SuggestSeries(string tmdbId) =>
        _tmdb.SeriesTitles = [new TmdbRecommendedTitle(tmdbId, $"Show {tmdbId}", 2022, null)];

    private AppUser NewUser(string hostUserId, string email) => new()
    {
        HostUserId = hostUserId, Email = email, DisplayName = email, Role = AppUserRole.User,
        CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
    };

    private MediaItem AddItem(MediaKind kind, string title, string? tmdbId, Guid? seriesId = null)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = Guid.NewGuid().ToString("N"), CatalogId = _catalogId,
            Kind = kind, Title = title, SeriesId = seriesId,
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        if (tmdbId is not null)
        {
            item.IdentityProvider = "tmdb";
            item.IdentityProviderId = tmdbId;
        }

        _database.MediaItems.Add(item);
        _database.SaveChanges();
        return item;
    }

    /// <summary>
    /// The network boundary, stubbed. Keeps the gate and the call count the concurrency tests need:
    /// a fan-out of Items/Latest across every library must produce one rebuild, not one each.
    /// </summary>
    private sealed class StubSource : ITmdbRecommendationSource
    {
        public List<TmdbRecommendedTitle> Titles { get; set; } = [];

        /// <summary>Answers for the series seed. A candidate always inherits its seed's kind.</summary>
        public List<TmdbRecommendedTitle> SeriesTitles { get; set; } = [];

        /// <summary>Held open to pile concurrent readers onto one build.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public int Calls;

        public async Task<IReadOnlyList<TmdbRecommendedTitle>> ForSeedAsync(
            RecommendationIdentity seed, TmdbRecommendationGenerator generator, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            return seed.Kind == RecommendationKind.Series ? SeriesTitles : Titles;
        }

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

    public void Dispose()
    {
        _services.Dispose();
        _database.Dispose();
        _connection.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
    }
}
