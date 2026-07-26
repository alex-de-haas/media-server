using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Library;
using MediaServer.Api.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// End-to-end coverage for tombstone adoption: a deleted-but-remembered title that is downloaded
/// again flows through the real pipeline and comes back as the <b>same</b> item — same internal id,
/// same user data — in the same catalog under the same public id, or re-homed into a new catalog
/// under a fresh one.
/// </summary>
public sealed class TombstoneRevivalTests
{
    private static void StrongMatch(PipelineTestHarness harness, string id = "27205") =>
        harness.MetadataProvider.OnSearch = query => [new MetadataCandidate(new ProviderRef("tmdb", id), query.Title, query.Year, 1.0)];

    [Fact]
    public async Task A_redownloaded_movie_revives_its_tombstone_with_the_same_public_id()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);

        var first = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv");
        await harness.Orchestrator.DriveAsync(first.IngestId, CancellationToken.None);

        Guid movieId;
        string publicId;
        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var movie = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Movie);
            (movieId, publicId) = (movie.Id, movie.PublicId!);

            await FavoriteAsync(database, movieId);
            var deleter = new LibraryDeleteService(database, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));
            Assert.True(await deleter.DeleteAsync(movieId, deleteFiles: true, deleteUserData: false, CancellationToken.None));
            Assert.NotNull((await database.MediaItems.AsNoTracking().SingleAsync(item => item.Id == movieId)).RemovedAt);
        }

        var second = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.2160p", "Inception.2010.2160p/movie.mkv", first.CatalogId);
        await harness.Orchestrator.DriveAsync(second.IngestId, CancellationToken.None);

        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var revived = await verifyDb.MediaItems.SingleAsync(item => item.Kind == MediaKind.Movie);
        Assert.Equal(movieId, revived.Id); // the same row, not a lookalike
        Assert.Equal(publicId, revived.PublicId); // deterministic id: even Jellyfin clients see the old item
        Assert.Null(revived.RemovedAt);
        Assert.Equal(first.CatalogId, revived.CatalogId);
        Assert.True(await verifyDb.UserItemData.AnyAsync(data => data.MediaItemId == movieId && data.IsFavorite));
        Assert.True(await verifyDb.MediaSources.AnyAsync(source => source.MediaItemId == movieId));
    }

    [Fact]
    public async Task A_movie_tombstone_is_rehomed_into_the_catalog_that_downloads_it()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);

        var first = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv");
        await harness.Orchestrator.DriveAsync(first.IngestId, CancellationToken.None);

        Guid movieId;
        string oldPublicId;
        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var movie = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Movie);
            (movieId, oldPublicId) = (movie.Id, movie.PublicId!);
            await FavoriteAsync(database, movieId);
            var deleter = new LibraryDeleteService(database, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));
            Assert.True(await deleter.DeleteAsync(movieId, deleteFiles: true, deleteUserData: false, CancellationToken.None));
        }

        // No catalog id passed: the second download lands in a brand-new catalog.
        var second = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.2160p", "Inception.2010.2160p/movie.mkv");
        await harness.Orchestrator.DriveAsync(second.IngestId, CancellationToken.None);

        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var revived = await verifyDb.MediaItems.SingleAsync(item => item.Kind == MediaKind.Movie);
        Assert.Equal(movieId, revived.Id); // adopted across the catalog boundary
        Assert.Equal(second.CatalogId, revived.CatalogId);
        Assert.Null(revived.RemovedAt);
        // The public id embeds the catalog, so a cross-catalog return surfaces under a fresh one.
        Assert.NotNull(revived.PublicId);
        Assert.NotEqual(oldPublicId, revived.PublicId);
        Assert.True(await verifyDb.UserItemData.AnyAsync(data => data.MediaItemId == movieId && data.IsFavorite));
    }

    [Fact]
    public async Task A_redownloaded_episode_revives_its_ghost_chain()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness, id: "1396");

        var first = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "Breaking.Bad.S01E01.1080p", "Breaking.Bad.S01E01.1080p/Breaking.Bad.S01E01.mkv");
        await harness.Orchestrator.DriveAsync(first.IngestId, CancellationToken.None);

        Guid episodeId, seasonId, seriesId;
        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var episode = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Episode);
            (episodeId, seasonId, seriesId) = (episode.Id, episode.SeasonId!.Value, episode.SeriesId!.Value);

            await FavoriteAsync(database, episodeId);
            var deleter = new LibraryDeleteService(database, new LibraryFileEraser(new CatalogPathSandbox(), NullLogger<LibraryFileEraser>.Instance));
            var result = await deleter.DeleteEpisodeAsync(episodeId, deleteFiles: true, deleteUserData: false, CancellationToken.None);
            Assert.NotNull(result);
            // The only episode is gone, so the whole chain left the library — as ghosts.
            Assert.True(result.SeriesRemoved);
        }

        var second = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "Breaking.Bad.S01E01.2160p", "Breaking.Bad.S01E01.2160p/Breaking.Bad.S01E01.mkv", first.CatalogId);
        await harness.Orchestrator.DriveAsync(second.IngestId, CancellationToken.None);

        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var episodes = await verifyDb.MediaItems.Where(item => item.Kind == MediaKind.Episode).ToListAsync();
        var revived = Assert.Single(episodes);
        Assert.Equal(episodeId, revived.Id);
        Assert.Null(revived.RemovedAt);
        Assert.NotNull(revived.PublicId);
        // The containers were revived too — same rows, republished, no duplicates beside them.
        var season = Assert.Single(await verifyDb.MediaItems.Where(item => item.Kind == MediaKind.Season).ToListAsync());
        var series = Assert.Single(await verifyDb.MediaItems.Where(item => item.Kind == MediaKind.Series).ToListAsync());
        Assert.Equal(seasonId, season.Id);
        Assert.Equal(seriesId, series.Id);
        Assert.Null(season.RemovedAt);
        Assert.Null(series.RemovedAt);
        Assert.True(await verifyDb.UserItemData.AnyAsync(data => data.MediaItemId == episodeId && data.IsFavorite));
    }

    private static async Task FavoriteAsync(MediaServerDbContext database, Guid mediaItemId)
    {
        var now = DateTimeOffset.UtcNow;
        var user = await database.AppUsers.FirstOrDefaultAsync();
        if (user is null)
        {
            user = new AppUser
            {
                HostUserId = "host-1", Email = "user@example.com", Role = AppUserRole.User,
                CreatedAt = now, LastSeenAt = now,
            };
            database.AppUsers.Add(user);
            await database.SaveChangesAsync();
        }

        database.UserItemData.Add(new UserItemData
        {
            Id = Guid.NewGuid(), AppUserId = user.Id, MediaItemId = mediaItemId, IsFavorite = true,
        });
        await database.SaveChangesAsync();
        database.ChangeTracker.Clear();
    }
}
