using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

public sealed class IngestPipelineTests
{
    private static void StrongMatch(PipelineTestHarness harness, string provider = "tmdb", string id = "27205") =>
        harness.MetadataProvider.OnSearch = query => [new MetadataCandidate(new ProviderRef(provider, id), query.Title, query.Year, 1.0)];

    [Fact]
    public async Task Movie_happy_path_publishes_a_playable_item()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);
        var (ingestId, catalogId, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Inception.2010.1080p.BluRay.x264", "Inception.2010.1080p.BluRay.x264/Inception.2010.1080p.mkv");

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var ingest = await database.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);
        Assert.NotNull(ingest.MediaItemId);

        var movie = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Movie);
        Assert.Equal("Inception", movie.Title);
        Assert.Equal(2010, movie.Year);
        Assert.False(string.IsNullOrEmpty(movie.PublicId));
        Assert.Equal("tmdb", movie.IdentityProvider);
        Assert.NotNull(movie.LibraryPath);

        // The clean library hardlink exists on disk.
        var catalog = await database.Catalogs.SingleAsync(item => item.Id == catalogId);
        var libraryAbsolute = Path.Combine(catalog.Root, movie.LibraryPath!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(libraryAbsolute));

        // Probed into a media source with streams.
        var source = await database.MediaSources.Include(item => item.Streams).SingleAsync(item => item.MediaItemId == movie.Id);
        Assert.Equal(2, source.Streams.Count);

        // Enriched for the supported language.
        Assert.True(await database.MetadataRecords.AnyAsync(record => record.MediaItemId == movie.Id && record.Language == "en-US"));
    }

    [Fact]
    public async Task Re_identifying_the_same_title_reuses_one_media_item()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness);

        var first = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/movie.mkv");
        await harness.Orchestrator.DriveAsync(first.IngestId, CancellationToken.None);

        // A second download of the same movie in the same catalog maps to the same canonical identity.
        var second = await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Inception.2010.720p", "Inception.2010.720p/movie.mkv", first.CatalogId);
        await harness.Orchestrator.DriveAsync(second.IngestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(1, await database.MediaItems.CountAsync(item => item.Kind == MediaKind.Movie));
    }

    [Fact]
    public async Task Low_confidence_match_routes_to_review_without_publishing()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = query => [new MetadataCandidate(new ProviderRef("tmdb", "1"), "Something Unrelated", 1999, 0.2)];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Obscure.Release.2021", "Obscure.Release.2021/movie.mkv");

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var ingest = await database.IngestItems.SingleAsync(item => item.Id == ingestId);

        Assert.Equal(IngestStatus.NeedsReview, ingest.Status);
        Assert.False(await database.MediaItems.AnyAsync(item => item.PublicId != null));
    }

    [Fact]
    public async Task Manual_match_resumes_a_review_item_to_publication()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [new MetadataCandidate(new ProviderRef("tmdb", "1"), "Wrong", 1999, 0.1)];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Ambiguous.2021", "Ambiguous.2021/movie.mkv");
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        Guid sourceFileId;
        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            sourceFileId = await database.SourceFiles.Select(file => file.Id).SingleAsync();

            var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();
            var matched = await ingestService.MatchAsync(ingestId,
                new MatchRequest(sourceFileId, MediaKind.Movie, "tmdb", "27205", "Inception", 2010, null, null), CancellationToken.None);
            Assert.True(matched);
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var ingest = await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);
        Assert.True(await verifyDb.MediaItems.AnyAsync(item => item.Title == "Inception" && item.PublicId != null));
    }

    [Fact]
    public async Task Series_pack_publishes_episode_under_series_and_season()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness, id: "1399");

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "Some.Show.S01E02.1080p", "Some.Show.S01E02.1080p/Some.Show.S01E02.mkv");

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var episode = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Episode);
        Assert.Equal(1, episode.ParentIndexNumber);
        Assert.Equal(2, episode.IndexNumber);
        Assert.NotNull(episode.SeriesId);
        Assert.NotNull(episode.SeasonId);
        Assert.False(string.IsNullOrEmpty(episode.PublicId));

        Assert.True(await database.MediaItems.AnyAsync(item => item.Kind == MediaKind.Series));
        Assert.True(await database.MediaItems.AnyAsync(item => item.Kind == MediaKind.Season));

        var ingest = await database.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);
    }

    [Fact]
    public async Task Ingest_list_orders_by_created_at_without_sqlite_translation_error()
    {
        using var harness = new PipelineTestHarness();

        // Two ingest items (one per seeded download); listing orders them by CreatedAt. Ordering a
        // DateTimeOffset in SQL throws on SQLite, so this would 500 if it didn't order client-side.
        await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "First.2001", "First.2001/movie.mkv");
        await harness.SeedCompletedDownloadAsync(CatalogType.Movie, "Second.2002", "Second.2002/movie.mkv");

        using var scope = harness.CreateScope();
        var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();

        var items = await ingestService.ListAsync(CancellationToken.None);

        Assert.Equal(2, items.Count);
    }
}
