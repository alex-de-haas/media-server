using MediaServer.Api.Data;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.WatchHistory;

/// <summary>
/// Two-way favorites reconciliation. The decisions are three-way — local now, remote now, and what the
/// last reconciliation recorded — because without that memory "favorited here, absent there" cannot be
/// told from "unfavorited there, still flagged here".
/// </summary>
public sealed class FavoritesSyncTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-26T12:00:00Z"));
    private readonly StubFavoritesProvider _provider = new();

    private int _userId;
    private Guid _connectionId;
    private Guid _movieId;
    private Guid _ghostId;

    public FavoritesSyncTests()
    {
        Seed();
        _context = _db.Create();
    }

    private FavoritesSyncService Service() => new(
        _context, new StubRegistry(_provider), _time, NullLogger<FavoritesSyncService>.Instance);

    [Fact]
    public async Task A_new_local_favorite_is_planned_for_the_provider()
    {
        await FavoriteAsync(_movieId);

        var plan = await Service().PreviewAsync(_userId, "trakt", CancellationToken.None);

        Assert.True(plan.Succeeded);
        var entry = Assert.Single(plan.Value!.Entries);
        Assert.Equal(FavoriteSyncAction.AddRemotely, entry.Action);
        Assert.Equal("Inception", entry.Title);
        // A preview writes nothing.
        await using var verify = _db.Create();
        Assert.Empty(await verify.WatchHistoryOutboxEvents.ToListAsync());
    }

    [Fact]
    public async Task Applying_queues_the_outbound_favorite_and_remembers_the_state()
    {
        await FavoriteAsync(_movieId);

        Assert.True((await Service().ApplyAsync(_userId, "trakt", CancellationToken.None)).Succeeded);

        await using var verify = _db.Create();
        var queued = Assert.Single(await verify.WatchHistoryOutboxEvents.ToListAsync());
        Assert.Equal(WatchHistoryOutboxOperation.AddFavorite, queued.Operation);
        var state = Assert.Single(await verify.WatchHistoryFavoriteStates.ToListAsync());
        Assert.True(state.LocalFavorite);
        Assert.Equal("27205", state.IdentityProviderId);
    }

    [Fact]
    public async Task A_favorite_added_at_the_provider_arrives_locally()
    {
        _provider.Favorites.Add(new ProviderFavorite(new FavoriteIdentity(FavoriteWorkKind.Movie, 27205, null), _time.GetUtcNow()));

        var plan = await Service().ApplyAsync(_userId, "trakt", CancellationToken.None);

        Assert.True(plan.Succeeded);
        Assert.Equal(FavoriteSyncAction.AddLocally, Assert.Single(plan.Value!.Entries).Action);
        await using var verify = _db.Create();
        Assert.True(await verify.UserItemData.AnyAsync(data => data.MediaItemId == _movieId && data.IsFavorite));
        Assert.Empty(await verify.WatchHistoryOutboxEvents.ToListAsync()); // nothing to send back
    }

    [Fact]
    public async Task A_favorite_removed_at_the_provider_is_cleared_locally()
    {
        // Both sides agreed last time; the provider has since dropped it.
        await FavoriteAsync(_movieId);
        await SeedStateAsync(remotePresent: true, localFavorite: true);

        var plan = await Service().ApplyAsync(_userId, "trakt", CancellationToken.None);

        Assert.Equal(FavoriteSyncAction.RemoveLocally, Assert.Single(plan.Value!.Entries).Action);
        await using var verify = _db.Create();
        Assert.False(await verify.UserItemData.AnyAsync(data => data.MediaItemId == _movieId && data.IsFavorite));
    }

    [Fact]
    public async Task A_favorite_cleared_locally_is_removed_at_the_provider()
    {
        // The provider still holds it, and the memory says this library did too — so the local clear is
        // the new fact and must travel, rather than the remote copy flowing back in.
        _provider.Favorites.Add(new ProviderFavorite(new FavoriteIdentity(FavoriteWorkKind.Movie, 27205, null), _time.GetUtcNow()));
        await SeedStateAsync(remotePresent: true, localFavorite: true);

        var plan = await Service().ApplyAsync(_userId, "trakt", CancellationToken.None);

        Assert.Equal(FavoriteSyncAction.RemoveRemotely, Assert.Single(plan.Value!.Entries).Action);
        await using var verify = _db.Create();
        var queued = Assert.Single(await verify.WatchHistoryOutboxEvents.ToListAsync());
        Assert.Equal(WatchHistoryOutboxOperation.RemoveFavorite, queued.Operation);
    }

    [Fact]
    public async Task An_inbound_favorite_lands_on_a_tombstone()
    {
        // Deleted but remembered: the ghost keeps the favorite, and a re-download brings it back visible.
        _provider.Favorites.Add(new ProviderFavorite(new FavoriteIdentity(FavoriteWorkKind.Movie, 99999, null), _time.GetUtcNow()));

        var plan = await Service().ApplyAsync(_userId, "trakt", CancellationToken.None);

        Assert.Equal(FavoriteSyncAction.AddLocally, Assert.Single(plan.Value!.Entries).Action);
        await using var verify = _db.Create();
        Assert.True(await verify.UserItemData.AnyAsync(data => data.MediaItemId == _ghostId && data.IsFavorite));
    }

    [Fact]
    public async Task A_remote_favorite_this_library_lacks_is_reported_rather_than_dropped()
    {
        _provider.Favorites.Add(new ProviderFavorite(new FavoriteIdentity(FavoriteWorkKind.Movie, 12345, null), _time.GetUtcNow()));

        var plan = await Service().PreviewAsync(_userId, "trakt", CancellationToken.None);

        var entry = Assert.Single(plan.Value!.Entries);
        Assert.Equal(FavoriteSyncAction.SkippedNotInLibrary, entry.Action);
        Assert.Equal(1, plan.Value!.Counts[FavoriteSyncAction.SkippedNotInLibrary]);
    }

    [Fact]
    public async Task A_second_preview_before_delivery_does_not_propose_undoing_the_favorite()
    {
        // Apply only *queues* the outbound add. If the remembered remote state claimed the write had
        // already landed, the next comparison would read the unchanged provider as a fresh remote
        // removal — and offer to clear the favorite the user just set.
        await FavoriteAsync(_movieId);
        Assert.True((await Service().ApplyAsync(_userId, "trakt", CancellationToken.None)).Succeeded);

        var again = await Service().PreviewAsync(_userId, "trakt", CancellationToken.None);

        Assert.Equal(FavoriteSyncAction.AddRemotely, Assert.Single(again.Value!.Entries).Action);
        await using var verify = _db.Create();
        Assert.True(await verify.UserItemData.AnyAsync(data => data.MediaItemId == _movieId && data.IsFavorite));
    }

    [Fact]
    public async Task The_plan_reports_how_full_the_remote_list_is()
    {
        _provider.RemoteCount = 97;

        var plan = await Service().PreviewAsync(_userId, "trakt", CancellationToken.None);

        Assert.Equal(97, plan.Value!.RemoteCount);
        Assert.Equal(100, plan.Value!.Capacity);
    }

    private async Task FavoriteAsync(Guid mediaItemId)
    {
        await using var seed = _db.Create();
        seed.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = mediaItemId, IsFavorite = true,
        });
        await seed.SaveChangesAsync();
    }

    private async Task SeedStateAsync(bool remotePresent, bool localFavorite)
    {
        await using var seed = _db.Create();
        seed.WatchHistoryFavoriteStates.Add(new WatchHistoryFavoriteState
        {
            Id = Guid.NewGuid(), ConnectionId = _connectionId, Kind = MediaKind.Movie,
            IdentityProvider = "tmdb", IdentityProviderId = "27205",
            RemotePresent = remotePresent, LocalFavorite = localFavorite, ReconciledAt = _time.GetUtcNow(),
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
        var ghost = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = null, CatalogId = catalog.Id, Kind = MediaKind.Movie,
            Title = "Phantom", Year = 2015, IdentityProvider = "tmdb", IdentityProviderId = "99999",
            RemovedAt = now, AddedAt = now, UpdatedAt = now,
        };
        context.MediaItems.AddRange(movie, ghost);
        context.SaveChanges();

        _userId = user.Id;
        _movieId = movie.Id;
        _ghostId = ghost.Id;

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
        public List<ProviderFavorite> Favorites { get; } = [];

        public int? RemoteCount { get; set; }

        public string ProviderKey => "trakt";

        public Task<WatchHistoryResult<FavoritesSnapshot>> GetFavoritesAsync(int appUserId, CancellationToken cancellationToken) =>
            Task.FromResult(WatchHistoryResult<FavoritesSnapshot>.Success(
                new FavoritesSnapshot(Favorites, RemoteCount ?? Favorites.Count, 100)));

        public Task<WatchHistoryResult<FavoritesWriteResult>> AddFavoritesAsync(
            int appUserId, IReadOnlyCollection<FavoriteIdentity> identities, CancellationToken cancellationToken) =>
            Task.FromResult(WatchHistoryResult<FavoritesWriteResult>.Success(new FavoritesWriteResult(identities.Count, 0, 0, null)));

        public Task<WatchHistoryResult<FavoritesWriteResult>> RemoveFavoritesAsync(
            int appUserId, IReadOnlyCollection<FavoriteIdentity> identities, CancellationToken cancellationToken) =>
            Task.FromResult(WatchHistoryResult<FavoritesWriteResult>.Success(new FavoritesWriteResult(identities.Count, 0, 0, null)));
    }
}
