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
}
