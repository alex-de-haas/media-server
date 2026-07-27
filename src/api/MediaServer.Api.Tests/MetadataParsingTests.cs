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

    [Theory]
    [InlineData("01. Назад в будущее 1985.mkv", "Назад в будущее", 1985)]
    [InlineData("01.Назад в будущее.1985.mkv", "Назад в будущее", 1985)]
    [InlineData("02 - Die Hard 2 1990.mkv", "Die Hard 2", 1990)]
    [InlineData("2. Die Hard 2 (1990).mkv", "Die Hard 2", 1990)]
    public void Strips_track_ordinal_prefix_from_movie_names(string name, string title, int year)
    {
        // Franchise packs number their films ("01. …") and the ordinal poisons the provider query.
        var parsed = _parser.Parse(name, CatalogType.Movie);

        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Theory]
    [InlineData("8 Mile (2002).mkv", "8 Mile", 2002)]
    [InlineData("1408.2007.1080p.mkv", "1408", 2007)]
    [InlineData("24.2016.1080p.mkv", "24", 2016)]
    public void Keeps_movie_titles_that_start_with_digits(string name, string title, int year)
    {
        // A bare number with no dot-space separator (or a dotted number without a leading zero) is a
        // title, not a track ordinal.
        var parsed = _parser.Parse(name, CatalogType.Movie);

        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Fact]
    public void Keeps_leading_numbers_in_series_names()
    {
        // In series catalogs a leading number is an episode ordinal, so movie-style ordinal stripping
        // must not apply.
        var parsed = _parser.Parse("01. The Show.mkv", CatalogType.Series);

        Assert.Equal(MediaKind.Series, parsed.Kind);
        Assert.Equal("01 The Show", parsed.Title);
    }

    [Fact]
    public void Strips_custom_release_group_from_a_movie_title()
    {
        // The dotted group is normalized the same way as the name, so "LostFilm.TV" matches "LostFilm TV".
        var parsed = _parser.Parse("Project.Hail.Mary.LostFilm.TV.avi", CatalogType.Movie, ["LostFilm.TV"]);

        Assert.Equal("Project Hail Mary", parsed.Title);
    }

    [Fact]
    public void Strips_custom_release_group_but_keeps_season_and_episode()
    {
        var parsed = _parser.Parse("[NewStudio] The Show S02E03 1080p.mkv", CatalogType.Series, ["NewStudio"]);

        Assert.Equal("The Show", parsed.Title);
        Assert.Equal(2, parsed.Season);
        Assert.Equal(3, parsed.Episode);
    }

    [Fact]
    public void Strips_bracket_wrapped_group_configured_without_brackets()
    {
        // A group configured as "HorribleSubs" is stripped even when the file wraps it in brackets — the
        // non-word-character boundary matches where a plain \b boundary would not.
        var parsed = _parser.Parse("[HorribleSubs] The Show S01E05.mkv", CatalogType.Series, ["HorribleSubs"]);

        Assert.Equal("The Show", parsed.Title);
        Assert.Equal(1, parsed.Season);
        Assert.Equal(5, parsed.Episode);
    }

    [Fact]
    public void Release_group_match_is_whole_word_and_case_insensitive()
    {
        // "yts" strips "YTS" despite the case difference, while "Mov" is left alone because it is only a
        // substring of "Movie" (whole-word matching never clips a real title word).
        var parsed = _parser.Parse("Mystery.Movie.2021.YTS.mp4", CatalogType.Movie, ["yts", "Mov"]);

        Assert.Equal("Mystery Movie", parsed.Title);
        Assert.Equal(2021, parsed.Year);
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

        Assert.Equal("Inception (2010)/Inception (2010).mkv", path);
    }

    [Fact]
    public void Movie_without_year_drops_empty_parentheses()
    {
        var catalog = new Catalog { Name = "Movies", Root = "/root", Type = CatalogType.Movie, NamingTemplate = "{Title} ({Year})" };
        var movie = new MediaItem { CatalogId = catalog.Id, Kind = MediaKind.Movie, Title = "Untitled" };

        var path = LibraryNaming.ForMovie(catalog, movie, ".mp4");

        Assert.Equal("Untitled/Untitled.mp4", path);
    }

    [Fact]
    public void Episode_path_uses_jellyfin_layout()
    {
        var series = new MediaItem { Kind = MediaKind.Series, Title = "The Show", Year = 2015 };
        var episode = new MediaItem { Kind = MediaKind.Episode, Title = "Pilot", ParentIndexNumber = 1, IndexNumber = 2 };

        var path = LibraryNaming.ForEpisode(series, episode, ".mkv");

        Assert.Equal("The Show (2015)/Season 01/The Show S01E02.mkv", path);
    }
}
