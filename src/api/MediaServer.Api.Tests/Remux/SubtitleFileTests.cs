using MediaServer.Api.Remux;

namespace MediaServer.Api.Tests.Remux;

public sealed class SubtitleFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("subtitle-file-tests").FullName;

    private IReadOnlyList<TextCue> Read(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return SubtitleFile.Read(path);
    }

    [Theory]
    [InlineData("a.srt", true)]
    [InlineData("a.ass", true)]
    [InlineData("a.ssa", true)]
    [InlineData("a.vtt", true)]
    [InlineData("a.sup", false)]
    [InlineData("a.idx", false)]
    public void Only_text_formats_are_convertible(string name, bool expected) =>
        Assert.Equal(expected, SubtitleFile.IsConvertible(name));

    [Fact]
    public void SubRip_gives_up_its_cues_with_their_timings()
    {
        var cues = Read("film.srt", """
            1
            00:00:20,000 --> 00:00:24,400
            Come with me

            2
            00:01:02,500 --> 00:01:04,000
            <i>Two</i>
            lines
            """);

        Assert.Equal(2, cues.Count);
        Assert.Equal(20_000, cues[0].Start);
        Assert.Equal(4_400, cues[0].Duration);
        Assert.Equal("Come with me", cues[0].Text);
        Assert.Equal(62_500, cues[1].Start);
        Assert.Equal("Two\nlines", cues[1].Text);      // markup off, the line break kept
    }

    [Fact]
    public void A_cue_numbered_or_not_reads_the_same()
    {
        var cues = Read("bare.srt", """
            00:00:01,000 --> 00:00:02,000
            No number above me
            """);

        Assert.Equal("No number above me", Assert.Single(cues).Text);
    }

    [Fact]
    public void WebVtt_timings_use_a_dot_and_are_read_all_the_same()
    {
        var cues = Read("film.vtt", """
            WEBVTT

            00:00:05.250 --> 00:00:07.000
            Dot, not comma
            """);

        var cue = Assert.Single(cues);
        Assert.Equal(5_250, cue.Start);
        Assert.Equal(1_750, cue.Duration);
    }

    [Fact]
    public void Ass_dialogue_gives_up_only_its_text()
    {
        var cues = Read("film.ass", """
            [Script Info]
            Title: Something

            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:03.00,0:00:05.50,Default,,0,0,0,,{\pos(400,570)}Hello, there
            Comment: 0,0:00:09.00,0:00:10.00,Default,,0,0,0,,Not a cue
            """);

        var cue = Assert.Single(cues);
        Assert.Equal(3_000, cue.Start);
        // ASS counts hundredths, so .50 is half a second rather than half a millisecond.
        Assert.Equal(2_500, cue.Duration);
        Assert.Equal("Hello, there", cue.Text);
    }

    [Fact]
    public void Cues_come_back_in_start_order_however_the_file_lists_them()
    {
        var cues = Read("shuffled.srt", """
            2
            00:00:30,000 --> 00:00:31,000
            Second

            1
            00:00:10,000 --> 00:00:11,000
            First
            """);

        Assert.Equal(["First", "Second"], cues.Select(cue => cue.Text));
    }

    [Fact]
    public void A_cue_with_no_duration_or_no_words_is_dropped()
    {
        var cues = Read("odd.srt", """
            1
            00:00:10,000 --> 00:00:10,000
            Zero length

            2
            00:00:20,000 --> 00:00:21,000


            3
            00:00:30,000 --> 00:00:31,000
            Kept
            """);

        Assert.Equal("Kept", Assert.Single(cues).Text);
    }

    [Fact]
    public void Something_that_is_not_a_subtitle_file_yields_nothing_rather_than_failing()
    {
        Assert.Empty(Read("junk.srt", "this file has no timings at all"));
        Assert.Empty(SubtitleFile.Read(Path.Combine(_root, "absent.srt")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
