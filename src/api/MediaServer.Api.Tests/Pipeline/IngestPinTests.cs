using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// Covers pinning a target identity on an ingest item so Identify resolves straight to it — the operator
/// "set the title before it finishes downloading" flow (and the future acquisition path). The metadata
/// provider is rigged to throw on search, proving the pinned path never searches or scores.
/// </summary>
public sealed class IngestPinTests
{
    private static void SearchMustNotRun(PipelineTestHarness harness) =>
        harness.MetadataProvider.OnSearch = _ => throw new InvalidOperationException("a pinned identify must not search the provider");

    [Fact]
    public async Task Pinned_movie_publishes_without_searching_the_provider()
    {
        using var harness = new PipelineTestHarness();
        SearchMustNotRun(harness);

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Ambiguous.Release.2021", "Ambiguous.Release.2021/movie.mkv");

        using (var scope = harness.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            var outcome = await service.PinAsync(
                ingestId, new PinIdentityRequest("tmdb", "27205", MediaKind.Movie, "Inception", 2010), CancellationToken.None);
            Assert.Equal(PinOutcome.Pinned, outcome);
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verify = harness.CreateScope();
        var database = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var ingest = await database.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);

        var movie = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Movie);
        Assert.Equal("Inception", movie.Title);
        Assert.Equal(2010, movie.Year);
        Assert.Equal("tmdb", movie.IdentityProvider);
        Assert.Equal("27205", movie.IdentityProviderId);
        Assert.False(string.IsNullOrEmpty(movie.PublicId));
    }

    [Fact]
    public async Task Pinned_series_publishes_the_episode_under_the_pinned_show_using_parsed_numbers()
    {
        using var harness = new PipelineTestHarness();
        SearchMustNotRun(harness);

        // The file name is deliberately something the parser would title-match badly; only the SxxEyy matters.
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "Totally.Mislabeled.S01E02.1080p", "Totally.Mislabeled.S01E02.1080p/Totally.Mislabeled.S01E02.mkv");

        using (var scope = harness.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            var outcome = await service.PinAsync(
                ingestId, new PinIdentityRequest("tmdb", "1399", MediaKind.Series, "Game of Thrones", 2011), CancellationToken.None);
            Assert.Equal(PinOutcome.Pinned, outcome);
        }

        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verify = harness.CreateScope();
        var database = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();

        var series = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Series);
        Assert.Equal("Game of Thrones", series.Title);
        Assert.Equal("1399", series.IdentityProviderId);

        var episode = await database.MediaItems.SingleAsync(item => item.Kind == MediaKind.Episode);
        Assert.Equal(1, episode.ParentIndexNumber);
        Assert.Equal(2, episode.IndexNumber);
        Assert.Equal(series.Id, episode.SeriesId);
        Assert.False(string.IsNullOrEmpty(episode.PublicId));

        Assert.Equal(IngestStatus.Done, (await database.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);
    }

    [Fact]
    public async Task Pinning_a_needs_review_item_resolves_it_to_publication_without_re_searching()
    {
        using var harness = new PipelineTestHarness();
        // First drive: a weak match parks the item at NeedsReview (no media item created).
        harness.MetadataProvider.OnSearch = _ => [new MetadataCandidate(new ProviderRef("tmdb", "1"), "Wrong", 1999, 0.1)];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Obscure.2021", "Obscure.2021/movie.mkv");
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using (var scope = harness.CreateScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            Assert.Equal(IngestStatus.NeedsReview, (await database.IngestItems.SingleAsync(item => item.Id == ingestId)).Status);
            Assert.False(await database.MediaItems.AnyAsync());
        }

        using (var scope = harness.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            var outcome = await service.PinAsync(
                ingestId, new PinIdentityRequest("tmdb", "27205", MediaKind.Movie, "Inception", 2010), CancellationToken.None);
            Assert.Equal(PinOutcome.Pinned, outcome);
        }

        // The re-drive must honor the pin and never touch the provider search again.
        SearchMustNotRun(harness);
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var verify = harness.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var ingest = await verifyDb.IngestItems.SingleAsync(item => item.Id == ingestId);
        Assert.Equal(IngestStatus.Done, ingest.Status);
        Assert.True(await verifyDb.MediaItems.AnyAsync(item => item.Title == "Inception" && item.IdentityProviderId == "27205" && item.PublicId != null));
    }

    [Fact]
    public async Task Pinning_is_rejected_once_the_item_has_already_been_identified()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = query => [new MetadataCandidate(new ProviderRef("tmdb", "27205"), query.Title, query.Year, 1.0)];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Inception.2010.1080p", "Inception.2010.1080p/Inception.2010.1080p.mkv");
        await harness.Orchestrator.DriveAsync(ingestId, CancellationToken.None);

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        var outcome = await service.PinAsync(
            ingestId, new PinIdentityRequest("tmdb", "1", MediaKind.Movie, "Something Else", 2000), CancellationToken.None);

        Assert.Equal(PinOutcome.AlreadyIdentified, outcome);
    }

    [Fact]
    public async Task Unpin_clears_the_target_identity()
    {
        using var harness = new PipelineTestHarness();
        harness.MetadataProvider.OnSearch = _ => [];

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Whatever.2021", "Whatever.2021/movie.mkv");

        using (var scope = harness.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IngestService>();
            Assert.Equal(PinOutcome.Pinned, await service.PinAsync(
                ingestId, new PinIdentityRequest("tmdb", "5", MediaKind.Movie, "Whatever", 2021), CancellationToken.None));
            Assert.True(await service.UnpinAsync(ingestId, CancellationToken.None));
        }

        using var verify = harness.CreateScope();
        var database = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        var item = await database.IngestItems.SingleAsync(candidate => candidate.Id == ingestId);
        Assert.Null(item.TargetProvider);
        Assert.Null(item.TargetProviderId);
        Assert.Null(item.TargetKind);
        Assert.Null(item.TargetTitle);
        Assert.Null(item.TargetYear);
    }

    [Fact]
    public async Task Pinning_a_kind_that_mismatches_the_catalog_is_rejected()
    {
        using var harness = new PipelineTestHarness();

        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "Some.Show.S01E01", "Some.Show.S01E01/Some.Show.S01E01.mkv");

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();

        // A movie kind on a series catalog would create a Movie under a Series catalog — reject it.
        var outcome = await service.PinAsync(
            ingestId, new PinIdentityRequest("tmdb", "1", MediaKind.Movie, "Wrong Kind", 2011), CancellationToken.None);

        Assert.Equal(PinOutcome.InvalidKind, outcome);
    }

    [Fact]
    public async Task Pin_and_unpin_report_a_missing_item()
    {
        using var harness = new PipelineTestHarness();
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();

        Assert.Equal(PinOutcome.NotFound, await service.PinAsync(
            Guid.NewGuid(), new PinIdentityRequest("tmdb", "1", MediaKind.Movie, "X", null), CancellationToken.None));
        Assert.False(await service.UnpinAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
