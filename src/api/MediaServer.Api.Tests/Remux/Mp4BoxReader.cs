using System.Buffers.Binary;
using System.Text;

namespace MediaServer.Api.Tests.Remux;

/// <summary>
/// Walks a synthesised header so tests can assert on what was written rather than on its length. Only
/// enough of ISO base media to find a box by path and read the fields these tests care about.
/// </summary>
internal sealed class Mp4BoxReader(byte[] data)
{
    internal readonly record struct Box(string Type, int Start, int End)
    {
        public int Length => End - Start;
    }

    /// <summary>Direct children of the given range.</summary>
    internal IEnumerable<Box> Children(int start, int end)
    {
        var position = start;
        while (position + 8 <= end)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4));
            var type = Encoding.ASCII.GetString(data, position + 4, 4);
            var header = 8;
            if (size == 1)
            {
                size = (int)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position + 8, 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - position;
            }

            yield return new Box(type, position + header, position + size);
            position += size;
        }
    }

    internal IEnumerable<Box> Top => Children(0, data.Length);

    /// <summary>
    /// Every box at the end of a slash-separated path — "moov/trak/mdia/minf/stbl/stsd" yields one per
    /// track. Sample entries are not addressable this way because <c>stsd</c> has a header before them.
    /// </summary>
    internal IEnumerable<Box> Find(string path)
    {
        IEnumerable<Box> level = Top;
        foreach (var step in path.Split('/'))
        {
            level = level.Where(box => box.Type == step).ToList();
            var next = new List<Box>();
            foreach (var box in level)
            {
                next.AddRange(Children(box.Start, box.End));
            }

            if (step == path.Split('/')[^1])
            {
                return level;
            }

            level = next;
        }

        return level;
    }

    internal Box? First(string path) => Find(path).Cast<Box?>().FirstOrDefault();

    /// <summary>The sample entry inside an <c>stsd</c>: past its version, flags and entry count.</summary>
    internal Box SampleEntry(Box stsd) => Children(stsd.Start + 8, stsd.End).First();

    internal uint U32At(int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));

    internal ulong U64At(int offset) => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));

    /// <summary>The (count, value) runs of an stts or ctts table.</summary>
    internal IReadOnlyList<(uint Count, uint Value)> Runs(Box table)
    {
        var count = U32At(table.Start + 4);
        var runs = new List<(uint, uint)>();
        for (var i = 0; i < count; i++)
        {
            var at = table.Start + 8 + (i * 8);
            runs.Add((U32At(at), U32At(at + 4)));
        }

        return runs;
    }

    internal IReadOnlyList<ulong> ChunkOffsets(Box co64)
    {
        var count = U32At(co64.Start + 4);
        var offsets = new List<ulong>();
        for (var i = 0; i < count; i++)
        {
            offsets.Add(U64At(co64.Start + 8 + (i * 8)));
        }

        return offsets;
    }
}
