using MediaServer.Api.Data;
using MediaServer.Api.Recommendations.Profile;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Recommendations;

/// <summary>
/// The profile cache holds a stamp of everything the profile was built from, so nothing has to
/// remember to invalidate it. These tests are that claim: each input moves the stamp, and nothing
/// else does.
/// </summary>
/// <remarks>
/// Reference equality is the observation. A cache hit returns the very profile instance it stored, so
/// a changed reference means a rebuild happened and an unchanged one means it did not — without
/// needing to instrument the builder.
/// </remarks>
public sealed class TasteProfileCacheTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly MediaServerDbContext _database;
    private readonly TestTimeProvider _time = new(DateTimeOffset.Parse("2026-08-14T12:00:00Z"));
    private readonly TasteProfileCache _cache = new();
    private readonly int _userId;
    private readonly int _otherUserId;
    private readonly Guid _catalogId = Guid.NewGuid();

    private Guid _movieId;

    public TasteProfileCacheTests()
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

        _movieId = AddMovie("Arrival", "Science Fiction").Id;
        AddPlay(_movieId, _userId);
    }

    [Fact]
    public async Task AnUnchangedInstanceServesTheSameProfile()
    {
        Assert.Same(await Get(), await Get());
    }

    [Fact]
    public async Task RatingATitleRebuildsTheProfile()
    {
        var before = await Get();

        Rate(_movieId, 5);

        Assert.NotSame(before, await Get());
    }

    [Fact]
    public async Task AFavoriteRebuildsTheProfile()
    {
        var before = await Get();

        UpdateUserData(_movieId, row => row.IsFavorite = true);

        Assert.NotSame(before, await Get());
    }

    [Fact]
    public async Task APlayRebuildsTheProfile()
    {
        var before = await Get();

        AddPlay(AddMovie("Dune", "Science Fiction").Id, _userId);

        Assert.NotSame(before, await Get());
    }

    [Fact]
    public async Task AHideRebuildsTheProfile()
    {
        var before = await Get();

        _database.RecommendationHides.Add(new RecommendationHide
        {
            Id = Guid.NewGuid(), AppUserId = _userId,
            Kind = MediaServer.Api.Recommendations.RecommendationKind.Movie,
            TmdbId = "99", CreatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();

        Assert.NotSame(before, await Get());
    }

    [Fact]
    public async Task TrackingATitleRebuildsTheProfile()
    {
        var before = await Get();

        var tracked = new TrackedTitle
        {
            Id = Guid.NewGuid(), Kind = MediaKind.Movie, IdentityProvider = "tmdb", IdentityProviderId = "555",
            Title = "Tracked",
        };
        _database.TrackedTitles.Add(tracked);
        _database.WatchlistEntries.Add(new WatchlistEntry
        {
            Id = Guid.NewGuid(), AppUserId = _userId, TrackedTitleId = tracked.Id, CreatedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();

        Assert.NotSame(before, await Get());
    }

    [Fact]
    public async Task AnAddedLibraryTitleRebuildsEveryProfile()
    {
        // The library is the IDF denominator, so adding to it changes what every profile means — this
        // is the invalidation nothing else in the design would ever notice.
        var before = await Get();

        AddMovie("Something new", "Drama");

        Assert.NotSame(before, await Get());
    }

    [Fact]
    public async Task ReEnrichingATitleRebuildsTheProfile()
    {
        var before = await Get();

        var record = _database.MetadataRecords.Single(row => row.MediaItemId == _movieId);
        record.Genres = ["Drama"];
        record.FetchedAt = _time.GetUtcNow().AddMinutes(5);
        _database.SaveChanges();

        Assert.NotSame(before, await Get());
    }

    [Fact]
    public async Task SwappingOneHideForAnotherRebuildsTheProfile()
    {
        // The count is unchanged, and the facets underneath are completely different. A stamp built
        // from totals alone would serve the old profile until something unrelated happened to move.
        var first = new RecommendationHide
        {
            Id = Guid.NewGuid(), AppUserId = _userId,
            Kind = MediaServer.Api.Recommendations.RecommendationKind.Movie,
            TmdbId = "111", CreatedAt = _time.GetUtcNow(),
        };
        _database.RecommendationHides.Add(first);
        _database.SaveChanges();
        var before = await Get();

        _database.RecommendationHides.Remove(first);
        _database.RecommendationHides.Add(new RecommendationHide
        {
            Id = Guid.NewGuid(), AppUserId = _userId,
            Kind = MediaServer.Api.Recommendations.RecommendationKind.Movie,
            TmdbId = "222", CreatedAt = _time.GetUtcNow().AddMinutes(1),
        });
        _database.SaveChanges();

        Assert.NotSame(before, await Get());
    }

    [Fact]
    public async Task AnotherUsersActivityDoesNotRebuildThisProfile()
    {
        // Their plays are not an input to my profile, and rebuilding on them would make a busy second
        // account quietly cost the first one its cache.
        var before = await Get();

        AddPlay(_movieId, _otherUserId);

        Assert.Same(before, await Get());
    }

    [Fact]
    public async Task EachUserGetsTheirOwnProfile()
    {
        var mine = await Get();
        var theirs = await _cache.GetAsync(_otherUserId, _database, Builder(), CancellationToken.None);

        Assert.NotSame(mine, theirs);
        Assert.True(theirs.IsEmpty); // they have watched nothing
    }

    private Task<TasteProfile> Get() => _cache.GetAsync(_userId, _database, Builder(), CancellationToken.None);

    private TasteProfileBuilder Builder() =>
        new(_database, new TitleFacetReader(_database), new LibraryFacetIndexCache(), _time);

    private AppUser NewUser(string hostUserId, string email) => new()
    {
        HostUserId = hostUserId, Email = email, DisplayName = email, Role = AppUserRole.User,
        CreatedAt = _time.GetUtcNow(), LastSeenAt = _time.GetUtcNow(),
    };

    private MediaItem AddMovie(string title, string genre)
    {
        var item = new MediaItem
        {
            Id = Guid.NewGuid(), CatalogId = _catalogId, Kind = MediaKind.Movie, Title = title,
            Year = 2016, AddedAt = _time.GetUtcNow(), UpdatedAt = _time.GetUtcNow(),
        };
        _database.MediaItems.Add(item);
        _database.MetadataRecords.Add(new MetadataRecord
        {
            Id = Guid.NewGuid(), MediaItemId = item.Id, Provider = "tmdb", Language = "en-US",
            Genres = [genre], FetchedAt = _time.GetUtcNow(),
        });
        _database.SaveChanges();
        return item;
    }

    private void AddPlay(Guid itemId, int appUserId)
    {
        _database.PlaybackHistoryEntries.Add(new PlaybackHistoryEntry
        {
            Id = Guid.NewGuid(), AppUserId = appUserId, MediaItemId = itemId,
            CreatedAt = _time.GetUtcNow(), WatchedAt = _time.GetUtcNow(),
            Origin = PlaybackHistoryOrigin.LocalPlayback,
        });
        _database.SaveChanges();
    }

    private void Rate(Guid itemId, int stars) => UpdateUserData(itemId, row => row.Rating = stars);

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

    public void Dispose()
    {
        _database.Dispose();
        _connection.Dispose();
    }
}
