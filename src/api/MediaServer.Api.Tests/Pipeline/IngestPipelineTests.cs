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
                new MatchRequest(MediaKind.Movie, "tmdb", "27205", "Inception", 2010,
                    [new MatchFileRequest(sourceFileId, null, null)]), CancellationToken.None);
            Assert.Equal(MatchOutcome.Matched, matched);
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var ingest = await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);
        Assert.True(await verifyDb.MediaItems.AnyAsync(item => item.Title == "Inception" && item.PublicId != null));
    }

    [Fact]
    public async Task Match_with_no_files_is_rejected_without_re_driving_the_item()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Obscure.Release.2021", "Obscure.Release.2021/movie.mkv");
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();
        Assert.Equal(MatchOutcome.FileNotFound, await ingestService.MatchAsync(ingestId,
            new MatchRequest(MediaKind.Movie, "tmdb", "27205", "Inception", 2010, []), CancellationToken.None));

        // The parked item must not have been flipped back to Pending by an empty batch.
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.NeedsReview, (await database.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);
    }

    [Fact]
    public async Task Bulk_match_resolves_a_whole_series_pack_with_one_confirmed_identity()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [new MetadataCandidate(new ProviderRef("tmdb", "1"), "Wrong", 1999, 0.1)];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "Obscure.Show.S01",
            "Obscure.Show.S01/Obscure.Show.S01E01.mkv",
            additionalSourceRelativePaths: ["Obscure.Show.S01/Obscure.Show.S01E02.mkv"]);
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            Assert.Equal(IngestStatus.NeedsReview, (await database.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);
            var files = await database.SourceFiles.OrderBy(file => file.RelativePath).ToListAsync();

            // One request: the operator confirmed the series once; only season/episode vary per file.
            var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.Equal(MatchOutcome.Matched, await ingestService.MatchAsync(ingestId,
                new MatchRequest(MediaKind.Episode, "tmdb", "4242", "Obscure Show", 2020,
                    [new MatchFileRequest(files[0].Id, 1, 1), new MatchFileRequest(files[1].Id, 1, 2)]),
                CancellationToken.None));
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verifyScope = harness.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);
        var episodes = await verifyDb.MediaItems
            .Where(item => item.Kind == MediaKind.Episode).OrderBy(item => item.IndexNumber).ToListAsync();
        Assert.Equal([1, 2], episodes.Select(episode => episode.IndexNumber ?? 0));
        Assert.All(episodes, episode => Assert.NotNull(episode.PublicId));
    }

    [Fact]
    public async Task Auto_matched_file_can_be_rematched_while_in_review_and_surfaces_its_mapping()
    {
        using var harness = new PipelineTestHarness();
        // The episode auto-matches (to E05 per its name); the creditless ED parks the batch in review.
        harness.MetadataProvider.OnSearch = query => query.Episode is not null
            ? [new MetadataCandidate(new ProviderRef("tmdb", "31911"), "Fullmetal Alchemist: Brotherhood", 2009, 1.0)]
            : [];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "FMA Brotherhood S01",
            "FMA/Fullmetal Alchemist Brotherhood S01E05.mkv",
            additionalSourceRelativePaths: ["FMA/Fullmetal Alchemist Brotherhood (Creditless ED 1).mkv"]);
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();

        // The review response shows where the auto-matched file currently points — series identity included,
        // so the dialog can pre-select the same series.
        var response = await ingestService.GetAsync(ingestId, CancellationToken.None);
        Assert.NotNull(response);
        var mapped = Assert.Single(response.SourceFiles, file => file.Assigned is not null);
        Assert.Equal("Episode", mapped.Assigned!.Kind);
        Assert.Equal(1, mapped.Assigned.Season);
        Assert.Equal(5, mapped.Assigned.Episode);
        Assert.Equal("Fullmetal Alchemist: Brotherhood", mapped.Assigned.SeriesTitle);
        Assert.Equal("tmdb", mapped.Assigned.Provider);
        Assert.Equal("31911", mapped.Assigned.ProviderId);

        // The operator corrects the episode number (the release was mislabeled) while the item is parked.
        Assert.Equal(MatchOutcome.Matched, await ingestService.MatchAsync(ingestId,
            new MatchRequest(MediaKind.Episode, "tmdb", "31911", "Fullmetal Alchemist: Brotherhood", 2009,
                [new MatchFileRequest(mapped.Id, 1, 6)]),
            CancellationToken.None));

        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var rematched = await database.MediaItems.SingleAsync(item =>
            item.Kind == MediaKind.Episode && item.IdentityEpisodeNumber == 6);
        Assert.Equal(rematched.Id, (await database.SourceFiles.SingleAsync(file => file.Id == mapped.Id)).MediaItemId);
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
    public async Task Two_files_for_one_episode_publish_as_alternate_versions()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness, id: "1399");

        // A release that ships both a regular and a black-and-white cut of the same episode.
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "Spider-Noir.S01E04.1080p",
            "Spider-Noir.S01E04.1080p/Spider-Noir.S01E04.1080p.rus.LostFilm.TV.mkv",
            additionalSourceRelativePaths: ["Spider-Noir.S01E04.1080p/Spider-Noir.BW.S01E04.1080p.rus.LostFilm.TV.mkv"]);

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        // The two files collapse onto one episode...
        var episode = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Episode);
        Assert.False(string.IsNullOrEmpty(episode.PublicId));

        // ...exposed as two distinct, labelled, on-disk versions linked back to their source files.
        var sources = await database.MediaSources.Where(source => source.MediaItemId == episode.Id).ToListAsync();
        Assert.Equal(2, sources.Count);
        Assert.Contains(sources, source => source.VersionName == "Black & White");
        Assert.Contains(sources, source => source.VersionName == "Standard");
        Assert.Equal(2, sources.Select(source => source.Path).Distinct().Count());
        Assert.All(sources, source => Assert.NotNull(source.SourceFileId));

        var ingest = await database.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);
    }

    [Fact]
    public async Task Ingest_list_shows_series_name_and_season_for_episodes()
    {
        using var harness = new PipelineTestHarness();
        StrongMatch(harness, id: "1399");

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "Some.Show.S01E02.1080p", "Some.Show.S01E02.1080p/Some.Show.S01E02.mkv");

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var ingestService = scope.ServiceProvider.GetRequiredService<IngestService>();

        var seriesTitle = await database.MediaItems
            .Where(item => item.Kind == MediaKind.Series).Select(item => item.Title).SingleAsync();

        var listed = await ingestService.ListAsync(CancellationToken.None);
        Assert.Equal($"{seriesTitle} · S01E02", Assert.Single(listed).MediaTitle);

        // The single-item GetAsync path composes the same title.
        var fetched = await ingestService.GetAsync(ingestId, CancellationToken.None);
        Assert.Equal($"{seriesTitle} · S01E02", fetched!.MediaTitle);
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
