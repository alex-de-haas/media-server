using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Recommendations.Evaluation;

/// <summary>
/// The evaluation harness: that its arithmetic is right, and that it leaves the database as it found
/// it.
/// </summary>
/// <remarks>
/// These tests deliberately make <b>no claim about recommendation quality</b>. Quality can only be
/// measured against a real history — a synthetic one would score whatever rule generated it — so what
/// is checked here is the instrument, not the reading. See
/// <see cref="RecommendationEvaluationHarness"/> for how to take a real reading.
/// </remarks>
public sealed class RecommendationEvaluationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-08-14T12:00:00Z"));
    private readonly int _userId;
    private readonly Guid _catalogId = Guid.NewGuid();

    public RecommendationEvaluationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _database = NewContext();
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
    public void RecallCountsTheShareOfTheFutureThatWasFound()
    {
        var future = Future("a", "b", "c", "d");

        Assert.Equal(0.5, RecommendationEvaluationHarness.Recall(Identities("a", "x", "b"), future));
        Assert.Equal(0, RecommendationEvaluationHarness.Recall(Identities("x", "y"), future));
        Assert.Equal(1, RecommendationEvaluationHarness.Recall(Identities("a", "b", "c", "d"), future));
    }

    [Fact]
    public void RecallIgnoresAnythingPastTheCut()
    {
        // The metric is named for the cut; counting a hit at rank fifty would be measuring a list
        // nobody scrolls.
        var future = Future("target");
        var padded = Identities([.. Enumerable.Range(0, RecommendationEvaluationHarness.At).Select(index => $"pad{index}")])
            .Concat(future.ToList())
            .ToList();

        Assert.Equal(0, RecommendationEvaluationHarness.Recall(padded, future));
    }

    [Fact]
    public void NdcgRewardsFindingTheSameTitlesHigherUp()
    {
        var future = Future("a", "b");

        var early = RecommendationEvaluationHarness.Ndcg(Identities("a", "b", "x", "y"), future);
        var late = RecommendationEvaluationHarness.Ndcg(Identities("x", "y", "a", "b"), future);

        Assert.True(early > late);
        // Perfect order is exactly one, whatever the number of held-out titles.
        Assert.Equal(1, early, precision: 9);
    }

    [Fact]
    public void NdcgIsZeroWhenNothingWasFoundAndWhenThereWasNothingToFind()
    {
        Assert.Equal(0, RecommendationEvaluationHarness.Ndcg(Identities("x"), Future("a")));
        Assert.Equal(0, RecommendationEvaluationHarness.Ndcg(Identities("a"), new HashSet<RecommendationIdentity>()));
    }

    [Fact]
    public async Task AUserWithTooLittleHistoryIsSkippedRatherThanScoredZero()
    {
        // Averaging a user who could not be split in would drag every configuration toward zero and
        // report a difference between weightings as smaller than it is.
        for (var index = 0; index < RecommendationEvaluationHarness.MinimumPlays - 1; index++)
        {
            AddPlay(AddMovie($"Seen {index}", $"{index}").Id, _time.GetUtcNow().AddDays(-index));
        }

        var result = await Harness().RunAsync(RecommendationWeights.Default);

        Assert.Equal(0, result.Users);
    }

    [Fact]
    public async Task TheRunLeavesTheHistoryExactlyAsItFoundIt()
    {
        // The whole point of the rollback: this is meant to be safe to point at a real database.
        for (var index = 0; index < 10; index++)
        {
            AddPlay(AddMovie($"Seen {index}", $"{index}").Id, _time.GetUtcNow().AddDays(-index));
        }

        var before = await _database.PlaybackHistoryEntries.CountAsync();
        await Harness().RunAsync(RecommendationWeights.Default);

        await using var verify = NewContext();
        Assert.Equal(before, await verify.PlaybackHistoryEntries.CountAsync());
    }

    [Fact]
    public async Task ASweepReportsEveryConfigurationOverTheSameHistory()
    {
        for (var index = 0; index < 10; index++)
        {
            AddPlay(AddMovie($"Seen {index}", $"{index}").Id, _time.GetUtcNow().AddDays(-index));
        }

        var results = await Harness().SweepAsync([
            ("shipped", RecommendationWeights.Default),
            ("ratings ignored", RecommendationWeights.Default with
            {
                RatingWeights = new Dictionary<int, double> { [3] = 1, [4] = 1, [5] = 1 },
            }),
        ]);

        Assert.Equal(["shipped", "ratings ignored"], results.Select(entry => entry.Label));
        Assert.All(results, entry => Assert.Equal(results[0].Result.Users, entry.Result.Users));
    }

    private RecommendationEvaluationHarness Harness() => new(NewContext, _time);

    private MediaServerDbContext NewContext() =>
        new(new DbContextOptionsBuilder<MediaServerDbContext>().UseSqlite(_connection).Options);

    private static List<RecommendationIdentity> Identities(params string[] tmdbIds) =>
        [.. tmdbIds.Select(tmdbId => new RecommendationIdentity(RecommendationKind.Movie, tmdbId))];

    private static HashSet<RecommendationIdentity> Future(params string[] tmdbIds) => [.. Identities(tmdbIds)];

    private MediaItem AddMovie(string title, string tmdbId)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = _catalogId, Kind = MediaKind.Movie, Title = title, Year = 2016,
            IdentityProvider = "tmdb", IdentityProviderId = tmdbId,
            AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(item);
        _database.SaveChanges();
        return item;
    }

    private void AddPlay(Guid itemId, DateTimeOffset watchedAt)
    {
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = itemId,
            CreatedAt = _time.GetUtcNow(), WatchedAt = watchedAt,
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
