using MediaServer.Api.Configuration;
using MediaServer.Api.Data;
using MediaServer.Api.Watchlist;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MediaServer.Api.Tests.Watchlist;

/// <summary>
/// Wishlist ↔ library linking: identity match on add, reconcile on a simulated publish, unlink on a
/// library delete (FK SetNull), and the owned/aired/missing-aired gap that refreshes when either the
/// library or the schedule changes.
/// </summary>
public sealed class WatchlistLibraryLinkerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 7, 14);

    private readonly SqliteConnection _connection = WatchlistTestData.OpenDatabase();
    private readonly FixedTimeProvider _clock = new(Now);

    public WatchlistLibraryLinkerTests()
    {
        using var database = WatchlistTestData.NewContext(_connection);
        database.Database.Migrate();
    }

    [Fact]
    public async Task Add_links_a_title_that_is_already_in_the_library()
    {
        int userId;
        Guid mediaItemId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            userId = WatchlistTestData.SeedUser(database).Id;
            mediaItemId = SeedLibraryMovie(database, "27205").Id;
        }

        using var context = WatchlistTestData.NewContext(_connection);
        var service = new WatchlistService(
            context, new MediaServerSettings(), new RecordingSyncQueue(), new WatchlistLibraryLinker(context, _clock), _clock);
        var result = await service.AddAsync(
            userId,
            new AddWatchlistRequest(new ProviderRefBody("tmdb", "27205"), MediaKind.Movie, null, null, null, null, "Inception", 2010, null),
            CancellationToken.None);

        Assert.True(result.Item!.InLibrary);
        Assert.Equal(mediaItemId, result.Item.LibraryItemId);
    }

    [Fact]
    public async Task Reconcile_links_on_publish_and_a_library_delete_unlinks()
    {
        Guid titleId;
        Guid mediaItemId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            titleId = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Inception").Id;
        }

        // Nothing published yet: reconcile is a no-op.
        Assert.Equal(0, await ReconcileAsync());

        // The pipeline publishes the identity → reconcile links the wishlist title.
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            mediaItemId = SeedLibraryMovie(database, "27205").Id;
        }

        Assert.Equal(1, await ReconcileAsync());
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            Assert.Equal(mediaItemId, (await database.TrackedTitles.SingleAsync(title => title.Id == titleId)).MediaItemId);
        }

        // Deleting the library item unlinks back to wishlist state (SetNull), no reconcile needed.
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            database.MediaItems.Remove(await database.MediaItems.SingleAsync(item => item.Id == mediaItemId));
            await database.SaveChangesAsync();
            Assert.Null((await database.TrackedTitles.SingleAsync(title => title.Id == titleId)).MediaItemId);
        }
    }

    [Fact]
    public async Task Reconcile_ignores_unpublished_and_mismatched_items()
    {
        Guid titleId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            titleId = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Inception").Id;
            SeedLibraryMovie(database, "27205", publicId: null); // mid-pipeline, not published
            SeedLibraryMovie(database, "99999"); // different identity
        }

        Assert.Equal(0, await ReconcileAsync());
        using var fresh = WatchlistTestData.NewContext(_connection);
        Assert.Null((await fresh.TrackedTitles.SingleAsync(title => title.Id == titleId)).MediaItemId);
    }

    [Fact]
    public async Task Gap_projects_owned_aired_and_missing_and_refreshes_with_both_sides()
    {
        Guid titleId;
        Guid seriesId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var series = SeedLibrarySeries(database, "1396", ownedEpisodes: [(1, 1), (1, 2)]);
            seriesId = series.Id;
            var title = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396", "Slow Horses");
            title.MediaItemId = series.Id;
            database.SaveChanges();
            // Aired per schedule: S1E1..E3 (E3 aired but unowned), S1E4 airs tomorrow.
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today.AddDays(-14), season: 1, episode: 1);
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today.AddDays(-7), season: 1, episode: 2);
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today.AddDays(-1), season: 1, episode: 3);
            WatchlistTestData.SeedRelease(database, title, ReleaseType.EpisodeAir, Today.AddDays(1), season: 1, episode: 4);
            titleId = title.Id;
        }

        var gap = await ComputeGapAsync(titleId);
        Assert.Equal(new LibraryGapDto(OwnedEpisodes: 2, AiredEpisodes: 3, MissingAired: 1), gap); // "behind by 1"

        // The library catches up (E3 published) → the projection follows without any stored state.
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            SeedLibraryEpisode(database, seriesId, season: 1, episode: 3);
        }

        gap = await ComputeGapAsync(titleId);
        Assert.Equal(new LibraryGapDto(3, 3, 0), gap);

        // The schedule side moves (E4 airs) → aired grows, missing follows.
        _clock.Now = Now.AddDays(2);
        gap = await ComputeGapAsync(titleId);
        Assert.Equal(new LibraryGapDto(3, 4, 1), gap);
    }

    [Fact]
    public async Task Gap_is_null_for_movies_and_unlinked_titles()
    {
        Guid movieTitleId;
        Guid unlinkedId;
        using (var database = WatchlistTestData.NewContext(_connection))
        {
            var movie = WatchlistTestData.SeedTitle(database, MediaKind.Movie, "27205", "Inception");
            movie.MediaItemId = SeedLibraryMovie(database, "27205").Id;
            database.SaveChanges();
            movieTitleId = movie.Id;
            unlinkedId = WatchlistTestData.SeedTitle(database, MediaKind.Series, "1396", "Slow Horses").Id;
        }

        Assert.Null(await ComputeGapAsync(movieTitleId));
        Assert.Null(await ComputeGapAsync(unlinkedId));
    }

    private async Task<int> ReconcileAsync()
    {
        using var database = WatchlistTestData.NewContext(_connection);
        return await new WatchlistLibraryLinker(database, _clock).ReconcileAsync(CancellationToken.None);
    }

    private async Task<LibraryGapDto?> ComputeGapAsync(Guid titleId)
    {
        using var database = WatchlistTestData.NewContext(_connection);
        var title = await database.TrackedTitles.Include(candidate => candidate.Releases)
            .SingleAsync(candidate => candidate.Id == titleId);
        return await new WatchlistLibraryLinker(database, _clock).ComputeGapAsync(title, CancellationToken.None);
    }

    private static MediaItem SeedLibraryMovie(MediaServerDbContext database, string tmdbId, string? publicId = "pub")
    {
        var catalog = database.Catalogs.FirstOrDefault();
        if (catalog is null)
        {
            catalog = new Catalog
            {
                Id = Guid.NewGuid(),
                Name = "Movies",
                Type = CatalogType.Movie,
                Root = Path.Combine(Path.GetTempPath(), "ms-linker-" + Guid.NewGuid().ToString("N")),
                CreatedAt = Now,
                UpdatedAt = Now,
            };
            database.Catalogs.Add(catalog);
        }

        var item = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = publicId is null ? null : publicId + Guid.NewGuid().ToString("N"),
            CatalogId = catalog.Id,
            Kind = MediaKind.Movie,
            Title = "Movie " + tmdbId,
            IdentityProvider = "tmdb",
            IdentityProviderId = tmdbId,
            AddedAt = Now,
            UpdatedAt = Now,
        };
        database.MediaItems.Add(item);
        database.SaveChanges();
        return item;
    }

    private static MediaItem SeedLibrarySeries(
        MediaServerDbContext database, string tmdbId, IReadOnlyList<(int Season, int Episode)> ownedEpisodes)
    {
        var catalog = new Catalog
        {
            Id = Guid.NewGuid(),
            Name = "Series",
            Type = CatalogType.Series,
            Root = Path.Combine(Path.GetTempPath(), "ms-linker-" + Guid.NewGuid().ToString("N")),
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        database.Catalogs.Add(catalog);

        var series = new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = "series-" + tmdbId,
            CatalogId = catalog.Id,
            Kind = MediaKind.Series,
            Title = "Series " + tmdbId,
            IdentityProvider = "tmdb",
            IdentityProviderId = tmdbId,
            AddedAt = Now,
            UpdatedAt = Now,
        };
        database.MediaItems.Add(series);
        database.SaveChanges();

        foreach (var (season, episode) in ownedEpisodes)
        {
            SeedLibraryEpisode(database, series.Id, season, episode);
        }

        return series;
    }

    private static void SeedLibraryEpisode(MediaServerDbContext database, Guid seriesId, int season, int episode)
    {
        var series = database.MediaItems.Single(item => item.Id == seriesId);
        database.MediaItems.Add(new MediaItem
        {
            Id = Guid.NewGuid(),
            PublicId = $"ep-{seriesId:N}-{season}-{episode}",
            CatalogId = series.CatalogId,
            Kind = MediaKind.Episode,
            SeriesId = seriesId,
            Title = $"S{season}E{episode}",
            IdentitySeasonNumber = season,
            IdentityEpisodeNumber = episode,
            AddedAt = Now,
            UpdatedAt = Now,
        });
        database.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();
}
