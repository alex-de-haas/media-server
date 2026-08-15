using MediaServer.Api.Data;
using MediaServer.Api.Metadata;

namespace MediaServer.Api.Tests.Metadata;

/// <summary>
/// The artwork ranking every read surface shares. The rules under test are per-role and deliberately not the
/// same: a poster must carry a title, a backdrop must not, and a logo sits between the two. See
/// <c>docs/features/artwork-language/feature.md</c>.
/// </summary>
public sealed class ImageSelectionTests
{
    private const string Russian = "ru-RU";

    [Fact]
    public void Poster_prefers_the_display_language_over_english_and_textless_art()
    {
        var images = Images(
            (ImageType.Primary, null, 0, "textless"),
            (ImageType.Primary, "en", 1, "english"),
            (ImageType.Primary, "ru", 2, "russian"));

        Assert.Equal("russian", images.Best(ImageType.Primary, Russian)?.Tag);
    }

    [Fact]
    public void Poster_prefers_english_over_textless_art_when_the_display_language_has_none()
    {
        // The heart of the change: a titled poster in the wrong language identifies a film, a beautiful
        // wordless one does not — and textless art is exactly what TMDb's "no language" tag means.
        var images = Images(
            (ImageType.Primary, null, 0, "textless"),
            (ImageType.Primary, "en", 1, "english"));

        Assert.Equal("english", images.Best(ImageType.Primary, Russian)?.Tag);
    }

    [Fact]
    public void Poster_prefers_any_titled_poster_over_textless_art()
    {
        var images = Images(
            (ImageType.Primary, null, 0, "textless"),
            (ImageType.Primary, "ja", 1, "japanese"));

        Assert.Equal("japanese", images.Best(ImageType.Primary, Russian)?.Tag);
    }

    [Fact]
    public void Poster_falls_back_to_textless_art_when_that_is_all_there_is()
    {
        var images = Images((ImageType.Primary, null, 3, "textless"));

        Assert.Equal("textless", images.Best(ImageType.Primary, Russian)?.Tag);
    }

    [Fact]
    public void An_empty_language_ranks_as_textless_rather_than_as_a_foreign_language()
    {
        // TMDb's iso_639_1 is stored verbatim, and an empty string means the same thing as null: no text on
        // the image. Treating it as a foreign language would rank it above real English art.
        var images = Images(
            (ImageType.Primary, "", 0, "empty"),
            (ImageType.Primary, "en", 1, "english"));

        Assert.Equal("english", images.Best(ImageType.Primary, Russian)?.Tag);
    }

    [Fact]
    public void The_providers_explicit_no_language_code_ranks_as_textless()
    {
        // TMDb labels the option "No Language (xx-XX)" and returns `xx` when it was set deliberately, null
        // when it was never set. Reading `xx` as a foreign language is what broke backdrop selection in other
        // clients: the textless backdrop — the one a surface actually wants — would rank last.
        var posters = Images(
            (ImageType.Primary, "xx", 0, "textless"),
            (ImageType.Primary, "en", 1, "english"));
        var backdrops = Images(
            (ImageType.Backdrop, "ru", 0, "russian"),
            (ImageType.Backdrop, "xx", 1, "textless"));

        Assert.Equal("english", posters.Best(ImageType.Primary, Russian)?.Tag);
        Assert.Equal("textless", backdrops.Best(ImageType.Backdrop, Russian)?.Tag);
    }

    [Fact]
    public void Backdrop_prefers_textless_art_because_the_surface_draws_its_own_title()
    {
        var images = Images(
            (ImageType.Backdrop, "ru", 0, "russian"),
            (ImageType.Backdrop, null, 1, "textless"));

        Assert.Equal("textless", images.Best(ImageType.Backdrop, Russian)?.Tag);
    }

    [Fact]
    public void Logo_keeps_display_then_english_then_neutral()
    {
        var images = Images(
            (ImageType.Logo, "ja", 0, "japanese"),
            (ImageType.Logo, null, 1, "neutral"),
            (ImageType.Logo, "en", 2, "english"));

        Assert.Equal("english", images.Best(ImageType.Logo, "en-US")?.Tag);
        // Russian has no logo here, so English is next — ahead of the neutral wordmark, which only wins
        // when the alternative is a language the reader cannot read (the case below).
        Assert.Equal("english", images.Best(ImageType.Logo, Russian)?.Tag);
    }

    [Fact]
    public void A_neutral_logo_outranks_one_in_a_language_the_reader_cannot_read()
    {
        var images = Images(
            (ImageType.Logo, "ja", 0, "japanese"),
            (ImageType.Logo, null, 1, "neutral"));

        Assert.Equal("neutral", images.Best(ImageType.Logo, Russian)?.Tag);
    }

    [Fact]
    public void A_three_letter_language_does_not_match_a_two_letter_prefix()
    {
        // fil-PH is Filipino; reading it as Finnish is the bug MetadataLanguage exists to avoid, and the
        // artwork ranking must not reintroduce it by comparing two characters.
        var images = Images(
            (ImageType.Primary, "fi", 0, "finnish"),
            (ImageType.Primary, "en", 1, "english"));

        Assert.Equal("english", images.Best(ImageType.Primary, "fil-PH")?.Tag);
    }

    [Fact]
    public void A_language_is_matched_case_insensitively()
    {
        var images = Images(
            (ImageType.Primary, null, 0, "textless"),
            (ImageType.Primary, "RU", 1, "russian"));

        Assert.Equal("russian", images.Best(ImageType.Primary, Russian)?.Tag);
    }

    [Fact]
    public void Candidates_in_one_tier_keep_the_providers_order()
    {
        var images = Images(
            (ImageType.Primary, "ru", 2, "third"),
            (ImageType.Primary, "ru", 0, "first"),
            (ImageType.Primary, "ru", 1, "second"));

        Assert.Equal(
            ["first", "second", "third"],
            images.InPreferenceOrder(ImageType.Primary, Russian).Select(image => image.Tag));
    }

    [Fact]
    public void A_shared_sort_order_is_broken_deterministically_rather_than_by_chance()
    {
        // A re-enrich never renumbers rows it already stored, so a poster added later can arrive holding a
        // sort order an incumbent already has. Without a final key the winner would be whatever order the
        // rows came back in.
        var forwards = Images(
            (ImageType.Primary, "ru", 0, "aaa"),
            (ImageType.Primary, "ru", 0, "bbb"));
        var backwards = Images(
            (ImageType.Primary, "ru", 0, "bbb"),
            (ImageType.Primary, "ru", 0, "aaa"));

        Assert.Equal("aaa", forwards.Best(ImageType.Primary, Russian)?.Tag);
        Assert.Equal("aaa", backwards.Best(ImageType.Primary, Russian)?.Tag);
    }

    [Fact]
    public void A_pinned_poster_outranks_every_tier()
    {
        var images = Images(
            (ImageType.Primary, "ru", 0, "russian"),
            (ImageType.Primary, null, 9, "textless"));

        Assert.Equal("textless", images.Best(ImageType.Primary, Russian, pinnedTag: "textless")?.Tag);
    }

    [Fact]
    public void A_pin_that_matches_nothing_leaves_the_ranking_in_charge()
    {
        // A pin survives a re-enrich, but the image behind it can be withdrawn by the provider. A dangling
        // pin must degrade to the ranking rather than blank the poster.
        var images = Images(
            (ImageType.Primary, "ru", 0, "russian"),
            (ImageType.Primary, null, 1, "textless"));

        Assert.Equal("russian", images.Best(ImageType.Primary, Russian, pinnedTag: "withdrawn")?.Tag);
    }

    [Fact]
    public void Only_the_requested_role_is_considered()
    {
        var images = Images(
            (ImageType.Backdrop, "ru", 0, "backdrop"),
            (ImageType.Logo, "ru", 0, "logo"));

        Assert.Null(images.Best(ImageType.Primary, Russian));
        Assert.Equal("backdrop", images.Best(ImageType.Backdrop, Russian)?.Tag);
    }

    [Fact]
    public void Tier_ranks_a_projected_row_the_same_way_as_a_loaded_entity()
    {
        // The surfaces that project a narrow row shape rank through Tier; it must agree with the extension
        // methods or the two would disagree about the same image.
        Assert.True(
            ImageSelection.Tier(ImageType.Primary, "ru", Russian) <
            ImageSelection.Tier(ImageType.Primary, null, Russian));
        Assert.True(
            ImageSelection.Tier(ImageType.Backdrop, null, Russian) <
            ImageSelection.Tier(ImageType.Backdrop, "ru", Russian));
    }

    private static List<ImageAsset> Images(params (ImageType Type, string? Language, int SortOrder, string Tag)[] images) =>
        images.Select(image => new ImageAsset
        {
            Id = Guid.NewGuid(),
            MediaItemId = Guid.Empty,
            ImageType = image.Type,
            Language = image.Language,
            Provider = "tmdb",
            RemotePath = $"https://image.tmdb.org/{image.Tag}.jpg",
            Tag = image.Tag,
            SortOrder = image.SortOrder,
        }).ToList();
}
