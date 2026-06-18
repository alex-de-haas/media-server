using MediaServer.Api.Catalogs;
using MediaServer.Api.Data;
using MediaServer.Api.Metadata;
using MediaServer.Api.Organizer;
using MediaServer.Api.Probe;

namespace MediaServer.Api.Tests;

public sealed class NameParserTests
{
    private readonly NameParser _parser = new();

    [Fact]
    public void Parses_movie_title_and_year()
    {
        var parsed = _parser.Parse("Inception.2010.1080p.BluRay.x264-GROUP", CatalogType.Movie);

        Assert.Equal(MediaKind.Movie, parsed.Kind);
        Assert.Equal("Inception", parsed.Title);
        Assert.Equal(2010, parsed.Year);
    }

    [Theory]
    [InlineData("The.Show.S01E02.1080p", "The Show", 1, 2)]
    [InlineData("The Show 1x05 WEB-DL", "The Show", 1, 5)]
    [InlineData("Another.Show.s03e11.HDTV", "Another Show", 3, 11)]
    public void Parses_series_season_episode(string name, string title, int season, int episode)
    {
        var parsed = _parser.Parse(name, CatalogType.Series);

        Assert.Equal(MediaKind.Episode, parsed.Kind);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
    }

    [Fact]
    public void Parses_double_episode_range()
    {
        var parsed = _parser.Parse("The.Show.S01E02E03.1080p", CatalogType.Series);

        Assert.Equal(2, parsed.Episode);
        Assert.Equal(3, parsed.EpisodeEnd);
    }

    [Fact]
    public void Parses_anime_absolute_numbering()
    {
        var parsed = _parser.Parse("[Group] Some Anime - 12 [1080p].mkv", CatalogType.Anime);

        Assert.Equal(MediaKind.Episode, parsed.Kind);
        Assert.Contains("Some Anime", parsed.Title);
        Assert.Equal(12, parsed.Episode);
    }
}

public sealed class TitleScoringTests
{
    [Fact]
    public void Exact_title_and_year_scores_highest()
    {
        var score = TitleScoring.Score("Inception", 2010, "Inception", 2010);
        Assert.True(score >= TitleScoring.AutoMatchThreshold);
    }

    [Fact]
    public void Unrelated_title_scores_below_threshold()
    {
        var score = TitleScoring.Score("Inception", 2010, "Frozen", 2013);
        Assert.True(score < TitleScoring.AutoMatchThreshold);
    }

    [Fact]
    public void Wrong_year_penalizes_score()
    {
        var same = TitleScoring.Score("The Matrix", 1999, "The Matrix", 1999);
        var wrong = TitleScoring.Score("The Matrix", 1999, "The Matrix", 2003);
        Assert.True(same > wrong);
    }
}

public sealed class LibraryNamingTests
{
    [Fact]
    public void Movie_path_uses_template_and_preserves_extension()
    {
        var catalog = new Catalog { Name = "Movies", Root = "/root", Type = CatalogType.Movie, NamingTemplate = "{Title} ({Year})" };
        var movie = new MediaItem { CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Inception", Year = 2010 };

        var path = LibraryNaming.ForMovie(catalog, movie, ".mkv");

        Assert.Equal("library/Inception (2010)/Inception (2010).mkv", path);
    }

    [Fact]
    public void Movie_without_year_drops_empty_parentheses()
    {
        var catalog = new Catalog { Name = "Movies", Root = "/root", Type = CatalogType.Movie, NamingTemplate = "{Title} ({Year})" };
        var movie = new MediaItem { CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Untitled" };

        var path = LibraryNaming.ForMovie(catalog, movie, ".mp4");

        Assert.Equal("library/Untitled/Untitled.mp4", path);
    }

    [Fact]
    public void Episode_path_uses_jellyfin_layout()
    {
        var series = new MediaItem { Kind = MediaKind.Series, Title = "The Show", Year = 2015 };
        var episode = new MediaItem { Kind = MediaKind.Episode, Title = "Pilot", ParentIndexNumber = 1, IndexNumber = 2 };

        var path = LibraryNaming.ForEpisode(series, episode, ".mkv");

        Assert.Equal("library/The Show (2015)/Season 01/The Show S01E02.mkv", path);
    }
}

public sealed class FfprobeParsingTests
{
    [Fact]
    public void Parses_format_and_streams()
    {
        const string json = """
        {
          "streams": [
            { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "r_frame_rate": "24000/1001", "color_transfer": "smpte2084", "disposition": { "default": 1, "forced": 0 } },
            { "index": 1, "codec_type": "audio", "codec_name": "aac", "channels": 6, "sample_rate": "48000", "tags": { "language": "eng" }, "disposition": { "default": 1, "forced": 0 } },
            { "index": 2, "codec_type": "subtitle", "codec_name": "subrip", "tags": { "language": "spa" }, "disposition": { "default": 0, "forced": 1 } }
          ],
          "format": { "format_name": "matroska,webm", "duration": "7200.0", "bit_rate": "8000000", "size": "7200000000" }
        }
        """;

        var result = FfprobeMediaProbe.Parse(json, "/library/movie.mkv");

        Assert.Equal("matroska,webm", result.Container);
        Assert.Equal(TimeSpan.FromSeconds(7200).Ticks, result.DurationTicks);
        Assert.Equal(8_000_000, result.Bitrate);
        Assert.Equal(3, result.Streams.Count);

        var video = result.Streams.Single(stream => stream.Type == StreamType.Video);
        Assert.Equal("h264", video.Codec);
        Assert.Equal(1920, video.Width);
        Assert.Equal("HDR10", video.HdrFormat);
        Assert.True(video.IsDefault);

        var subtitle = result.Streams.Single(stream => stream.Type == StreamType.Subtitle);
        Assert.True(subtitle.IsForced);
        Assert.Equal("spa", subtitle.Language);
    }
}
