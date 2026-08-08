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

    /// <summary>
    /// What an E-AC-3 access unit says about itself. Unlike AC-3 a frame is not always 1536 samples —
    /// it carries one, two, three or six blocks of 256 — so the duration is read rather than assumed.
    /// </summary>
    internal readonly record struct Eac3Description(
        byte[] Dec3, int SampleRate, int Channels, int SamplesPerFrame);

    private static readonly int[] Eac3HalfRates = [24000, 22050, 16000];
    private static readonly int[] Eac3Blocks = [1, 2, 3, 6];

    /// <summary>
    /// Reads an E-AC-3 access unit into the <c>dec3</c> descriptor an <c>ec-3</c> track needs.
    ///
    /// The unit may hold several substreams — one independent, then any dependent ones carrying the extra
    /// channels — each with its own sync frame. They are walked by their stated sizes so the dependent
    /// ones can be counted, which is what <c>dec3</c> wants to know and what a decoder needs in order to
    /// find them.
    /// </summary>
    internal static Eac3Description? DescribeEac3(ReadOnlySpan<byte> unit)
    {
        if (Sync(unit) is not { } first || first.StreamType == 1)
        {
            // A unit that opens with a dependent substream has lost its independent one; there is nothing
            // to describe it against.
            return null;
        }

        // Count the dependent substreams that follow, by walking each frame's stated size.
        var dependents = 0;
        var at = first.FrameBytes;
        while (at + 6 <= unit.Length && Sync(unit[at..]) is { } next)
        {
            if (next.StreamType == 1)
            {
                dependents++;
            }
            else
            {
                break;                              // a second independent substream: not this frame's
            }

            at += next.FrameBytes;
        }

        var sampleRate = first.Fscod < 3
            ? Ac3SampleRates[first.Fscod]
            : Eac3HalfRates[Math.Min(first.Fscod2, 2)];
        var samplesPerFrame = Eac3Blocks[first.NumBlocksCode] * 256;
        var dataRate = (int)(first.FrameBytes * 8L * sampleRate / samplesPerFrame / 1000);

        // dec3: data_rate (13) num_ind_sub (3), then per independent substream fscod (2) bsid (5)
        // reserved (1) asvc (1) bsmod (3) acmod (3) lfeon (1) reserved (3) num_dep_sub (4), and a
        // reserved bit when there are no dependents.
        var writer = new BitWriter();
        writer.Write(Math.Min(dataRate, 0x1FFF), 13);
        writer.Write(0, 3);                         // one independent substream, stored as N-1
        writer.Write(first.Fscod, 2);
        writer.Write(first.Bsid, 5);
        writer.Write(0, 1);                         // reserved
        writer.Write(0, 1);                         // asvc
        writer.Write(0, 3);                         // bsmod: informational, and buried past the mixing data
        writer.Write(first.Acmod, 3);
        writer.Write(first.Lfeon, 1);
        writer.Write(0, 3);                         // reserved
        writer.Write(dependents, 4);
        if (dependents == 0)
        {
            writer.Write(0, 1);                     // reserved
        }
        else
        {
            writer.Write(0, 9);                     // chan_loc: the dependent channels are not described
        }

        return new Eac3Description(
            writer.ToArray(),
            sampleRate,
            Ac3Channels[first.Acmod] + first.Lfeon,
            samplesPerFrame);
    }

    private readonly record struct Eac3Sync(
        int StreamType, int FrameBytes, int Fscod, int Fscod2, int NumBlocksCode, int Acmod, int Lfeon, int Bsid);

    private static Eac3Sync? Sync(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 6 || frame[0] != 0x0B || frame[1] != 0x77)
        {
            return null;
        }

        var bits = new BitReader(frame);
        bits.Read(16);                              // syncword
        var streamType = bits.Read(2);
        bits.Read(3);                               // substreamid
        var frameSize = ((bits.Read(11) + 1) * 2);
        var fscod = bits.Read(2);
        var fscod2 = 0;
        var numBlocksCode = 3;                      // six blocks, which is what a half-rate frame carries
        if (fscod == 3)
        {
            fscod2 = bits.Read(2);
        }
        else
        {
            numBlocksCode = bits.Read(2);
        }

        var acmod = bits.Read(3);
        var lfeon = bits.Read(1);
        var bsid = bits.Read(5);

        // E-AC-3 is bitstream id 16; anything else read this way is not one.
        return bsid is < 11 or > 16 || frameSize <= 0
            ? null
            : new Eac3Sync(streamType, frameSize, fscod, fscod2, numBlocksCode, acmod, lfeon, bsid);
    }

    /// <summary>Bit-packing for the one descriptor that is not byte-aligned.</summary>
    private ref struct BitWriter
    {
        private readonly List<byte> _bytes = [];
        private int _pending;
        private int _bits;

        public BitWriter()
        {
        }

        public void Write(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                _pending = (_pending << 1) | ((value >> i) & 1);
                if (++_bits == 8)
                {
                    _bytes.Add((byte)_pending);
                    _pending = 0;
                    _bits = 0;
                }
            }
        }

        public byte[] ToArray()
        {
            if (_bits > 0)
            {
                _bytes.Add((byte)(_pending << (8 - _bits)));
            }

            return [.. _bytes];
        }
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
