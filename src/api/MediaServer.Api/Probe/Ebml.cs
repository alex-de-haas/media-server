using System.Buffers.Binary;
using System.Text;

namespace MediaServer.Api.Probe;

/// <summary>
/// The element reader Matroska is built on, extracted from <see cref="ContainerHeader"/> so that a second
/// walker does not mean a second parser. <see cref="ContainerHeader"/> reads the head of a file to describe
/// it; the remux index walks every cluster in it. Both need exactly these primitives, and a divergence
/// between two copies of them would be the kind of bug that only shows up on one file in a hundred.
///
/// Everything here is positional and stateless: callers pass a stream and a byte range, and nothing is
/// buffered across calls. That keeps a walk over a 26 GB file to header reads and seeks.
/// </summary>
internal static class Ebml
{
    /// <summary>An element's id and the byte range of its body — the header is already consumed.</summary>
    internal readonly record struct Element(ulong Id, long Start, long End);

    /// <summary>
    /// Reads one element header at <paramref name="position"/>. Returns null past <paramref name="limit"/>
    /// or on a malformed header, which is how a walk over truncated or padded data stops rather than
    /// running away.
    /// </summary>
    internal static Element? Read(Stream stream, long position, long limit)
    {
        if (position >= limit)
        {
            return null;
        }

        stream.Position = position;
        var id = ReadVint(stream, keepMarker: true, out var idLength);
        if (idLength == 0)
        {
            return null;
        }

        var size = ReadVint(stream, keepMarker: false, out var sizeLength);
        if (sizeLength == 0)
        {
            return null;
        }

        var start = position + idLength + sizeLength;
        // An "unknown size" VINT (every data bit set) means the element runs to the end of its parent —
        // live-muxed Segments do this, and treating it as a huge size would run past the file.
        var unknown = size == (ulong.MaxValue >> (64 - (7 * sizeLength)));
        var end = unknown ? limit : Math.Min(limit, start + (long)size);
        return end < start ? null : new Element(id, start, end);
    }

    /// <summary>
    /// Scans forward for one element id. <paramref name="stopAt"/> ends the search early — passing the
    /// Cluster id is what keeps a header read from walking into the media.
    /// </summary>
    internal static Element? Find(Stream stream, long from, long to, ulong id, ulong? stopAt)
    {
        var position = from;
        while (position < to)
        {
            if (Read(stream, position, to) is not { } element || (stopAt is { } stop && element.Id == stop))
            {
                return null;
            }

            if (element.Id == id)
            {
                return element;
            }

            position = element.End;
        }

        return null;
    }

    /// <summary>
    /// A variable-length integer. Element ids keep their length marker, because that is what makes them
    /// unique; sizes and track numbers drop it.
    /// </summary>
    internal static ulong ReadVint(Stream stream, bool keepMarker, out int length)
    {
        var first = stream.ReadByte();
        if (first < 0)
        {
            length = 0;
            return 0;
        }

        length = 1;
        var mask = 0x80;
        while (length <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            length++;
        }

        if (length > 8)
        {
            length = 0;
            return 0;
        }

        var value = keepMarker ? (ulong)first : (ulong)(first & (mask - 1));
        for (var i = 1; i < length; i++)
        {
            var next = stream.ReadByte();
            if (next < 0)
            {
                length = 0;
                return 0;
            }

            value = (value << 8) | (byte)next;
        }

        return value;
    }

    internal static ulong ReadUInt(Stream stream, long start, long end)
    {
        stream.Position = start;
        ulong value = 0;
        for (var i = start; i < end && i < start + 8; i++)
        {
            var next = stream.ReadByte();
            if (next < 0)
            {
                break;
            }

            value = (value << 8) | (byte)next;
        }

        return value;
    }

    internal static double? ReadFloat(Stream stream, long start, long end)
    {
        var length = (int)(end - start);
        if (length is not (4 or 8))
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[8];
        stream.Position = start;
        stream.ReadExactly(buffer[..length]);
        return length == 4
            ? BinaryPrimitives.ReadSingleBigEndian(buffer[..4])
            : BinaryPrimitives.ReadDoubleBigEndian(buffer[..8]);
    }

    internal static string? ReadString(Stream stream, long start, long end)
    {
        var length = end - start;
        if (length <= 0 || length > 4096)
        {
            return null;
        }

        var buffer = new byte[length];
        stream.Position = start;
        stream.ReadExactly(buffer);
        var text = Encoding.UTF8.GetString(buffer).TrimEnd('\0');
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// A binary element, capped: <c>CodecPrivate</c> and the Dolby Vision configuration are the ones this
    /// exists for, and both are small. A cap keeps a corrupt size from asking for a gigabyte.
    /// </summary>
    internal static byte[]? ReadBytes(Stream stream, long start, long end, int max = 64 * 1024)
    {
        var length = end - start;
        if (length <= 0 || length > max)
        {
            return null;
        }

        var buffer = new byte[length];
        stream.Position = start;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
