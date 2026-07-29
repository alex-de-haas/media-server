using MediaServer.Api.Probe;

namespace MediaServer.Api.Tests.Probe;

/// <summary>
/// The language vocabulary an operator's input is validated against. The set served to the dialog has to be
/// what this service <b>accepts</b>, not what it stores: a client validating against the stored forms alone
/// is stricter than the API, which is the one direction a client-side check must never be wrong in.
/// </summary>
public sealed class LanguageTagsTests
{
    [Theory]
    [InlineData("rus", "rus")]
    [InlineData("ru", "rus")]
    [InlineData("deu", "ger")]
    [InlineData("de", "ger")]
    [InlineData("pt-BR", "por")]
    [InlineData(" RUS ", "rus")]
    public void Normalize_folds_every_accepted_spelling_onto_the_stored_one(string typed, string expected) =>
        Assert.Equal(expected, LanguageTags.Normalize(typed));

    [Theory]
    [InlineData("rsu")]
    [InlineData("russian")]
    [InlineData("")]
    [InlineData(null)]
    public void Normalize_refuses_what_it_does_not_recognize(string? typed) =>
        Assert.Null(LanguageTags.Normalize(typed));

    [Theory]
    [InlineData("rus")] // canonical
    [InlineData("ger")] // canonical, where the two standards disagree
    [InlineData("deu")] // the terminological spelling that folds onto it
    [InlineData("ru")]  // the ISO 639-1 pair
    [InlineData("de")]
    public void Accepted_carries_every_form_Normalize_takes(string tag) =>
        Assert.Contains(tag, LanguageTags.Accepted);

    [Fact]
    public void Accepted_and_Normalize_agree_on_every_entry()
    {
        // The list is what a client filters by, so anything in it must survive the service's own check —
        // otherwise the dialog offers a value the submit then refuses.
        var refused = LanguageTags.Accepted.Where(tag => LanguageTags.Normalize(tag) is null).ToList();

        Assert.Empty(refused);
    }
}
