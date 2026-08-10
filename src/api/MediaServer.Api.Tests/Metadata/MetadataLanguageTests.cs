using MediaServer.Api.Metadata;

namespace MediaServer.Api.Tests.Metadata;

/// <summary>
/// The shared display-language helpers: which cached record a read surface renders, and how it orders the
/// titles it rendered. Both are reached from every listing, so the degenerate inputs matter as much as the
/// ordinary ones.
/// </summary>
public sealed class MetadataLanguageTests
{
    private static string Pick(string preferred, params string[] languages) =>
        MetadataLanguage.Pick(languages, preferred, language => language);

    [Fact]
    public void Pick_prefers_the_exact_tag()
    {
        Assert.Equal("ru-RU", Pick("ru-RU", "en-US", "ru-RU", "ru-UA"));
    }

    [Fact]
    public void Pick_falls_back_to_the_same_primary_subtag()
    {
        Assert.Equal("ru-UA", Pick("ru-RU", "en-US", "ru-UA"));
        Assert.Equal("ja", Pick("ja-JP", "en-US", "ja"));
    }

    /// <summary>
    /// Matching on the first two characters would read "fil-PH" as Finnish and hand back a Finnish record
    /// for a Filipino library. The comparison is on the whole primary subtag instead.
    /// </summary>
    [Fact]
    public void Pick_does_not_match_a_different_language_that_shares_two_letters()
    {
        Assert.Equal("en-US", Pick("fil-PH", "en-US", "fi-FI"));
        Assert.Equal("fil-PH", Pick("fil", "fi-FI", "fil-PH"));
    }

    [Fact]
    public void Pick_falls_back_to_the_first_record_when_no_language_matches()
    {
        Assert.Equal("en-US", Pick("ru-RU", "en-US", "ja"));
    }

    /// <summary>
    /// Every caller either checks for emptiness first or iterates groups, which are never empty — so an
    /// empty set is a caller bug and says so, rather than surfacing as an index-out-of-range.
    /// </summary>
    [Fact]
    public void Pick_rejects_an_empty_record_set()
    {
        var empty = Array.Empty<string>();

        var error = Assert.Throws<ArgumentException>(
            () => MetadataLanguage.Pick(empty, "en-US", language => language));
        Assert.Equal("records", error.ParamName);
    }

    [Fact]
    public void TitleOrder_ignores_case()
    {
        var order = MetadataLanguage.TitleOrder("en-US");

        Assert.True(order.Compare("alien", "Zulu") < 0);
        Assert.True(order.Compare("the Matrix", "Zulu") < 0);
        Assert.Equal(0, order.Compare("Alien", "alien"));
    }

    /// <summary>
    /// A listing has to come back ordered whatever the configured tag looks like. Config parsing drops
    /// blank entries, but a tag the host has no culture for can still arrive, and it must degrade to
    /// invariant ordering rather than fail the request.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-language-tag")]
    public void TitleOrder_falls_back_to_invariant_ordering_for_an_unusable_tag(string? language)
    {
        var order = MetadataLanguage.TitleOrder(language);

        Assert.True(order.Compare("Alien", "Zulu") < 0);
        Assert.Equal(0, order.Compare("Alien", "alien"));
    }
}
