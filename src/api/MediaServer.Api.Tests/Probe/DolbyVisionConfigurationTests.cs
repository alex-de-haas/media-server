using MediaServer.Api.Probe;

namespace MediaServer.Api.Tests.Probe;

/// <summary>
/// The 24-byte configuration record, read the way both container readers and the remux path read it. The
/// fixtures are the records of real files: a UHD Blu-ray remux (profile 7, level 6, enhancement layer,
/// compatibility id 6), a WEB-DL (profile 8, compatibility id 1), and the two other shapes a player's
/// behaviour turns on.
/// </summary>
public sealed class DolbyVisionConfigurationTests
{
    /// <summary>Profile 7, level 6, rpu/el/bl present, base-layer compatibility id 6 — Starship Troopers (1997).</summary>
    public static readonly byte[] Profile7 = [0x01, 0x00, 0x0E, 0x37, 0x60, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>Profile 8, level 6, rpu and bl present, compatibility id 1 — Avatar (2009).</summary>
    public static readonly byte[] Profile81 = [0x01, 0x00, 0x10, 0x35, 0x10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>Profile 8 with an HLG base layer (compatibility id 4).</summary>
    public static readonly byte[] Profile84 = [0x01, 0x00, 0x10, 0x35, 0x40, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>Profile 5: no compatible base layer at all.</summary>
    public static readonly byte[] Profile5 = [0x01, 0x00, 0x0A, 0x35, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void Reads_a_disc_remux_as_a_dual_layer_profile_7()
    {
        var record = DolbyVisionConfiguration.Parse(Profile7);

        Assert.Equal(new DolbyVisionDetail(7, 6, 6, ElPresent: true), record);
    }

    [Fact]
    public void Reads_a_web_release_as_a_single_layer_profile_8_1()
    {
        var record = DolbyVisionConfiguration.Parse(Profile81);

        Assert.Equal(new DolbyVisionDetail(8, 6, 1, ElPresent: false), record);
    }

    [Fact]
    public void Reads_the_fields_that_are_not_bytes_of_their_own()
    {
        // The profile is the top seven bits of byte 2, the level straddles bytes 2 and 3, the flags are
        // the low three bits of byte 3 and the compatibility id the high nibble of byte 4.
        Assert.Equal(4, DolbyVisionConfiguration.Parse(Profile84)!.BlSignalCompatibilityId);
        Assert.Equal(5, DolbyVisionConfiguration.Parse(Profile5)!.Profile);
        Assert.Equal(0, DolbyVisionConfiguration.Parse(Profile5)!.BlSignalCompatibilityId);
    }

    [Fact]
    public void Only_the_first_five_bytes_are_needed_and_fewer_is_not_a_record()
    {
        Assert.Equal(7, DolbyVisionConfiguration.Parse(Profile7.AsSpan(0, 5))!.Profile);
        Assert.Null(DolbyVisionConfiguration.Parse(Profile7.AsSpan(0, 4)));
        Assert.Null(DolbyVisionConfiguration.Parse([]));
    }

    [Theory]
    [InlineData(5, 0, false, true)]
    [InlineData(8, 1, false, true)]
    [InlineData(8, 4, false, true)]
    [InlineData(8, 2, false, false)]
    [InlineData(7, 6, true, false)]
    [InlineData(4, 0, false, false)]
    public void A_single_layer_decoder_plays_profile_5_and_the_hdr_bases_of_profile_8(int profile, int compatibility, bool el, bool playable) =>
        Assert.Equal(playable, DolbyVisionConfiguration.IsSingleLayerPlayable(new DolbyVisionDetail(profile, 6, compatibility, el)));
}
