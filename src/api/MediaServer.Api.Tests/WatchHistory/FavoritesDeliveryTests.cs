using MediaServer.Api.Data;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.WatchHistory;

/// <summary>
/// Delivering queued favorites. The case that matters most is Trakt's full list: HTTP 420 is not a
/// pacing problem, so it must end the event rather than retry into the same wall — and it must stay
/// visible instead of being swallowed.
/// </summary>
public sealed class FavoritesDeliveryTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-26T12:00:00Z"));
    private readonly StubFavoritesProvider _provider = new();

    private int _userId;
    private Guid _connectionId;
    private Guid _movieId;

    public FavoritesDeliveryTests()
    {
        Seed();
        _context = _db.Create();
    }

    private WatchHistoryDeliveryService Service() => new(
        _context, new StubRegistry(_provider), _time, NullLogger<WatchHistoryDeliveryService>.Instance);

    [Fact]
    public async Task An_add_is_delivered_and_records_how_full_the_list_is()
    {
        _provider.RemoteCountAfterWrite = 42;
        await QueueAsync(WatchHistoryOutboxOperation.AddFavorite);

        var result = await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(1, result.Delivered);
        await using var verify = _db.Create();
        var queued = Assert.Single(await verify.WatchHistoryOutboxEvents.ToListAsync());
        Assert.Equal(WatchHistoryOutboxStatus.Completed, queued.Status);
        var connection = await verify.WatchHistoryConnections.SingleAsync();
        Assert.Equal(42, connection.FavoritesRemoteCount);
        // The cap rides along with every write: Settings shows the counter only when it has both, so a
        // user who never runs an explicit sync would otherwise see neither half.
        Assert.Equal(100, connection.FavoritesCapacity);
    }

    [Fact]
    public async Task A_full_trakt_list_ends_the_event_instead_of_retrying()
    {
        _provider.Failure = WatchHistoryFailure.AccountLimitReached;
        await QueueAsync(WatchHistoryOutboxOperation.AddFavorite);

        var result = await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Retried);
        await using var verify = _db.Create();
        var queued = Assert.Single(await verify.WatchHistoryOutboxEvents.ToListAsync());
        // Terminal and surfaced: only the user can free space, and a silent swallow is what this design
        // set out to avoid.
        Assert.Equal(WatchHistoryOutboxStatus.Terminal, queued.Status);
        Assert.NotNull(queued.LastError);
    }

    [Fact]
    public async Task A_transient_failure_still_retries()
    {
        _provider.Failure = WatchHistoryFailure.Transient;
        await QueueAsync(WatchHistoryOutboxOperation.RemoveFavorite);

        var result = await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(1, result.Retried);
        await using var verify = _db.Create();
        Assert.Equal(WatchHistoryOutboxStatus.Pending, (await verify.WatchHistoryOutboxEvents.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_title_the_provider_does_not_know_ends_the_event()
    {
        _provider.NotFound = 1;
        await QueueAsync(WatchHistoryOutboxOperation.AddFavorite);

        var result = await Service().DeliverAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);
        await using var verify = _db.Create();
        Assert.Equal(WatchHistoryOutboxStatus.Terminal, (await verify.WatchHistoryOutboxEvents.SingleAsync()).Status);
    }

    private async Task QueueAsync(WatchHistoryOutboxOperation operation)
    {
        await using var seed = _db.Create();
        seed.WatchHistoryOutboxEvents.Add(new WatchHistoryOutboxEvent
        {
            Id = Guid.NewGuid(),
            ConnectionId = _connectionId,
            AppUserId = _userId,
            MediaItemId = _movieId,
            Operation = operation,
            IdentitySnapshot = FavoritesRecorder.Snapshot(new FavoriteIdentity(FavoriteWorkKind.Movie, 27205, null)),
            IdempotencyKey = $"key-{operation}",
            Status = WatchHistoryOutboxStatus.Pending,
            CreatedAt = _time.GetUtcNow(),
            NextAttemptAt = _time.GetUtcNow(),
        });
        await seed.SaveChangesAsync();
    }

    private void Seed()
    {
        var now = _time.GetUtcNow();
        using var context = _db.Create();

        var user = new AppUser
        {
            HostUserId = "host-1", Email = "user@example.com", Role = AppUserRole.User,
            CreatedAt = now, LastSeenAt = now,
        };
        context.AppUsers.Add(user);
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(), Name = "Movies", Type = CatalogType.Movie, Root = "/tmp/na",
            CreatedAt = now, UpdatedAt = now,
        };
        context.Catalogs.Add(catalog);
        var movie = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = "pub-movie", CatalogId = catalog.Id, Kind = MediaKind.Movie,
            Title = "Inception", Year = 2010, IdentityProvider = "tmdb", IdentityProviderId = "27205",
            AddedAt = now, UpdatedAt = now,
        };
        context.MediaItems.Add(movie);
        context.SaveChanges();

        _userId = user.Id;
        _movieId = movie.Id;

        var connection = new WatchHistoryProviderConnection
        {
            Id = Guid.NewGuid(), AppUserId = user.Id, ProviderKey = "trakt",
            Status = WatchHistoryConnectionStatus.Connected, ConnectedAt = now,
            SecretKey = "trakt.connection.x.tokens",
        };
        context.WatchHistoryConnections.Add(connection);
        context.SaveChanges();
        _connectionId = connection.Id;
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }

    private sealed class StubRegistry(IWatchHistoryFavoritesProvider favorites) : IWatchHistoryProviderRegistry
    {
        public IReadOnlyList<WatchHistoryProviderDescriptor> Describe() => [];

        public IWatchHistoryProvider? Find(string providerKey) => null;

        public IWatchHistoryProviderAuthorization? FindAuthorization(string providerKey) => null;

        public IWatchHistoryFavoritesProvider? FindFavorites(string providerKey) => favorites;
    }

    private sealed class StubFavoritesProvider : IWatchHistoryFavoritesProvider
    {
        public WatchHistoryFailure? Failure { get; set; }

        public int NotFound { get; set; }

        public int? RemoteCountAfterWrite { get; set; }

        public string ProviderKey => "trakt";

        public Task<WatchHistoryResult<FavoritesSnapshot>> GetFavoritesAsync(int appUserId, CancellationToken cancellationToken) =>
            Task.FromResult(WatchHistoryResult<FavoritesSnapshot>.Success(new FavoritesSnapshot([], 0, 100)));

        public Task<WatchHistoryResult<FavoritesWriteResult>> AddFavoritesAsync(
            int appUserId, IReadOnlyCollection<FavoriteIdentity> identities, CancellationToken cancellationToken) => Write(identities);

        public Task<WatchHistoryResult<FavoritesWriteResult>> RemoveFavoritesAsync(
            int appUserId, IReadOnlyCollection<FavoriteIdentity> identities, CancellationToken cancellationToken) => Write(identities);

        private Task<WatchHistoryResult<FavoritesWriteResult>> Write(IReadOnlyCollection<FavoriteIdentity> identities) =>
            Task.FromResult(Failure is { } failure
                ? WatchHistoryResult<FavoritesWriteResult>.Failed(failure, "stub failure")
                : WatchHistoryResult<FavoritesWriteResult>.Success(
                    new FavoritesWriteResult(identities.Count - NotFound, 0, NotFound, RemoteCountAfterWrite, 100)));
    }
}
