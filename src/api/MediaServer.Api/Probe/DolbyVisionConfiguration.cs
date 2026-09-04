using System.Buffers.Binary;

namespace MediaServer.Api.Probe;

/// <summary>
/// What a Dolby Vision configuration record says about a video stream, in the fields a player's behaviour
/// turns on. <see cref="Profile"/> 7 with <see cref="ElPresent"/> is a UHD Blu-ray's dual layer, which
/// Apple TV and Infuse play as HDR10; profile 8 with <see cref="BlSignalCompatibilityId"/> 1 is the single
/// layer they play as Dolby Vision; profile 5 has no viewable base layer at all.
/// </summary>
public sealed record DolbyVisionDetail(int Profile, int Level, int BlSignalCompatibilityId, bool ElPresent);

/// <summary>
/// Reads the 24-byte <c>DOVIDecoderConfigurationRecord</c> — the payload of an MP4 <c>dvcC</c>/<c>dvvC</c>
/// box and of a Matroska <c>BlockAdditionMapping</c>'s extra data — which both container readers and the
/// remux path have in hand. ffprobe's <c>dv_profile</c> and friends are these same bits.
/// <para>
/// Layout: version major (8), version minor (8), profile (7), level (6), rpu_present (1), el_present (1),
/// bl_present (1), bl_signal_compatibility_id (4), then reserved bits. Only the first five bytes carry
/// anything read here, but a record shorter than that is not a record.
/// </para>
/// </summary>
public static class DolbyVisionConfiguration
{
    public const int Length = 24;

    public static DolbyVisionDetail? Parse(ReadOnlySpan<byte> record)
    {
        if (record.Length < 5)
        {
            return null;
        }

        var packed = BinaryPrimitives.ReadUInt16BigEndian(record[2..4]);
        var profile = packed >> 9;
        var level = (packed >> 3) & 0x3F;
        var elPresent = ((packed >> 1) & 0x01) == 1;
        var compatibility = record[4] >> 4;
        return new DolbyVisionDetail(profile, level, compatibility, elPresent);
    }

    /// <summary>Whether a record describes a stream a single-layer decoder can take as Dolby Vision: profile
    /// 5, or profile 8 with an HDR10 (1) or HLG (4) base layer. Profile 7's enhancement layer is what no Apple
    /// device decodes, and the 8.2 SDR base is not a picture anyone asked for.</summary>
    public static bool IsSingleLayerPlayable(DolbyVisionDetail detail) =>
        detail.Profile == 5 || (detail.Profile == 8 && detail.BlSignalCompatibilityId is 1 or 4);
}
