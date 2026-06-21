using MediaServer.Api.Organizer;

namespace MediaServer.Api.Tests.Organizer;

public sealed class EditionLabelerTests
{
    [Fact]
    public void Labels_a_known_tag_against_a_plain_sibling()
    {
        var labels = EditionLabeler.Label(
        [
            ".incoming/x/Spider-Noir.BW.S01E04.1080p.rus.LostFilm.TV.mkv",
            ".incoming/x/Spider-Noir.S01E04.1080p.rus.LostFilm.TV.mkv",
        ]);

        Assert.Equal(["Black & White", "Standard"], labels);
    }

    [Fact]
    public void Labels_two_known_tags_distinctly()
    {
        var labels = EditionLabeler.Label(
        [
            ".incoming/x/Movie.2024.2160p.HDR.mkv",
            ".incoming/x/Movie.2024.1080p.SDR.mkv",
        ]);

        // The shared title/year tokens drop out; only the differing quality tags remain.
        Assert.Contains("HDR", labels[0]);
        Assert.Contains("SDR", labels[1]);
        Assert.Equal(labels.Distinct().Count(), labels.Count);
    }

    [Fact]
    public void Falls_back_to_ordinals_when_no_known_tag_distinguishes()
    {
        var labels = EditionLabeler.Label(
        [
            ".incoming/x/Movie.GroupA.mkv",
            ".incoming/x/Movie.GroupB.mkv",
        ]);

        Assert.Equal(["Version 1", "Version 2"], labels);
    }

    [Fact]
    public void Always_returns_distinct_nonempty_labels()
    {
        var labels = EditionLabeler.Label(
        [
            ".incoming/x/a.mkv",
            ".incoming/x/b.mkv",
            ".incoming/x/c.mkv",
        ]);

        Assert.Equal(3, labels.Count);
        Assert.All(labels, label => Assert.False(string.IsNullOrWhiteSpace(label)));
        Assert.Equal(labels.Distinct().Count(), labels.Count);
    }
}
