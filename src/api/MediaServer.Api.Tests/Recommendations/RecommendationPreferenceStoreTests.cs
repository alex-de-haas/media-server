using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The Popular ↔ Deep cuts dial: its default, its bounds, and that it shares a row with the source
/// preference without either setting clobbering the other.
/// </summary>
public sealed class RecommendationPreferenceStoreTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-08-14T12:00:00Z"));
    private readonly int _userId;

    public RecommendationPreferenceStoreTests()
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
    }

    [Fact]
    public async Task AUserWhoNeverTouchedTheDialGetsTheBehaviourTheFeedAlwaysHad()
    {
        Assert.Equal(0, await Store().PopularityBiasAsync(_userId, CancellationToken.None));
    }

    [Fact]
    public async Task TheDialRoundTrips()
    {
        Assert.True(await Store().SetPopularityBiasAsync(_userId, 0.8, _time.GetUtcNow(), CancellationToken.None));

        Assert.Equal(0.8, await Store().PopularityBiasAsync(_userId, CancellationToken.None));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(RecommendationPreferenceStore.MaxPopularityBias + 0.1)]
    [InlineData(double.NaN)]
    public async Task AValueOffTheDialIsRefusedRatherThanClamped(double bias)
    {
        // Clamping would store a setting the user did not choose and then show it back to them.
        Assert.False(await Store().SetPopularityBiasAsync(_userId, bias, _time.GetUtcNow(), CancellationToken.None));
        Assert.Equal(0, await Store().PopularityBiasAsync(_userId, CancellationToken.None));
    }

    [Fact]
    public async Task SettingTheDialDoesNotDisturbTheSourcePreference()
    {
        // The two live in one row but are separate statements: narrowing sources is not a claim about
        // popularity, and moving the dial is not a claim about sources.
        _database.RecommendationPreferences.Add(new RecommendationPreference
        {
            Id = Guid.NewGuid(), AppUserId = _userId, Sources = "library", UpdatedAt = _time.GetUtcNow(),
        });
        await _database.SaveChangesAsync();

        Assert.True(await Store().SetPopularityBiasAsync(_userId, 1.5, _time.GetUtcNow(), CancellationToken.None));

        _database.ChangeTracker.Clear();
        var row = await _database.RecommendationPreferences.SingleAsync();
        Assert.Equal("library", row.Sources);
        Assert.Equal(1.5, row.PopularityBias);
    }

    [Fact]
    public async Task SettingTheDialFirstLeavesEverySourceAvailable()
    {
        // A null Sources means "every available source". Creating the row for the dial's sake must
        // not read as the user having turned sources off.
        Assert.True(await Store().SetPopularityBiasAsync(_userId, 0.5, _time.GetUtcNow(), CancellationToken.None));

        Assert.Null((await _database.RecommendationPreferences.SingleAsync()).Sources);
    }

    private RecommendationPreferenceStore Store() => new(_database);

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
