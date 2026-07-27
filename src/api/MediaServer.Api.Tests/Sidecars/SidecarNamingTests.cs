using MediaServer.Api.Data;
using MediaServer.Api.Sidecars;

namespace MediaServer.Api.Tests.Sidecars;

/// <summary>
/// Naming the companion files that land beside a library file. The rule that matters is that a slug is the
/// exception rather than the default: a lone subtitle keeps the plain name clients match on, and only a
/// track that would collide with a sibling pays for one.
/// </summary>
public sealed class SidecarNamingTests
{
    private const string Video = "The Rock (1996).mkv";

    private static SourceFile File(string relativePath) =>
        new() { Id = Guid.NewGuid(), RelativePath = relativePath };

    private static IReadOnlyList<string> Names(
        params (string Path, string? Language, string? Title)[] companions) =>
        [.. SidecarNaming
            .For(Video, [.. companions.Select(entry => (File(entry.Path), entry.Language, entry.Title))])
            .Select(named => named.FileName)];

    [Fact]
    public void A_lone_track_keeps_the_plain_name_clients_match_on()
    {
        Assert.Equal(
            ["The Rock (1996).rus.srt"],
            Names((".incoming/x/subs.srt", "rus", null)));
    }

    [Fact]
    public void A_lone_track_with_a_title_still_needs_no_slug()
    {
        // Nothing would collide with it, and the conventional form is worth more than the label.
        Assert.Equal(
            ["The Rock (1996).rus.mka"],
            Names((".incoming/x/dub.mka", "rus", "Дубляж")));
    }

    [Fact]
    public void Several_tracks_in_one_language_are_told_apart_by_their_titles()
    {
        Assert.Equal(
            [
                "The Rock (1996).rus.Дубляж.mka",
                "The Rock (1996).rus.Диктор CDV.mka",
            ],
            Names(
                (".incoming/x/a.mka", "rus", "Дубляж"),
                (".incoming/x/b.mka", "rus", "Диктор CDV")));
    }

    [Fact]
    public void Audio_and_subtitles_do_not_crowd_each_other()
    {
        // One Russian dub and one Russian subtitle are not a collision — different kinds, different
        // extensions — so both keep the plain form.
        Assert.Equal(
            ["The Rock (1996).rus.mka", "The Rock (1996).rus.srt"],
            Names(
                (".incoming/x/dub.mka", "rus", "Дубляж"),
                (".incoming/x/subs.srt", "rus", "Forced")));
    }

    [Fact]
    public void Tracks_in_different_languages_do_not_crowd_each_other()
    {
        Assert.Equal(
            ["The Rock (1996).rus.mka", "The Rock (1996).eng.mka"],
            Names(
                (".incoming/x/a.mka", "rus", "Дубляж"),
                (".incoming/x/b.mka", "eng", "Original")));
    }

    [Fact]
    public void A_title_no_filesystem_accepts_is_made_safe()
    {
        // Taken from a real release: "|" is invalid on Windows, exFAT and SMB, and a dot would be read as
        // one of the name's own separators.
        var names = Names(
            (".incoming/x/a.mka", "rus", "DUB | DD5.1 @ 640 kbps"),
            (".incoming/x/b.mka", "rus", "Original | DD5.1 @ 640 kbps"));

        Assert.All(names, name =>
        {
            Assert.DoesNotContain('|', name);
            Assert.DoesNotContain(name.AsSpan(0, name.Length - 4).ToString(), "5.1");
        });
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_crowded_track_with_no_title_falls_back_to_its_position()
    {
        // Real releases do ship an untitled track next to titled ones — Mercy's English track has no title
        // at all — and something still has to tell them apart.
        var names = Names(
            (".incoming/x/a.mka", "rus", "MVO wMedia"),
            (".incoming/x/b.mka", "rus", null));

        Assert.Equal("The Rock (1996).rus.MVO wMedia.mka", names[0]);
        Assert.Equal("The Rock (1996).rus.2.mka", names[1]);
    }

    [Fact]
    public void Titles_that_sanitize_to_the_same_thing_still_get_distinct_names()
    {
        var names = Names(
            (".incoming/x/a.mka", "rus", "DUB|"),
            (".incoming/x/b.mka", "rus", "DUB"));

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void A_track_with_no_language_is_named_without_one()
    {
        Assert.Equal(["The Rock (1996).mka"], Names((".incoming/x/a.mka", null, null)));
    }

    [Fact]
    public void An_overlong_name_is_trimmed_to_fit_the_filesystem()
    {
        // 255 bytes, and Cyrillic costs two per character — so the budget is counted in bytes.
        var names = Names(
            (".incoming/x/a.mka", "rus", new string('Я', 200)),
            (".incoming/x/b.mka", "rus", new string('Ю', 200)));

        Assert.All(names, name => Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(name) <= 255,
            $"{name} is {System.Text.Encoding.UTF8.GetByteCount(name)} bytes"));
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_extension_is_kept_and_lowercased()
    {
        Assert.Equal(["The Rock (1996).rus.srt"], Names((".incoming/x/subs.SRT", "rus", null)));
    }

    [Theory]
    // Path.GetInvalidFileNameChars() answers for the runtime, and on Linux — which the container is — that
    // is only "/" and NUL. The library it writes into may be exFAT or SMB, or be opened from Windows, so
    // these have to go regardless of where the process happens to run.
    [InlineData("Vol: 2")]
    [InlineData("What? Really")]
    [InlineData("Star*Dub")]
    [InlineData("The \"Best\" Cut")]
    [InlineData("A<B>C")]
    [InlineData("Back\\Slash")]
    public void Characters_windows_and_exfat_refuse_are_removed_even_on_linux(string title)
    {
        var names = Names(
            (".incoming/x/a.mka", "rus", title),
            (".incoming/x/b.mka", "rus", "Other"));

        var name = names[0];
        Assert.DoesNotContain(name, character => ":*?\"<>\\/".Contains(character));
        Assert.False(name.EndsWith(". mka", StringComparison.Ordinal));
    }

    [Fact]
    public void A_title_that_sanitizes_to_nothing_falls_back_to_the_position()
    {
        var names = Names(
            (".incoming/x/a.mka", "rus", "???"),
            (".incoming/x/b.mka", "rus", "Real"));

        Assert.Equal("The Rock (1996).rus.1.mka", names[0]);
    }

    [Fact]
    public void A_sidecar_never_takes_the_videos_own_name()
    {
        // A companion whose language and extension would land it exactly on the video would overwrite it.
        var names = Names((".incoming/x/other.mkv", null, null));

        Assert.NotEqual(Video, Assert.Single(names));
    }
}
