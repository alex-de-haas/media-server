using MediaServer.Api.Mux;

namespace MediaServer.Api.Tests.Mux;

public sealed class AudioTrackLabelerTests
{
    /// <summary>One companion's title — the labeller works on a whole set, so a lone path is a set of one.</summary>
    private static string? Title(string companion, string video) =>
        AudioTrackLabeler.InferTitles([companion], video)[0];

    /// <summary>Titles for a video's companions, in the order given.</summary>
    private static IReadOnlyList<string?> Titles(string video, params string[] companions) =>
        AudioTrackLabeler.InferTitles(companions, video);

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
        Assert.Equal("Rus Sound [AniLibria]", Title(".incoming/x/FMA/Rus Sound [AniLibria]/FMA 01.mka",
            ".incoming/x/FMA/FMA 01.mkv"));
    }

    [Fact]
    public void A_track_next_to_its_video_has_no_title()
    {
        Assert.Null(Title(".incoming/x/Movie/Movie.rus.ac3",
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
        Assert.Equal(expected, Title($".incoming/x/{folder}/{Name}.mka", $".incoming/x/{Name}.mkv"));
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
        Assert.Null(Title($".incoming/x/{folder}/{Name}.ass", $".incoming/x/{Name}.mkv"));
    }

    // Layout 2: everything flat, the label carried as a suffix on the file name.
    [Theory]
    [InlineData("The Rock (1996).rus.AniDUB.mka", "AniDUB")]
    [InlineData("The Rock (1996).rus.Get Smart.mka", "Get Smart")]
    [InlineData("The Rock (1996).Гаврилов.mka", "Гаврилов")]
    [InlineData("The Rock (1996).rus.MVO Дубляжная.mka", "MVO Дубляжная")]
    public void The_name_suffix_titles_a_track_in_a_flat_layout(string companion, string expected) =>
        Assert.Equal(expected, Title($".incoming/x/{companion}", ".incoming/x/The Rock (1996).mkv"));

    [Theory]
    // Nothing but a language, or a language and a subtitle flag, is not a title.
    [InlineData("The Rock (1996).rus.srt")]
    [InlineData("The Rock (1996).rus.forced.srt")]
    [InlineData("The Rock (1996).eng.sdh.ass")]
    [InlineData("The Rock (1996).mka")]
    public void A_suffix_of_only_language_and_flags_yields_no_title(string companion) =>
        Assert.Null(Title($".incoming/x/{companion}", ".incoming/x/The Rock (1996).mkv"));

    [Fact]
    public void The_name_wins_over_the_folder()
    {
        // A grouped release whose files also carry a suffix: the more specific label is in the name.
        Assert.Equal("Gavrilov", Title(".incoming/x/RUS Sound/Movie.rus.Gavrilov.mka", ".incoming/x/Movie.mkv"));
    }

    [Theory]
    // Layout 3: everything flat in the video's own folder, each track named by nothing but its author.
    // "Побег из Нью-Йорка" ships four авторских перевода this way, and the name is the only thing telling
    // them apart — the folder is shared with the video and says nothing.
    [InlineData("Володарский.ac3", "Володарский")]
    [InlineData("Гаврилов.ac3", "Гаврилов")]
    [InlineData("Сербин.dts", "Сербин")]
    public void A_track_named_only_for_its_author_is_titled_by_its_own_name(string companion, string expected) =>
        Assert.Equal(expected, Title($".incoming/x/Побег из Нью-Йорка/{companion}",
            ".incoming/x/Побег из Нью-Йорка/Побег из Нью-Йорка. BDRip 1080p.mkv"));

    [Theory]
    // A name or folder that only restates the release says nothing, however its punctuation differs from
    // the organized video's — otherwise the label would just echo the film's own title. This matters
    // because by labelling time the video has been organized, so the folders always differ.
    [InlineData("Some.Movie.2020/Some.Movie.2020.mka")]
    [InlineData("Some.Movie.2020/Some Movie 2020.mka")]
    [InlineData("Some.Movie.2020/Some.Movie.2020.rus.mka")]
    [InlineData("Some Movie 2020/dub.rus.mka")]
    public void A_name_or_folder_that_only_repeats_the_video_is_not_a_title(string companion) =>
        Assert.Null(Title($".incoming/dl1/{companion}", "Some Movie (2020)/Some Movie (2020).mkv"));

    [Fact]
    public void The_group_folder_still_wins_over_a_name_that_repeats_the_release()
    {
        // The staging name differs from the organized one only in punctuation, so the name matches nothing
        // and carries nothing — but the folder names the dub group, which is the real label.
        Assert.Equal("[AniDUB]", Title(".incoming/dl1/Some.Movie.2020/RUS Sound/[AniDUB]/Some.Movie.2020.mka",
            "Some Movie (2020)/Some Movie (2020).mkv"));
    }

    // ---- the set is what reveals which component carries the labels ----

    [Fact]
    public void When_the_names_vary_they_are_the_labels()
    {
        // "Побег из Нью-Йорка": four авторских перевода in the film's own folder. Only the names differ,
        // and by labelling time the video has been organized under its English title — so the staging folder
        // shares no words with it and would otherwise be taken for a label.
        const string Video = "Escape from New York (1981)/Escape from New York (1981).mkv";
        const string Folder = ".incoming/dl1/Побег из Нью-Йорка";

        Assert.Equal(
            ["Володарский", "Гаврилов", "Горчаков", "Сербин"],
            Titles(Video,
                $"{Folder}/Володарский.ac3",
                $"{Folder}/Гаврилов.ac3",
                $"{Folder}/Горчаков.ac3",
                $"{Folder}/Сербин.dts"));
    }

    [Fact]
    public void When_the_folders_vary_they_are_the_labels()
    {
        // The mirror case: every file carries the video's name and only the folder tells them apart.
        const string Video = "Some Movie (2020)/Some Movie (2020).mkv";
        const string Root = ".incoming/dl1/Some.Movie.2020/RUS Sound";

        Assert.Equal(
            ["[AniDUB]", "[Get Smart]", "[MCA]"],
            Titles(Video,
                $"{Root}/[AniDUB]/Some.Movie.2020.mka",
                $"{Root}/[Get Smart]/Some.Movie.2020.mka",
                $"{Root}/[MCA]/Some.Movie.2020.mka"));
    }

    [Fact]
    public void A_release_that_labels_by_suffix_still_reads_from_the_names()
    {
        const string Video = "The Rock (1996)/The Rock (1996).mkv";

        Assert.Equal(
            ["Гаврилов", "Сербин"],
            Titles(Video,
                ".incoming/dl1/The Rock (1996).rus.Гаврилов.mka",
                ".incoming/dl1/The Rock (1996).rus.Сербин.mka"));
    }

    [Fact]
    public void Nothing_distinguishing_leaves_every_title_empty()
    {
        // Same folder, same name but for an index the labeller must not mistake for a label. The naming
        // step falls back to positions, which is honest — nothing here says who made either track.
        const string Video = "Some Movie (2020)/Some Movie (2020).mkv";

        Assert.All(
            Titles(Video,
                ".incoming/dl1/Some.Movie.2020/Some.Movie.2020.mka",
                ".incoming/dl1/Some.Movie.2020/Some Movie 2020.mka"),
            title => Assert.Null(title));
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
        Assert.Equal(title, Title($"Show/Season 01/{companion}", $"Show/Season 01/{Video}"));
    }
}
