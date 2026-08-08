using MediaServer.Api.Remux;

namespace MediaServer.Api.Tests.Remux;

public sealed class SynthesizedMp4StreamTests
{
    private static byte[] Fill(int size, byte value) => Enumerable.Repeat(value, size).ToArray();

    private static SynthesizedMp4Stream Stream(byte[] header, params byte[][] parts) =>
        new(header, [.. parts.Select(part => (Stream)new MemoryStream(part))]);

    /// <summary>Reads the whole stream the way a caller must: repeatedly, until it stops giving.</summary>
    private static byte[] Drain(Stream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[7];                 // deliberately awkward, to cross boundaries mid-read
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    [Fact]
    public void The_length_is_the_header_plus_every_part()
    {
        using var stream = Stream(Fill(10, 0x01), Fill(20, 0x02), Fill(5, 0x03));

        Assert.Equal(35, stream.Length);
    }

    [Fact]
    public void Reading_from_the_top_yields_the_header_then_each_part_in_turn()
    {
        var header = Fill(10, 0x01);
        var first = Fill(20, 0x02);
        var second = Fill(5, 0x03);
        using var stream = Stream(header, first, second);

        Assert.Equal([.. header, .. first, .. second], Drain(stream));
    }

    [Fact]
    public void A_read_stops_at_a_boundary_rather_than_spanning_it()
    {
        using var stream = Stream(Fill(4, 0x01), Fill(4, 0x02));
        var buffer = new byte[100];

        // Four bytes of header are all that is on offer, however much was asked for.
        Assert.Equal(4, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(4, stream.Read(buffer, 0, buffer.Length));
        Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
    }

    [Fact]
    public void Seeking_lands_where_it_says_and_reads_from_there()
    {
        using var stream = Stream(Fill(10, 0x01), Fill(20, 0x02), Fill(5, 0x03));
        var buffer = new byte[3];

        stream.Position = 15;                     // inside the first part
        stream.ReadExactly(buffer);
        Assert.Equal([0x02, 0x02, 0x02], buffer);

        stream.Position = 31;                     // inside the second
        stream.ReadExactly(buffer);
        Assert.Equal([0x03, 0x03, 0x03], buffer);

        Assert.Equal(34, stream.Seek(-1, SeekOrigin.End));
    }

    [Fact]
    public void Reading_past_the_end_gives_nothing_rather_than_failing()
    {
        using var stream = Stream(Fill(4, 0x01), Fill(4, 0x02));
        stream.Position = 99;

        Assert.Equal(0, stream.Read(new byte[10], 0, 10));
    }

    [Fact]
    public async Task The_asynchronous_path_reads_the_same_bytes()
    {
        var header = Fill(6, 0x01);
        var part = Fill(9, 0x02);
        await using var stream = Stream(header, part);

        var buffer = new byte[15];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(total), CancellationToken.None)) > 0)
        {
            total += read;
        }

        Assert.Equal(15, total);
        Assert.Equal([.. header, .. part], buffer);
    }

    [Fact]
    public void Disposing_closes_every_part()
    {
        var first = new MemoryStream(Fill(4, 0x01));
        var second = new MemoryStream(Fill(4, 0x02));
        var stream = new SynthesizedMp4Stream(Fill(2, 0x00), [first, second]);

        stream.Dispose();

        Assert.Throws<ObjectDisposedException>(() => first.ReadByte());
        Assert.Throws<ObjectDisposedException>(() => second.ReadByte());
    }
}
