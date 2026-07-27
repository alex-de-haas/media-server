using MediaServer.Api.Mux;

namespace MediaServer.Api.Tests.Mux;

public sealed class AudioTrackLabelerTests
{
    [Theory]
    [InlineData(".incoming/x/FMA/Rus Sound [AniLibria]/FMA 01.mka", "rus")]
    [InlineData(".incoming/x/FMA/RUSSIAN/FMA 01.mka", "rus")]
    [InlineData(".incoming/x/Movie/Movie.2020.eng.ac3", "eng")]
    [InlineData(".incoming/x/Show/Sound JPN/Show S01E01.mka", "jpn")]
    public void Infers_the_language_from_folder_or_filename_tokens(string path, string expected) =>
        Assert.Equal(expected, AudioTrackLabeler.InferLanguage(path));

    [Fact]
    public void The_filename_token_wins_over_the_folder()
    {
        Assert.Equal("rus", AudioTrackLabeler.InferLanguage(".incoming/x/Eng Sound/Show.rus.mka"));
    }

    [Theory]
    // No whole-token hit: two-letter codes are excluded so title words never mis-tag a track.
    [InlineData(".incoming/x/Is It Wrong to Pick Up Girls/01.mka")]
    [InlineData(".incoming/x/Attack on Titan/Attack on Titan 01.mka")]
    // "rus" inside a word is not a token.
    [InlineData(".incoming/x/Trust/Trust 01.mka")]
    public void Leaves_the_language_null_without_an_unambiguous_token(string path) =>
        Assert.Null(AudioTrackLabeler.InferLanguage(path));

    [Fact]
    public void The_tracks_own_folder_becomes_its_title()
    {
        Assert.Equal("Rus Sound [AniLibria]", AudioTrackLabeler.InferTitle(
            ".incoming/x/FMA/Rus Sound [AniLibria]/FMA 01.mka",
            ".incoming/x/FMA/FMA 01.mkv"));
    }

    [Fact]
    public void A_track_next_to_its_video_has_no_title()
    {
        Assert.Null(AudioTrackLabeler.InferTitle(
            ".incoming/x/Movie/Movie.rus.ac3",
            ".incoming/x/Movie/Movie.mkv"));
    }

    // Layout 1: per-group folders, every companion reusing the video's exact file name. Taken from
    // "Fullmetal Alchemist Brotherhood [BDRip] [1080p]", whose three dub groups differ only by folder.
    [Theory]
    [InlineData("RUS Sound/[AniDUB]", "[AniDUB]")]
    [InlineData("RUS Sound/[Get Smart]", "[Get Smart]")]
    [InlineData("RUS Sound/[MCA]", "[MCA]")]
    public void The_dub_group_folder_titles_a_track_that_reuses_the_videos_name(string folder, string expected)
    {
        const string Name = "[Yousei-raws] Fullmetal Alchemist Brotherhood 01 [BDrip 1920x1080 x264 FLAC]";
        Assert.Equal(expected, AudioTrackLabeler.InferTitle($".incoming/x/{folder}/{Name}.mka", $".incoming/x/{Name}.mkv"));
    }

    [Theory]
    // A folder that only classifies what is inside it names a bucket, not a track.
    [InlineData("RUS Subs")]
    [InlineData("Sound")]
    [InlineData("Russian")]
    [InlineData("Rus Sound")]
    public void A_folder_of_only_language_and_category_words_yields_no_title(string folder)
    {
        const string Name = "[Yousei-raws] Fullmetal Alchemist Brotherhood 01 [BDrip 1920x1080 x264 FLAC]";
        Assert.Null(AudioTrackLabeler.InferTitle($".incoming/x/{folder}/{Name}.ass", $".incoming/x/{Name}.mkv"));
    }

    // Layout 2: everything flat, the label carried as a suffix on the file name.
    [Theory]
    [InlineData("The Rock (1996).rus.AniDUB.mka", "AniDUB")]
    [InlineData("The Rock (1996).rus.Get Smart.mka", "Get Smart")]
    [InlineData("The Rock (1996).Гаврилов.mka", "Гаврилов")]
    [InlineData("The Rock (1996).rus.MVO Дубляжная.mka", "MVO Дубляжная")]
    public void The_name_suffix_titles_a_track_in_a_flat_layout(string companion, string expected) =>
        Assert.Equal(expected, AudioTrackLabeler.InferTitle(
            $".incoming/x/{companion}", ".incoming/x/The Rock (1996).mkv"));

    [Theory]
    // Nothing but a language, or a language and a subtitle flag, is not a title.
    [InlineData("The Rock (1996).rus.srt")]
    [InlineData("The Rock (1996).rus.forced.srt")]
    [InlineData("The Rock (1996).eng.sdh.ass")]
    [InlineData("The Rock (1996).mka")]
    public void A_suffix_of_only_language_and_flags_yields_no_title(string companion) =>
        Assert.Null(AudioTrackLabeler.InferTitle(
            $".incoming/x/{companion}", ".incoming/x/The Rock (1996).mkv"));

    [Fact]
    public void The_name_wins_over_the_folder()
    {
        // A grouped release whose files also carry a suffix: the more specific label is in the name.
        Assert.Equal("Gavrilov", AudioTrackLabeler.InferTitle(
            ".incoming/x/RUS Sound/Movie.rus.Gavrilov.mka", ".incoming/x/Movie.mkv"));
    }

    [Theory]
    // The names this feature writes must read back on a later catalog scan, without the database.
    [InlineData("Fullmetal Alchemist Brotherhood S01E01.rus.AniDUB.mka", "rus", "AniDUB")]
    [InlineData("Fullmetal Alchemist Brotherhood S01E01.rus.Get Smart.mka", "rus", "Get Smart")]
    [InlineData("Fullmetal Alchemist Brotherhood S01E01.rus.ass", "rus", null)]
    public void Its_own_output_reads_back(string companion, string language, string? title)
    {
        const string Video = "Fullmetal Alchemist Brotherhood S01E01.mkv";
        Assert.Equal(language, AudioTrackLabeler.InferLanguage($"Show/Season 01/{companion}"));
        Assert.Equal(title, AudioTrackLabeler.InferTitle($"Show/Season 01/{companion}", $"Show/Season 01/{Video}"));
    }
}
