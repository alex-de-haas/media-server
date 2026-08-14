using MediaServer.Api.Data;
using MediaServer.Api.Recommendations;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The Popular ↔ Deep cuts dial: its default and its bounds.
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



    private RecommendationPreferenceStore Store() => new(_database);

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
