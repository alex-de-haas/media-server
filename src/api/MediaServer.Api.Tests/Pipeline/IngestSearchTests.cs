using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>Covers the M4 re-search-by-corrected-title flow on a NeedsReview item.</summary>
public sealed class IngestSearchTests
{
    [Fact]
    public async Task SearchAsync_uses_corrected_title_and_movie_kind_for_a_movie_catalog()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Project.Hail.Mary.rus.LostFilm.TV.avi", "Project.Hail.Mary.rus.LostFilm.TV.avi");

        MediaQuery? captured = null;
        harness.MetadataProvider.OnSearch = query =>
        {
            captured = query;
            return [new MetadataCandidate(new ProviderRef("tmdb", "123"), "Project Hail Mary", 2026, 0.9)];
        };

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        var results = await service.SearchAsync(ingestId, new MetadataSearchRequest("Project Hail Mary", 2026, null), CancellationToken.None);

        Assert.NotNull(results);
        var candidate = Assert.Single(results!);
        Assert.Equal("Project Hail Mary", candidate.Title);
        Assert.NotNull(captured);
        Assert.Equal(MediaKind.Movie, captured!.Kind);
        Assert.Equal("Project Hail Mary", captured.Title);
        Assert.Equal(2026, captured.Year);
    }

    [Fact]
    public async Task SearchAsync_defaults_to_series_kind_for_a_series_catalog()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Series, "Some.Show.S01E01.mkv", "Some.Show.S01E01.mkv");

        MediaQuery? captured = null;
        harness.MetadataProvider.OnSearch = query => { captured = query; return []; };

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        await service.SearchAsync(ingestId, new MetadataSearchRequest("Some Show", null, null), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(MediaKind.Series, captured!.Kind);
    }

    [Fact]
    public async Task SearchAsync_honors_an_explicit_kind_override()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Ambiguous.mkv", "Ambiguous.mkv");

        MediaQuery? captured = null;
        harness.MetadataProvider.OnSearch = query => { captured = query; return []; };

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        await service.SearchAsync(ingestId, new MetadataSearchRequest("Ambiguous", null, MediaKind.Series), CancellationToken.None);

        Assert.Equal(MediaKind.Series, captured!.Kind);
    }

    [Fact]
    public async Task SearchAsync_falls_back_to_a_yearless_query_when_the_year_filter_returns_nothing()
    {
        using var harness = new PipelineTestHarness();
        var (ingestId, _, _) = await harness.SeedCompletedDownloadAsync(
            CatalogType.Movie, "Project.Hail.Mary.rus.LostFilm.TV.avi", "Project.Hail.Mary.rus.LostFilm.TV.avi");

        // Mirrors TMDb: a year-constrained search for an upcoming film returns nothing; yearless finds it.
        var queries = new List<MediaQuery>();
        harness.MetadataProvider.OnSearch = query =>
        {
            queries.Add(query);
            return query.Year is null
                ? [new MetadataCandidate(new ProviderRef("tmdb", "123"), "Project Hail Mary", 2026, 0.95)]
                : [];
        };

        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();
        var results = await service.SearchAsync(ingestId, new MetadataSearchRequest("Project Hail Mary", 2026, null), CancellationToken.None);

        var candidate = Assert.Single(results!);
        Assert.Equal("Project Hail Mary", candidate.Title);
        // First the year-constrained attempt, then the yearless fallback.
        Assert.Equal([2026, null], queries.Select(query => query.Year));
    }

    [Fact]
    public async Task SearchAsync_returns_null_for_an_unknown_item()
    {
        using var harness = new PipelineTestHarness();
        using var scope = harness.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IngestService>();

        var results = await service.SearchAsync(Guid.NewGuid(), new MetadataSearchRequest("Whatever", null, null), CancellationToken.None);

        Assert.Null(results);
    }
}
