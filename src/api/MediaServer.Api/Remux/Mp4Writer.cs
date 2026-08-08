using System.Buffers.Binary;
using System.Text;

namespace MediaServer.Api.Remux;

/// <summary>
/// The ISO base media boxes the synthesiser assembles, and the two codec descriptors it cannot copy.
/// Kept apart from <see cref="Mp4Synthesizer"/> so that the assembly reads as a shape and not as byte
/// arithmetic.
/// </summary>
internal static class Mp4Writer
{
    /// <summary>Every track keeps time in nanoseconds, which divides both film and audio rates exactly.</summary>
    internal const int Timescale = 1_000_000_000;

    internal static byte[] Box(string type, params byte[][] parts)
    {
        var payload = parts.SelectMany(part => part).ToArray();
        var size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(8 + payload.Length));
        return [.. size, .. Encoding.ASCII.GetBytes(type), .. payload];
    }

    internal static byte[] Full(string type, byte version, uint flags, params byte[][] parts)
    {
        var head = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(head, flags);
        head[0] = version;
        return Box(type, [head, .. parts]);
    }

    internal static byte[] U16(ushort value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        return buffer;
    }

    internal static byte[] U32(uint value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        return buffer;
    }

    internal static byte[] I32(int value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        return buffer;
    }

    internal static byte[] U64(ulong value)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buffer, value);
        return buffer;
    }

    /// <summary>The identity transform every track carries unless it is rotated, which none here are.</summary>
    internal static byte[] UnityMatrix() =>
        [.. I32(0x10000), .. I32(0), .. I32(0), .. I32(0), .. I32(0x10000), .. I32(0),
         .. I32(0), .. I32(0), .. I32(0x40000000)];

    // ---- codec descriptors -------------------------------------------------------------------------

    /// <summary>
    /// What a Matroska <c>CodecID</c> maps to: the box its <c>CodecPrivate</c> already is, and the sample
    /// entry to write when Dolby Vision is not being asked for. The configuration record is carried
    /// verbatim in both cases — Matroska stores exactly the bytes the MP4 box wants.
    /// </summary>
    internal static (string ConfigurationBox, string SampleEntry)? VideoCodec(string codecId) => codecId switch
    {
        "V_MPEGH/ISO/HEVC" => ("hvcC", "hvc1"),
        "V_MPEG4/ISO/AVC" => ("avcC", "avc1"),
        _ => null,
    };

    private static readonly int[] Ac3SampleRates = [48000, 44100, 32000];
    private static readonly int[] Ac3Channels = [2, 1, 2, 3, 3, 4, 4, 5];

    internal readonly record struct Ac3Description(byte[] Dac3, int SampleRate, int Channels);

    /// <summary>
    /// Enough of an AC-3 sync frame to build <c>dac3</c>, the descriptor an MP4 audio track needs and the
    /// one thing Matroska does not carry: an AC-3 track there has no <c>CodecPrivate</c> at all, because
    /// every frame restates its own parameters.
    /// </summary>
    internal static Ac3Description? DescribeAc3(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 8 || frame[0] != 0x0B || frame[1] != 0x77)
        {
            return null;
        }

        var bits = new BitReader(frame);
        bits.Read(16);                              // syncword
        bits.Read(16);                              // crc1
        var fscod = bits.Read(2);
        var frmsizecod = bits.Read(6);
        var bsid = bits.Read(5);
        var bsmod = bits.Read(3);
        var acmod = bits.Read(3);
        if ((acmod & 1) != 0 && acmod != 1)
        {
            bits.Read(2);                           // cmixlev
        }

        if ((acmod & 4) != 0)
        {
            bits.Read(2);                           // surmixlev
        }

        if (acmod == 2)
        {
            bits.Read(2);                           // dsurmod
        }

        var lfeon = bits.Read(1);
        var bitRateCode = frmsizecod >> 1;

        var packed = (fscod << 22) | (bsid << 17) | (bsmod << 14) | (acmod << 11)
                     | (lfeon << 10) | (bitRateCode << 5);
        var dac3 = U32((uint)packed)[1..];           // the record is three bytes

        return new Ac3Description(
            dac3,
            fscod < 3 ? Ac3SampleRates[fscod] : 48000,
            Ac3Channels[acmod] + lfeon);
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _position;

        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                var bit = (_data[_position >> 3] >> (7 - (_position & 7))) & 1;
                value = (value << 1) | bit;
                _position++;
            }

            return value;
        }
    }
}
