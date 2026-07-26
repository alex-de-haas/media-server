using MediaServer.Api.Catalogs;
using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// A tombstone must be invisible to every library surface: it exists to preserve user data, not to
/// browse. These tests seed a ghost movie (unpublished, RemovedAt set, favorited, with a resume
/// position) beside a published one and assert the ghost surfaces nowhere — while the pieces that
/// deliberately know about ghosts (the watchlist unlink on delete) behave.
/// </summary>
public sealed class TombstoneLeakRegressionTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;
    private readonly LibraryReadService _library;

    private Guid _catalogId;
    private Guid _publishedId;
    private Guid _ghostId;
    private int _userId;

    public TombstoneLeakRegressionTests()
    {
        Seed();
        _context = _db.Create();
        var settings = new MediaServerSettings { SupportedLanguages = ["en-US"] };
        _library = new LibraryReadService(_context, new UserDataService(_context, TimeProvider.System), settings);
    }

    [Fact]
    public async Task The_library_list_and_recent_rail_skip_ghosts()
    {
        var list = await _library.ListAsync(catalogId: null, kind: null, appUserId: _userId, CancellationToken.None);
        Assert.Equal(_publishedId, Assert.Single(list).Id);

        var recent = await _library.GetRecentAsync(limit: 10, appUserId: _userId, CancellationToken.None);
        Assert.Equal(_publishedId, Assert.Single(recent).Id);
    }

    [Fact]
    public async Task The_resume_rail_skips_a_ghost_with_a_resume_position()
    {
        var resume = await _library.GetResumeAsync(_userId, limit: 10, CancellationToken.None);
        Assert.Empty(resume); // the only in-progress item is the ghost
    }

    [Fact]
    public async Task Detail_returns_null_for_a_ghost()
    {
        Assert.Null(await _library.GetDetailAsync(_ghostId, _userId, CancellationToken.None));
    }

    [Fact]
    public async Task Favorite_writes_by_internal_id_refuse_a_ghost()
    {
        // The ordinary favorite endpoint reaches published items only; the removed-titles surface has
        // its own targeted action for ghosts.
        var userData = new UserDataService(_context, TimeProvider.System);
        Assert.Null(await userData.SetFavoriteAsync(_userId, _ghostId, favorite: false, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_a_watchlisted_item_unlinks_its_tracked_title()
    {
        Guid titleId;
        await using (var seed = _db.Create())
        {
            var title = new TrackedTitle
            {
                Id = Guid.NewGuid(), Kind = MediaKind.Movie, IdentityProvider = "tmdb", IdentityProviderId = "27205",
                Title = "Inception", MediaItemId = _publishedId,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            };
            seed.TrackedTitles.Add(title);
            // Give the published movie signal so the delete tombstones instead of purging — the FK's
            // SetNull would mask a missing unlink on the tombstone path.
            seed.UserItemData.Add(new UserItemData
            {
                Id = Guid.NewGuid(), AppUserId = _userId, MediaItemId = _publishedId, IsFavorite = true,
            });
            await seed.SaveChangesAsync();
            titleId = title.Id;
        }

        var deleter = new LibraryDeleteService(_context, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));
        Assert.True(await deleter.DeleteAsync(_publishedId, deleteFiles: false, deleteUserData: false, CancellationToken.None));

        await using var verify = _db.Create();
        Assert.NotNull((await verify.MediaItems.SingleAsync(item => item.Id == _publishedId)).RemovedAt);
        Assert.Null((await verify.TrackedTitles.SingleAsync(title => title.Id == titleId)).MediaItemId);
    }

    private void Seed()
    {
        var now = DateTimeOffset.UtcNow;
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
        _catalogId = catalog.Id;
        context.Catalogs.Add(catalog);

        var published = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = "pub-1", CatalogId = catalog.Id, Kind = MediaKind.Movie,
            Title = "Inception", Year = 2010, IdentityProvider = "tmdb", IdentityProviderId = "27205",
            AddedAt = now, UpdatedAt = now,
        };
        var ghost = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = null, CatalogId = catalog.Id, Kind = MediaKind.Movie,
            Title = "Phantom", Year = 2015, IdentityProvider = "tmdb", IdentityProviderId = "99999",
            RemovedAt = now, AddedAt = now.AddDays(1), UpdatedAt = now,
        };
        _publishedId = published.Id;
        _ghostId = ghost.Id;
        context.MediaItems.AddRange(published, ghost);
        context.SaveChanges();

        _userId = user.Id;
        // The ghost carries the strongest possible signal: favorited AND mid-playback.
        context.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = ghost.Id,
            IsFavorite = true, PlaybackPositionTicks = 1000, LastPlayedDate = now,
        });
        context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }
}
