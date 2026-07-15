using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Pipeline;

namespace MediaServer.Api.Tests.Pipeline;

/// <summary>
/// Covers the name-parsed hints (<c>ParsedTitle/Year/Season/Episode</c>) the review DTO surfaces per
/// source file so the dialog can pre-fill the corrected title and per-file season/episode.
/// </summary>
public sealed class IngestSourceFileParseTests
{
    private static readonly NameParser Parser = new();

    private static IngestItemResponse Build(CatalogType catalogType, params string[] relativePaths)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new IngestItem { Id = Guid.NewGuid(), CatalogId = Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        var files = relativePaths
            .Select(path => new SourceFile
            {
                Id = Guid.NewGuid(),
                IngestItemId = item.Id,
                RelativePath = path,
                SizeBytes = 1,
                AssignmentStatus = SourceFileAssignmentStatus.NeedsReview,
                CreatedAt = now,
                UpdatedAt = now,
            })
            .ToList();

        return IngestItemResponse.From(
            item, files, downloadName: null, mediaTitle: null, Parser, catalogType, releaseGroups: [],
            assignedMedia: new Dictionary<Guid, IngestAssignedMedia>());
    }

    [Fact]
    public void Surfaces_per_file_season_and_episode_for_a_series_pack()
    {
        var response = Build(CatalogType.Series, "The.Show/The.Show.S02E03.1080p.mkv", "The.Show/The.Show.S02E04.1080p.mkv");

        Assert.Equal("The Show", response.SourceFiles[0].ParsedTitle);
        Assert.Equal(2, response.SourceFiles[0].ParsedSeason);
        Assert.Equal(3, response.SourceFiles[0].ParsedEpisode);

        // The second file in the same pack carries its own episode number, not a shared value.
        Assert.Equal(2, response.SourceFiles[1].ParsedSeason);
        Assert.Equal(4, response.SourceFiles[1].ParsedEpisode);
    }

    [Fact]
    public void Surfaces_title_and_year_for_a_movie_without_season_or_episode()
    {
        var response = Build(CatalogType.Movie, "Inception.2010.1080p.BluRay.x264-GROUP.mkv");

        Assert.Equal("Inception", response.SourceFiles[0].ParsedTitle);
        Assert.Equal(2010, response.SourceFiles[0].ParsedYear);
        Assert.Null(response.SourceFiles[0].ParsedSeason);
        Assert.Null(response.SourceFiles[0].ParsedEpisode);
    }
}
