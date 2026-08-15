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

    [Theory]
    [InlineData("Dolby Vision \u00b7 HDR10")]
    [InlineData("Dolby Vision, HDR10")]
    [InlineData("HDR10 \u00b7 Dolby Vision")]
    public void A_value_naming_several_formats_is_presentable_when_any_of_them_is(string format)
    {
        // Production holds "Dolby Vision · HDR10" — what a profile 8.1 file honestly is. Compared whole
        // against a vocabulary of single names it matches nothing, and a television was refused a film
        // it would have played.
        Assert.True(NativePlaybackResolver.CanPresentFor(format, Television));
    }

    [Fact]
    public void A_compound_of_things_a_display_cannot_show_is_still_refused()
    {
        Assert.False(NativePlaybackResolver.CanPresentFor("Dolby Vision \u00b7 HDR10", SdrOnly));
    }

    [Fact]
    public void The_plus_in_hdr10_plus_is_part_of_the_name_rather_than_a_separator()
    {
        Assert.True(NativePlaybackResolver.CanPresentFor("HDR10+", Television));
        Assert.False(NativePlaybackResolver.CanPresentFor("HDR10+", SdrOnly));
    }

    [Theory]
    [InlineData("Dolby Vision")]
    [InlineData("Dolby Vision \u00b7 HDR10")]
    [InlineData("HDR10 \u00b7 Dolby Vision")]
    public void A_dolby_vision_source_is_signalled_as_dolby_vision(string format)
    {
        // The downgrade this guards against is silent: a compound compared whole against "Dolby Vision"
        // matches nothing, the film is signalled hvc1, and a television that can show Dolby Vision
        // quietly gets HDR10 instead. Which is the one thing this feature exists to deliver.
        Assert.Equal("dvh1", NativePlaybackResolver.SignallingForTest(format, Television));
    }

    [Theory]
    [InlineData("HDR10")]
    [InlineData("HDR")]
    [InlineData("HDR10+")]
    [InlineData("SDR")]
    [InlineData(null)]
    public void Everything_else_gets_the_cross_compatible_entry(string? format)
    {
        Assert.Equal("hvc1", NativePlaybackResolver.SignallingForTest(format, Television));
    }

    [Fact]
    public void A_client_that_cannot_show_dolby_vision_is_not_sent_it()
    {
        // The spike established that a dvh1 track on a device that cannot present it does not degrade —
        // it breaks. So this is not a preference.
        var hdr10Only = new NativeCapabilityProfile(["mp4"], ["hevc"], ["eac3"], ["SDR", "HDR10"]);

        Assert.Equal("hvc1", NativePlaybackResolver.SignallingForTest("Dolby Vision \u00b7 HDR10", hdr10Only));
    }

    [Fact]
    public void A_format_nothing_has_heard_of_is_refused_rather_than_assumed()
    {
        // Better a refusal a viewer can read than a picture nobody can watch.
        Assert.False(NativePlaybackResolver.CanPresentFor("HDR-Vivid", Television));
    }
}
