using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using MediaServer.Api.WatchHistory;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.WatchHistory;

/// <summary>
/// Outbound favorites: an explicit favorite or unfavorite of a movie or series queues work for the
/// connected provider, and nothing else ever does. The delivery half — including Trakt's full-list 420
/// — is covered by <see cref="FavoritesDeliveryTests"/>.
/// </summary>
public sealed class FavoritesPushTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-07-26T12:00:00Z"));

    private int _userId;
    private Guid _connectionId;
    private Guid _movieId;
    private Guid _seriesId;
    private Guid _episodeId;

    public FavoritesPushTests()
    {
        Seed();
        _context = _db.Create();
    }

    private UserDataService UserData(bool favoritesProvider = true) => new(
        _context,
        _time,
        watchHistory: null,
        logger: null,
        favorites: new FavoritesRecorder(
            _context,
            new StubRegistry(favoritesProvider ? new StubFavoritesProvider() : null),
            _time,
            NullLogger<FavoritesRecorder>.Instance));

    [Fact]
    public async Task Favoriting_a_movie_queues_an_add()
    {
        Assert.NotNull(await UserData().SetFavoriteAsync(_userId, _movieId, favorite: true, CancellationToken.None));

        await using var verify = _db.Create();
        var queued = Assert.Single(await verify.WatchHistoryOutboxEvents.ToListAsync());
        Assert.Equal(WatchHistoryOutboxOperation.AddFavorite, queued.Operation);
        Assert.Equal(_movieId, queued.MediaItemId);
        var identity = FavoritesRecorder.Deserialize(queued.IdentitySnapshot);
        Assert.NotNull(identity);
        Assert.Equal(FavoriteWorkKind.Movie, identity.Kind);
        Assert.Equal(27205, identity.TmdbId);
    }

    [Fact]
    public async Task Unfavoriting_queues_a_removal_and_re_favoriting_supersedes_it()
    {
        var service = UserData();
        await service.SetFavoriteAsync(_userId, _movieId, favorite: true, CancellationToken.None);
        await service.SetFavoriteAsync(_userId, _movieId, favorite: false, CancellationToken.None);

        await using (var verify = _db.Create())
        {
            // The undelivered add is gone: sending both would put the work on Trakt and take it off
            // again, in whichever order the worker happened to pick them up.
            var queued = Assert.Single(await verify.WatchHistoryOutboxEvents.ToListAsync());
            Assert.Equal(WatchHistoryOutboxOperation.RemoveFavorite, queued.Operation);
        }

        await service.SetFavoriteAsync(_userId, _movieId, favorite: true, CancellationToken.None);

        await using var final = _db.Create();
        var last = Assert.Single(await final.WatchHistoryOutboxEvents.ToListAsync());
        Assert.Equal(WatchHistoryOutboxOperation.AddFavorite, last.Operation);
    }

    [Fact]
    public async Task Re_favoriting_something_already_favorited_queues_nothing()
    {
        var service = UserData();
        await service.SetFavoriteAsync(_userId, _movieId, favorite: true, CancellationToken.None);
        await using (var seed = _db.Create())
        {
            await seed.WatchHistoryOutboxEvents.ExecuteDeleteAsync();
        }

        // No transition, no statement: the flag was already true.
        await service.SetFavoriteAsync(_userId, _movieId, favorite: true, CancellationToken.None);

        await using var verify = _db.Create();
        Assert.Empty(await verify.WatchHistoryOutboxEvents.ToListAsync());
    }

    [Fact]
    public async Task Favoriting_a_series_queues_the_series_itself()
    {
        await UserData().SetFavoriteAsync(_userId, _seriesId, favorite: true, CancellationToken.None);

        await using var verify = _db.Create();
        var queued = Assert.Single(await verify.WatchHistoryOutboxEvents.ToListAsync());
        var identity = FavoritesRecorder.Deserialize(queued.IdentitySnapshot);
        Assert.Equal(FavoriteWorkKind.Series, identity!.Kind);
        Assert.Equal(1396, identity.TmdbId);
    }

    [Fact]
    public async Task Favoriting_an_episode_stays_local()
    {
        // Trakt holds favorites for works only. An episode favorite is a perfectly good local flag and
        // must not be approximated by favoriting its whole series.
        await UserData().SetFavoriteAsync(_userId, _episodeId, favorite: true, CancellationToken.None);

        await using var verify = _db.Create();
        Assert.Empty(await verify.WatchHistoryOutboxEvents.ToListAsync());
        Assert.True(await verify.UserItemData.AnyAsync(data => data.MediaItemId == _episodeId && data.IsFavorite));
    }

    [Fact]
    public async Task Nothing_is_queued_without_a_favorites_capable_connection()
    {
        await UserData(favoritesProvider: false).SetFavoriteAsync(_userId, _movieId, favorite: true, CancellationToken.None);

        await using var verify = _db.Create();
        Assert.Empty(await verify.WatchHistoryOutboxEvents.ToListAsync());
    }

    [Fact]
    public async Task Unfavoriting_one_copy_of_a_duplicated_work_keeps_the_remote_favorite()
    {
        // Until the single-catalog audit repairs a pre-existing pair, one work can be two rows. The
        // provider holds one favorite for it, so it only goes away when the last copy is cleared.
        Guid secondCopyId;
        await using (var seed = _db.Create())
        {
            var copy = new MediaItem
            {
                Id = Guid.NewGuid(), PublicId = "pub-copy", CatalogId = await seed.Catalogs.Select(c => c.Id).FirstAsync(),
                Kind = MediaKind.Movie, Title = "Inception", Year = 2010,
                IdentityProvider = "tmdb", IdentityProviderId = "27205",
                AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
            };
            seed.MediaItems.Add(copy);
            await seed.SaveChangesAsync();
            secondCopyId = copy.Id;
        }

        var service = UserData();
        await service.SetFavoriteAsync(_userId, _movieId, favorite: true, CancellationToken.None);
        await service.SetFavoriteAsync(_userId, secondCopyId, favorite: true, CancellationToken.None);
        await using (var seed = _db.Create())
        {
            await seed.WatchHistoryOutboxEvents.ExecuteDeleteAsync();
        }

        await service.SetFavoriteAsync(_userId, _movieId, favorite: false, CancellationToken.None);
        await using (var verify = _db.Create())
        {
            Assert.Empty(await verify.WatchHistoryOutboxEvents.ToListAsync()); // the twin still carries it
        }

        await service.SetFavoriteAsync(_userId, secondCopyId, favorite: false, CancellationToken.None);
        await using var final = _db.Create();
        var queued = Assert.Single(await final.WatchHistoryOutboxEvents.ToListAsync());
        Assert.Equal(WatchHistoryOutboxOperation.RemoveFavorite, queued.Operation);
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
        var series = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = "pub-series", CatalogId = catalog.Id, Kind = MediaKind.Series,
            Title = "Breaking Bad", Year = 2008, IdentityProvider = "tmdb", IdentityProviderId = "1396",
            AddedAt = now, UpdatedAt = now,
        };
        var episode = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = "pub-episode", CatalogId = catalog.Id, Kind = MediaKind.Episode,
            Title = "Pilot", ParentId = series.Id, SeriesId = series.Id, ParentIndexNumber = 1, IndexNumber = 1,
            IdentityProvider = "tmdb", IdentityProviderId = "1396",
            IdentitySeasonNumber = 1, IdentityEpisodeNumber = 1,
            AddedAt = now, UpdatedAt = now,
        };
        context.MediaItems.AddRange(movie, series, episode);
        context.SaveChanges();

        _userId = user.Id;
        _movieId = movie.Id;
        _seriesId = series.Id;
        _episodeId = episode.Id;

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

    private sealed class StubRegistry(IWatchHistoryFavoritesProvider? favorites) : IWatchHistoryProviderRegistry
    {
        public IReadOnlyList<WatchHistoryProviderDescriptor> Describe() => [];

        public IWatchHistoryProvider? Find(string providerKey) => null;

        public IWatchHistoryProviderAuthorization? FindAuthorization(string providerKey) => null;

        public IWatchHistoryFavoritesProvider? FindFavorites(string providerKey) => favorites;
    }

    private sealed class StubFavoritesProvider : IWatchHistoryFavoritesProvider
    {
        public string ProviderKey => "trakt";

        public Task<WatchHistoryResult<FavoritesSnapshot>> GetFavoritesAsync(int appUserId, CancellationToken cancellationToken) =>
            Task.FromResult(WatchHistoryResult<FavoritesSnapshot>.Success(new FavoritesSnapshot([], 0, 100)));

        public Task<WatchHistoryResult<FavoritesWriteResult>> AddFavoritesAsync(
            int appUserId, IReadOnlyCollection<FavoriteIdentity> identities, CancellationToken cancellationToken) =>
            Task.FromResult(WatchHistoryResult<FavoritesWriteResult>.Success(new FavoritesWriteResult(identities.Count, 0, 0, null)));

        public Task<WatchHistoryResult<FavoritesWriteResult>> RemoveFavoritesAsync(
            int appUserId, IReadOnlyCollection<FavoriteIdentity> identities, CancellationToken cancellationToken) =>
            Task.FromResult(WatchHistoryResult<FavoritesWriteResult>.Success(new FavoritesWriteResult(identities.Count, 0, 0, null)));
    }
}
