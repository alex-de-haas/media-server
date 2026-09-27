using Imposter.Abstractions;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[assembly: GenerateImposter(typeof(IMetadataProvider))]

namespace MediaServer.Api.Tests.Pipeline;

public sealed class OriginalMovieNamingTests
{
    [Theory]
    [InlineData("Крепкий орешек", "Die Hard", "Die Hard")]
    [InlineData("Die Hard", "Die Hard", "Die Hard")]
    [InlineData("Localized title", "映画", "映画")]
    [InlineData("Localized title", null, "Localized title")]
    [InlineData("Localized title", "  ", "Localized title")]
    public async Task Automatic_match_uses_original_title_before_placement(
        string displayTitle, string? originalTitle, string expectedTitle)
    {
        using var harness = Harness(displayTitle, () => originalTitle);
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Release.1988", "Release.1988/movie.mkv");

        await harness.Orchestrator.DriveAsync(ingestId, default);

        using var scope = harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await db.IngestItems.SingleAsync()).Status);
        var movie = await db.MediaItems.SingleAsync();
        Assert.Equal(displayTitle, movie.Title);
        var expectedPath = $"{expectedTitle} (1988)/{expectedTitle} (1988).mkv";
        Assert.Equal(expectedPath, movie.LibraryPath);
        Assert.Equal(expectedPath, (await db.MediaSources.SingleAsync()).Path);
        var catalog = await db.Catalogs.SingleAsync();
        Assert.True(File.Exists(Path.Combine(catalog.Root, expectedPath)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_and_pinned_matches_resolve_original_title(bool pinned)
    {
        using var harness = Harness("Крепкий орешек", () => "Die Hard", allowSearch: false);
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Release.1988", "Release.1988/movie.mkv");
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            if (pinned)
            {
                var ingest = await db.IngestItems.SingleAsync();
                ingest.TargetProvider = "tmdb";
                ingest.TargetProviderId = "562";
                ingest.TargetKind = MediaKind.Movie;
                ingest.TargetTitle = "Крепкий орешек";
                ingest.TargetYear = 1988;
                await db.SaveChangesAsync();
            }
            else
            {
                var file = await db.SourceFiles.SingleAsync();
                var result = await scope.ServiceProvider.GetRequiredService<IngestService>().MatchAsync(
                    ingestId, new MatchRequest(MediaKind.Movie, "tmdb", "562", "Крепкий орешек", 1988,
                        [new MatchFileRequest(file.Id, null, null)]), default);
                Assert.Equal(MatchOutcome.Matched, result);
                Assert.Equal("Die Hard", (await db.MediaItems.SingleAsync()).OriginalTitle);
            }
        }

        await harness.Orchestrator.DriveAsync(ingestId, default);

        using var verify = harness.CreateScope();
        var database = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await database.IngestItems.SingleAsync()).Status);
        Assert.Equal("Die Hard (1988)/Die Hard (1988).mkv", (await database.MediaSources.SingleAsync()).Path);
    }

    [Fact]
    public async Task Later_original_metadata_keeps_new_versions_in_the_existing_folder()
    {
        string? original = null;
        using var harness = Harness("Крепкий орешек", () => original);
        var first = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Release.1988", "Release.1988/movie.mkv");
        await harness.Orchestrator.DriveAsync(first.IngestId, default);

        // Simulate enrichment of an older library item after its localized path was established.
        original = "Die Hard";
        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            (await db.MediaItems.SingleAsync()).OriginalTitle = original;
            await db.SaveChangesAsync();
        }
        var second = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Release.1988.4K", "Release.1988.4K/movie.mkv", first.CatalogId);
        await harness.Orchestrator.DriveAsync(second.IngestId, default);

        using var verify = harness.CreateScope();
        var database = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.All(await database.IngestItems.ToListAsync(), item => Assert.Equal(IngestStatus.Done, item.Status));
        Assert.Single(await database.MediaItems.ToListAsync());
        Assert.Equal(new[]
        {
            "Крепкий орешек (1988)/Крепкий орешек (1988) - Version 2.mkv",
            "Крепкий орешек (1988)/Крепкий орешек (1988).mkv",
        }, await database.MediaSources.OrderBy(source => source.Path).Select(source => source.Path).ToArrayAsync());
        var catalog = await database.Catalogs.SingleAsync();
        Assert.All(await database.MediaSources.ToListAsync(), source => Assert.True(File.Exists(Path.Combine(catalog.Root, source.Path))));
    }

    [Fact]
    public async Task Empty_metadata_response_retries_before_placement_and_recovers_original_name()
    {
        var available = false;
        using var harness = Harness("Крепкий орешек", () => "Die Hard", metadataAvailable: () => available);
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Release.1988", "Release.1988/movie.mkv");

        await harness.Orchestrator.DriveAsync(ingestId, default);

        using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MediaServerDbContext>();
            var ingest = await db.IngestItems.SingleAsync();
            Assert.Equal(IngestStatus.Pending, ingest.Status);
            Assert.Equal(IngestStage.Identify, ingest.Stage);
            Assert.NotNull(ingest.NextAttemptAt);
            Assert.Contains("No movie metadata returned", ingest.LastError);
            Assert.DoesNotContain("organize", ingest.StagesCompleted);
            Assert.Empty(await db.MediaItems.ToListAsync());
            var file = await db.SourceFiles.SingleAsync();
            var catalog = await db.Catalogs.SingleAsync();
            Assert.Null(file.PlacementPath);
            Assert.True(File.Exists(Path.Combine(catalog.Root, file.RelativePath)));
            Assert.False(Directory.Exists(Path.Combine(catalog.Root, "Крепкий орешек (1988)")));
            Assert.True(await scope.ServiceProvider.GetRequiredService<IngestService>().RetryAsync(ingestId, default));
        }

        available = true;
        await harness.Orchestrator.DriveAsync(ingestId, default);

        using var verify = harness.CreateScope();
        var database = verify.ServiceProvider.GetRequiredService<MediaServerDbContext>();
        Assert.Equal(IngestStatus.Done, (await database.IngestItems.SingleAsync()).Status);
        var path = (await database.MediaSources.SingleAsync()).Path;
        Assert.Equal("Die Hard (1988)/Die Hard (1988).mkv", path);
        Assert.True(File.Exists(Path.Combine((await database.Catalogs.SingleAsync()).Root, path)));
    }

    private static PipelineTestHarness Harness(string title, Func<string?> original, bool allowSearch = true,
        Func<bool>? metadataAvailable = null)
    {
        var provider = IMetadataProvider.Imposter();
        provider.SearchAsync(Arg<MediaQuery>.Any(), Arg<CancellationToken>.Any())
            .Returns((MediaQuery query, CancellationToken ct) => allowSearch
                ? Task.FromResult<IReadOnlyList<MetadataCandidate>>([new(new("tmdb", "562"), title, 1988, 1)])
                : throw new InvalidOperationException("A confirmed identity must not be searched again."));
        provider.FetchAsync(Arg<ProviderRef>.Any(), Arg<MediaKind>.Any(), Arg<IReadOnlyList<string>>.Any(), Arg<CancellationToken>.Any())
            .Returns((ProviderRef reference, MediaKind kind, IReadOnlyList<string> languages, CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<ProviderMetadata>>(metadataAvailable?.Invoke() == false ? [] : languages.Select(language => new ProviderMetadata(
                    reference, language, title, original(), "en", null, null, [], null, null, null, null, "{}")).ToList()));
        provider.GetImagesAsync(Arg<ProviderRef>.Any(), Arg<MediaKind>.Any(), Arg<IReadOnlyList<string>>.Any(), Arg<CancellationToken>.Any())
            .Returns(Task.FromResult<IReadOnlyList<RemoteImage>>([]));
        return new PipelineTestHarness(services => services.AddSingleton(provider.Instance()));
    }
}
