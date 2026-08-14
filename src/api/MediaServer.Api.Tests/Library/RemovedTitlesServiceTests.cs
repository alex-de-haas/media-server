using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Tests.Jellyfin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Library;

/// <summary>
/// The removed-titles surface: tombstoned movies/series listed with the signed-in user's signal
/// summary (favorite, plays across the ghost subtree, last watched), favorite clearing on ghosts,
/// and the retroactive permanent purge.
/// </summary>
public sealed class RemovedTitlesServiceTests : IDisposable
{
    private readonly JellyfinDatabase _db = new();
    private readonly MediaServerDbContext _context;

    private Guid _catalogId;
    private Guid _ghostMovieId;
    private Guid _ghostSeriesId;
    private Guid _ghostEpisodeId;
    private Guid _publishedMovieId;
    private int _userId;

    public RemovedTitlesServiceTests()
    {
        Seed();
        _context = _db.Create();
    }

    [Fact]
    public async Task List_returns_only_tombstoned_top_level_titles_with_signal_summary()
    {
        var titles = await new RemovedTitlesService(_context).ListAsync(_userId, CancellationToken.None);

        Assert.Equal(2, titles.Count); // the ghost movie and the ghost series; never the published movie
        var movie = Assert.Single(titles, title => title.Id == _ghostMovieId);
        Assert.True(movie.IsFavorite);
        Assert.Equal(0, movie.PlayCount);
        Assert.Equal("https://cdn/phantom.jpg", movie.PosterUrl);

        // The series aggregates its ghost episode's plays — and its favorite — up to the title: the
        // episode favorite is what kept this chain alive, so the entry must show it.
        var series = Assert.Single(titles, title => title.Id == _ghostSeriesId);
        Assert.True(series.IsFavorite);
        Assert.Equal(2, series.PlayCount);
        Assert.NotNull(series.LastWatchedAt);
    }

    [Fact]
    public async Task Clear_favorite_works_on_a_ghost_and_only_on_a_ghost()
    {
        var service = new RemovedTitlesService(_context);

        Assert.True(await service.ClearFavoriteAsync(_userId, _ghostMovieId, CancellationToken.None));
        Assert.False(await service.ClearFavoriteAsync(_userId, _ghostMovieId, CancellationToken.None)); // nothing left to clear
        // A published item is the ordinary favorite endpoint's business, not this surface's.
        Assert.False(await service.ClearFavoriteAsync(_userId, _publishedMovieId, CancellationToken.None));

        await using var verify = _db.Create();
        Assert.False(await verify.UserItemData.AnyAsync(data => data.MediaItemId == _ghostMovieId && data.IsFavorite));
    }

    [Fact]
    public async Task Clear_favorite_on_a_series_clears_descendant_favorites_too()
    {
        // The favorite sits on the ghost episode, not the series row — clearing at the title level
        // must reach it, or the flag would be permanently stuck (the ordinary endpoint refuses ghosts).
        var service = new RemovedTitlesService(_context);

        Assert.True(await service.ClearFavoriteAsync(_userId, _ghostSeriesId, CancellationToken.None));

        await using var verify = _db.Create();
        Assert.False(await verify.UserItemData.AnyAsync(data => data.MediaItemId == _ghostEpisodeId && data.IsFavorite));
        Assert.False((await new RemovedTitlesService(verify).ListAsync(_userId, CancellationToken.None))
            .Single(title => title.Id == _ghostSeriesId).IsFavorite);
    }

    [Fact]
    public async Task Clearing_a_ghost_favorite_leaves_its_rating_alone()
    {
        // Deleting a file does not retract a verdict on a film that was watched, so the two clears are
        // separate gestures. Folding them together would silently discard the judgement.
        var service = new RemovedTitlesService(_context);

        Assert.True(await service.ClearFavoriteAsync(_userId, _ghostMovieId, CancellationToken.None));

        await using var verify = _db.Create();
        Assert.Equal(4, (await new RemovedTitlesService(verify).ListAsync(_userId, CancellationToken.None))
            .Single(title => title.Id == _ghostMovieId).UserRating);
    }

    [Fact]
    public async Task Clear_rating_works_on_a_ghost_and_only_on_a_ghost()
    {
        var service = new RemovedTitlesService(_context);

        Assert.True(await service.ClearRatingAsync(_userId, _ghostMovieId, CancellationToken.None));
        Assert.False(await service.ClearRatingAsync(_userId, _ghostMovieId, CancellationToken.None)); // nothing left
        Assert.False(await service.ClearRatingAsync(_userId, _publishedMovieId, CancellationToken.None));

        await using var verify = _db.Create();
        var title = (await new RemovedTitlesService(verify).ListAsync(_userId, CancellationToken.None))
            .Single(entry => entry.Id == _ghostMovieId);
        Assert.Null(title.UserRating);
        Assert.True(title.IsFavorite); // the favorite is a separate statement and survives
    }

    [Fact]
    public async Task Purge_removes_the_ghost_subtree_and_all_its_user_data()
    {
        var deleter = new LibraryDeleteService(_context, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));

        Assert.True(await deleter.PurgeRemovedAsync(_ghostSeriesId, CancellationToken.None));

        await using var verify = _db.Create();
        Assert.False(await verify.MediaItems.AnyAsync(item => item.Id == _ghostSeriesId || item.Id == _ghostEpisodeId));
        Assert.False(await verify.PlaybackHistoryEntries.AnyAsync(entry => entry.MediaItemId == _ghostEpisodeId));
        // The other ghost and the published movie are untouched.
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == _ghostMovieId));
        Assert.True(await verify.MediaItems.AnyAsync(item => item.Id == _publishedMovieId));
    }

    [Fact]
    public async Task Purge_refuses_published_items_and_unknown_ids()
    {
        var deleter = new LibraryDeleteService(_context, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));

        Assert.False(await deleter.PurgeRemovedAsync(_publishedMovieId, CancellationToken.None));
        Assert.False(await deleter.PurgeRemovedAsync(Guid.NewGuid(), CancellationToken.None));
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
            Title = "Inception", Year = 2010, AddedAt = now, UpdatedAt = now,
        };
        var ghostMovie = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = null, CatalogId = null, Kind = MediaKind.Movie,
            Title = "Phantom", Year = 2015, RemovedAt = now, AddedAt = now, UpdatedAt = now,
        };
        var ghostSeries = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = null, CatalogId = catalog.Id, Kind = MediaKind.Series,
            Title = "Gone Show", Year = 2018, RemovedAt = now, AddedAt = now, UpdatedAt = now,
        };
        var ghostSeason = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = null, CatalogId = catalog.Id, Kind = MediaKind.Season,
            Title = "Season 1", ParentId = ghostSeries.Id, SeriesId = ghostSeries.Id, IndexNumber = 1,
            RemovedAt = now, AddedAt = now, UpdatedAt = now,
        };
        var ghostEpisode = new MediaItem
        {
            Id = Guid.NewGuid(), PublicId = null, CatalogId = catalog.Id, Kind = MediaKind.Episode,
            Title = "Pilot", ParentId = ghostSeason.Id, SeriesId = ghostSeries.Id, SeasonId = ghostSeason.Id,
            ParentIndexNumber = 1, IndexNumber = 1, RemovedAt = now, AddedAt = now, UpdatedAt = now,
        };
        _publishedMovieId = published.Id;
        _ghostMovieId = ghostMovie.Id;
        _ghostSeriesId = ghostSeries.Id;
        _ghostEpisodeId = ghostEpisode.Id;
        context.MediaItems.AddRange(published, ghostMovie, ghostSeries, ghostSeason, ghostEpisode);
        context.ImageAssets.Add(new ImageAsset
        {
            Id = Guid.NewGuid(), MediaItemId = ghostMovie.Id, ImageType = ImageType.Primary,
            Provider = "tmdb", RemotePath = "https://cdn/phantom.jpg", Tag = "tag-1",
        });
        context.SaveChanges();

        _userId = user.Id;
        context.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = ghostMovie.Id, IsFavorite = true, Rating = 4,
        });
        context.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = published.Id, IsFavorite = true,
        });
        // The series' favorite sits on its ghost episode — the case a root-only lookup would miss.
        context.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = ghostEpisode.Id, IsFavorite = true,
        });
        context.PlaybackHistoryEntries.AddRange(
            new PlaybackHistoryEntry
            {
                Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = ghostEpisode.Id, CreatedAt = now,
                WatchedAt = now.AddDays(-2), Origin = PlaybackHistoryOrigin.LocalPlayback,
            },
            new PlaybackHistoryEntry
            {
                Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = ghostEpisode.Id, CreatedAt = now,
                WatchedAt = now.AddDays(-1), Origin = PlaybackHistoryOrigin.LocalPlayback,
            });
        context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
    }
}
