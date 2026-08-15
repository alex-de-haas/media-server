using MediaServer.Api.Native.Playback;

namespace MediaServer.Api.Tests.Native;

/// <summary>
/// Whether a client can present what a file holds.
///
/// Two vocabularies meet here and nothing else makes them agree. A probe names what it can see — and the
/// header probe cannot tell HDR10 from HDR10+, so it says the generic "HDR". A client names the formats
/// it decodes, and never says that word. Against production, a header-probed HDR film was refused to a
/// television that would have played it.
/// </summary>
public sealed class DynamicRangeTests
{
    private static NativeCapabilityProfile Television => new(
        ["mp4"], ["hevc"], ["eac3"], ["SDR", "HDR10", "Dolby Vision"]);

    private static NativeCapabilityProfile SdrOnly => new(["mp4"], ["hevc"], ["eac3"], ["SDR"]);

    [Theory]
    [InlineData("SDR")]
    [InlineData(null)]
    [InlineData("HDR10")]
    [InlineData("Dolby Vision")]
    [InlineData("HDR10+")]
    [InlineData("HDR")]
    public void A_television_presents_everything_that_rests_on_hdr10(string? format)
    {
        Assert.True(NativePlaybackResolver.CanPresentFor(format, Television));
    }

    [Theory]
    [InlineData("HDR10")]
    [InlineData("Dolby Vision")]
    [InlineData("HDR10+")]
    [InlineData("HDR")]
    public void A_display_without_hdr_presents_none_of_them(string format)
    {
        Assert.False(NativePlaybackResolver.CanPresentFor(format, SdrOnly));
    }

    [Fact]
    public void An_sdr_file_plays_on_anything()
    {
        Assert.True(NativePlaybackResolver.CanPresentFor("SDR", SdrOnly));
        Assert.True(NativePlaybackResolver.CanPresentFor(null, SdrOnly));
    }

    [Fact]
    public void A_format_nothing_has_heard_of_is_refused_rather_than_assumed()
    {
        // Better a refusal a viewer can read than a picture nobody can watch.
        Assert.False(NativePlaybackResolver.CanPresentFor("HDR-Vivid", Television));
    }
}
