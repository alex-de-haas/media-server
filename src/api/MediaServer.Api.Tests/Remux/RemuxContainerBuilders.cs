using System.Buffers.Binary;
using MediaServer.Api.Tests.Probe;

namespace MediaServer.Api.Tests.Remux;

/// <summary>
/// Matroska pieces the header-probe builders do not need: track numbers, codec private data, the Dolby
/// Vision block-addition mapping, and clusters holding real blocks — including laced ones, which are the
/// reason this exists at all. A laced block cannot be produced by remuxing a test file through ffmpeg,
/// because ffmpeg never laces, so it has to be written by hand.
/// </summary>
internal static class RemuxContainerBuilders
{
    private static byte[] Ebml(uint id, params byte[][] payload) => ContainerBuilders.Ebml(id, payload);

    private static byte[] Uint(ulong value) => ContainerBuilders.Uint(value);

    public static byte[] TrackEntry(
        ulong number,
        ulong type,
        string codec,
        byte[]? codecPrivate = null,
        byte[]? dolbyVision = null,
        ulong width = 0,
        ulong height = 0,
        ulong defaultDuration = 0,
        ulong channels = 0,
        int primaries = 0,
        int transfer = 0,
        int matrix = 0,
        int range = 0)
    {
        var children = new List<byte[]>
        {
            Ebml(0xD7, Uint(number)),
            Ebml(0x83, Uint(type)),
            Ebml(0x86, ContainerBuilders.Str(codec)),
        };

        if (codecPrivate is not null)
        {
            children.Add(Ebml(0x63A2, codecPrivate));
        }

        if (defaultDuration > 0)
        {
            children.Add(Ebml(0x23E383, Uint(defaultDuration)));
        }

        if (type == 1)
        {
            var video = new List<byte[]> { Ebml(0xB0, Uint(width)), Ebml(0xBA, Uint(height)) };
            if (primaries > 0 || transfer > 0 || matrix > 0 || range > 0)
            {
                var colour = new List<byte[]>();
                if (matrix > 0) { colour.Add(Ebml(0x55B1, Uint((ulong)matrix))); }
                if (range > 0) { colour.Add(Ebml(0x55B9, Uint((ulong)range))); }
                if (transfer > 0) { colour.Add(Ebml(0x55BA, Uint((ulong)transfer))); }
                if (primaries > 0) { colour.Add(Ebml(0x55BB, Uint((ulong)primaries))); }
                video.Add(Ebml(0x55B0, [.. colour]));
            }

            children.Add(Ebml(0xE0, [.. video]));
        }

        if (type == 2 && channels > 0)
        {
            children.Add(Ebml(0xE1, Ebml(0x9F, Uint(channels))));
        }

        if (dolbyVision is not null)
        {
            children.Add(Ebml(0x41E4,
                Ebml(0x41A4, ContainerBuilders.Str("Dolby Vision configuration")),
                Ebml(0x41E7, Uint(0x64766343)),     // 'dvcC'
                Ebml(0x41ED, dolbyVision)));
        }

        return Ebml(0xAE, [.. children]);
    }

    public static byte[] Cluster(ulong timestamp, params byte[][] blocks) =>
        Ebml(0x1F43B675, [Ebml(0xE7, Uint(timestamp)), .. blocks]);

    public static byte[] SimpleBlock(ulong track, short relative, bool keyframe, params byte[][] frames) =>
        Ebml(0xA3, BlockBody(track, relative, (byte)(keyframe ? 0x80 : 0x00), Lacing.None, frames));

    public static byte[] LacedSimpleBlock(ulong track, short relative, Lacing lacing, params byte[][] frames) =>
        Ebml(0xA3, BlockBody(track, relative, 0x80, lacing, frames));

    /// <summary>
    /// A block inside a group. Keyframe-ness here is the absence of a <c>ReferenceBlock</c>, not a flag,
    /// which is the distinction the indexer has to get right for the sync table to mean anything.
    /// </summary>
    public static byte[] BlockGroup(ulong track, short relative, bool references, params byte[][] frames)
    {
        var children = new List<byte[]> { Ebml(0xA1, BlockBody(track, relative, 0x00, Lacing.None, frames)) };
        if (references)
        {
            children.Add(Ebml(0xFB, [0x01]));
        }

        return Ebml(0xA0, [.. children]);
    }

    public enum Lacing
    {
        None = 0,
        Xiph = 1,
        Fixed = 2,
        Ebml = 3,
    }

    private static byte[] BlockBody(ulong track, short relative, byte baseFlags, Lacing lacing, byte[][] frames)
    {
        var flags = (byte)(baseFlags | ((int)lacing << 1));
        var header = new List<byte> { (byte)(0x80 | track) };   // one-byte VINT: track numbers here are small
        Span<byte> time = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(time, relative);
        header.AddRange(time.ToArray());
        header.Add(flags);

        if (lacing == Lacing.None)
        {
            return [.. header, .. frames.SelectMany(frame => frame)];
        }

        header.Add((byte)(frames.Length - 1));                  // stored as N-1
        header.AddRange(LacingSizes(lacing, frames));
        return [.. header, .. frames.SelectMany(frame => frame)];
    }

    private static IEnumerable<byte> LacingSizes(Lacing lacing, byte[][] frames)
    {
        switch (lacing)
        {
            case Lacing.Fixed:
                // No size table: every frame is the same length, and the reader divides.
                return [];

            case Lacing.Xiph:
                var xiph = new List<byte>();
                foreach (var frame in frames[..^1])
                {
                    var remaining = frame.Length;
                    while (remaining >= 255)
                    {
                        xiph.Add(255);
                        remaining -= 255;
                    }

                    xiph.Add((byte)remaining);
                }

                return xiph;

            default:
                var ebml = new List<byte>(TwoByteVint((ulong)frames[0].Length));
                for (var i = 1; i < frames.Length - 1; i++)
                {
                    // Deltas are signed and biased by half the range the width can hold.
                    var delta = frames[i].Length - frames[i - 1].Length;
                    ebml.AddRange(TwoByteVint((ulong)(delta + ((1 << 13) - 1))));
                }

                return ebml;
        }
    }

    private static byte[] TwoByteVint(ulong value) =>
        [(byte)(0x40 | (value >> 8)), (byte)(value & 0xFF)];

    public static byte[] Frame(int size, byte fill) => Enumerable.Repeat(fill, size).ToArray();
}
