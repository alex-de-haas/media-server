using MediaServer.Api.Remux;
using System.Text;

namespace MediaServer.Api.Tests.Remux;

public sealed class SubtitleTextTests
{
    private static string Convert(string payload, string codec) =>
        SubtitleText.Convert(Encoding.UTF8.GetBytes(payload), codec);

    [Theory]
    [InlineData("S_TEXT/UTF8", true)]
    [InlineData("S_TEXT/ASS", true)]
    [InlineData("S_TEXT/SSA", true)]
    [InlineData("S_HDMV/PGS", false)]
    [InlineData("S_VOBSUB", false)]
    public void Only_text_formats_are_convertible(string codec, bool expected) =>
        Assert.Equal(expected, SubtitleText.IsConvertible(codec));

    [Fact]
    public void SubRip_keeps_its_words_and_loses_its_markup()
    {
        Assert.Equal("Come with me", Convert("<i>Come with me</i>", "S_TEXT/UTF8"));
        Assert.Equal("Two\nlines", Convert("Two\r\nlines", "S_TEXT/UTF8"));
        Assert.Equal("Loud", Convert("<font color=\"#ff0000\">Loud</font>", "S_TEXT/UTF8"));
    }

    [Fact]
    public void An_ass_row_gives_up_only_its_text()
    {
        // ReadOrder, Layer, Style, Name, MarginL, MarginR, MarginV, Effect, then the line.
        Assert.Equal(
            "This is the line",
            Convert("0,0,Default,,0,0,0,,This is the line", "S_TEXT/ASS"));
    }

    [Fact]
    public void Ass_override_blocks_are_instructions_and_not_words()
    {
        Assert.Equal(
            "Hello there",
            Convert(@"0,0,Default,,0,0,0,,{\pos(400,570)\fad(200,0)}Hello there", "S_TEXT/ASS"));
    }

    [Fact]
    public void Ass_line_breaks_become_real_ones()
    {
        Assert.Equal("First\nSecond", Convert(@"0,0,Default,,0,0,0,,First\NSecond", "S_TEXT/ASS"));
    }

    [Fact]
    public void A_comma_in_the_text_is_not_mistaken_for_a_field_separator()
    {
        Assert.Equal(
            "Well, yes, of course",
            Convert("0,0,Default,,0,0,0,,Well, yes, of course", "S_TEXT/ASS"));
    }

    [Fact]
    public void A_row_that_is_not_the_expected_shape_is_shown_rather_than_dropped()
    {
        Assert.Equal("no fields here", Convert("no fields here", "S_TEXT/ASS"));
    }
}
