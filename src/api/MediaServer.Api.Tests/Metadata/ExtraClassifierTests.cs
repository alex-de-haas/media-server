using MediaServer.Api.Data;
using MediaServer.Api.Metadata;

namespace MediaServer.Api.Tests.Metadata;

public sealed class ExtraClassifierTests
{
    [Theory]
    [InlineData("[Group] Show NCOP.mkv", ExtraKind.CreditlessOpening, "Creditless Opening")]
    [InlineData("[Group] Show NCOP1 [BD 1080p].mkv", ExtraKind.CreditlessOpening, "Creditless Opening 1")]
    [InlineData("Show.NC.ED.02.mkv", ExtraKind.CreditlessEnding, "Creditless Ending 2")]
    [InlineData("Fullmetal Alchemist Brotherhood (Creditless OP 1).mkv", ExtraKind.CreditlessOpening, "Creditless Opening 1")]
    [InlineData("Show - Creditless ED 3.mkv", ExtraKind.CreditlessEnding, "Creditless Ending 3")]
    [InlineData("Show Non-Credit Opening.mkv", ExtraKind.CreditlessOpening, "Creditless Opening")]
    [InlineData("Show Clean Ending 2 [1080p].mkv", ExtraKind.CreditlessEnding, "Creditless Ending 2")]
    public void Classifies_creditless_openings_and_endings(string name, ExtraKind kind, string title)
    {
        var extra = ExtraClassifier.Classify(name, CatalogType.Anime);

        Assert.NotNull(extra);
        Assert.Equal(kind, extra.Kind);
        Assert.Equal(title, extra.Title);
        Assert.False(extra.SuggestSkip);
    }

    [Theory]
    [InlineData("[Group] Show - OP2 [BD].mkv", ExtraKind.Opening, "Opening 2")]
    [InlineData("Show - ED 1.mkv", ExtraKind.Ending, "Ending 1")]
    [InlineData("Show (OP).mkv", ExtraKind.Opening, "Opening")]
    [InlineData("[Group] Show - SP 2 [BD].mkv", ExtraKind.Special, "Special 2")]
    [InlineData("Show SP01.mkv", ExtraKind.Special, "Special 1")]
    [InlineData("Show Special 3.mkv", ExtraKind.Special, "Special 3")]
    [InlineData("[Group] Show - PV 1.mkv", ExtraKind.PromoVideo, "PV 1")]
    [InlineData("Show Trailer.mkv", ExtraKind.Trailer, "Trailer")]
    public void Classifies_shorthand_extras(string name, ExtraKind kind, string title)
    {
        var extra = ExtraClassifier.Classify(name, CatalogType.Anime);

        Assert.NotNull(extra);
        Assert.Equal(kind, extra.Kind);
        Assert.Equal(title, extra.Title);
    }

    [Theory]
    [InlineData("Menu.mkv", "Menu")]
    [InlineData("[Group] BD Menu 01.mkv", "Menu 1")]
    [InlineData("Show - CM 2.mkv", "CM 2")]
    public void Menus_and_commercials_suggest_skip(string name, string title)
    {
        var extra = ExtraClassifier.Classify(name, CatalogType.Anime);

        Assert.NotNull(extra);
        Assert.Equal(title, extra.Title);
        Assert.True(extra.SuggestSkip);
    }

    [Fact]
    public void Files_in_an_extras_folder_classify_generically()
    {
        var extra = ExtraClassifier.Classify("Show S01/Extras/Interview with the staff.mkv", CatalogType.Series);

        Assert.NotNull(extra);
        Assert.Equal(ExtraKind.Other, extra.Kind);
        Assert.Equal("Interview with the staff", extra.Title);
    }

    [Theory]
    [InlineData("The Show S01E02 1080p.mkv")] // regular episode
    [InlineData("Show - OP Girl S01E05.mkv")] // ambiguous token next to a real episode marker
    [InlineData("[Group] One Piece - OP 1071 [1080p].mkv")] // 3+ digits reads as an absolute episode number
    [InlineData("[Group] Some Anime - 12 [1080p].mkv")] // absolute numbering, no extra tokens
    [InlineData("Trailer Park Boys - Episode Name.mkv")] // "trailer" not at the end
    [InlineData("The Menu 2022 1080p.mkv")] // title containing "menu"
    public void Regular_content_is_not_classified(string name)
    {
        Assert.Null(ExtraClassifier.Classify(name, CatalogType.Anime));
    }

    [Fact]
    public void Definitive_creditless_tokens_win_over_an_episode_marker()
    {
        var extra = ExtraClassifier.Classify("Show S01 NCED 1.mkv", CatalogType.Anime);

        Assert.NotNull(extra);
        Assert.Equal(ExtraKind.CreditlessEnding, extra.Kind);
    }

    [Fact]
    public void Movie_catalogs_never_classify()
    {
        // The shorthand tokens are too ambiguous against movie titles ("Special 26", "The Menu").
        Assert.Null(ExtraClassifier.Classify("Special 26.mkv", CatalogType.Movie));
        Assert.Null(ExtraClassifier.Classify("Show NCOP.mkv", CatalogType.Movie));
    }
}
