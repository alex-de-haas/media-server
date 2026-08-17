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

    [Fact]
    public void Seeking_before_the_beginning_is_refused_rather_than_silently_allowed()
    {
        using var stream = Stream(Fill(4, 0x01), Fill(4, 0x02));

        // The Position setter refuses a negative; Seek must not be a way around it, or the next read
        // would index the header from before its start.
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(-stream.Length - 1, SeekOrigin.End));
        Assert.Equal(0, stream.Position);
    }
}

/// <summary>
/// That reading onwards does not keep re-seating the part underneath.
///
/// Assigning Position discards a FileStream's read buffer, so doing it on every read meant no
/// buffering and no read-ahead during playback — the one access pattern that is purely sequential.
/// Measured on production as roughly half the throughput a film needs.
/// </summary>
public sealed class SynthesizedMp4StreamSeekTests
{
    /// <summary>A part that counts how often it was moved rather than read onwards from.</summary>
    private sealed class CountingStream(byte[] content) : MemoryStream(content)
    {
        public int Seeks { get; private set; }

        public override long Position
        {
            get => base.Position;
            set
            {
                if (value != base.Position)
                {
                    Seeks++;
                }

                base.Position = value;
            }
        }
    }

    [Fact]
    public void Reading_onwards_does_not_move_the_part_at_all()
    {
        var part = new CountingStream([.. Enumerable.Range(0, 4096).Select(i => (byte)i)]);
        var stream = new SynthesizedMp4Stream(new byte[16], [part]);

        var buffer = new byte[512];
        for (var i = 0; i < 8; i++)
        {
            stream.Read(buffer);
        }

        // The header is consumed first, then the part is entered once and never re-seated.
        Assert.True(part.Seeks <= 1, $"the part was moved {part.Seeks} times reading straight through");
    }

    [Fact]
    public void Seeking_still_moves_it()
    {
        var part = new CountingStream([.. Enumerable.Range(0, 4096).Select(i => (byte)i)]);
        var stream = new SynthesizedMp4Stream(new byte[16], [part]);
        var buffer = new byte[64];

        stream.Position = 1000;
        stream.Read(buffer);
        stream.Position = 3000;
        stream.Read(buffer);

        Assert.Equal(2, part.Seeks);
    }

    [Fact]
    public void A_jump_backwards_reads_the_right_bytes()
    {
        // The guard compares positions, so it has to be the *part's* position and not the output's.
        var content = new byte[4096];
        for (var i = 0; i < content.Length; i++) { content[i] = (byte)(i % 251); }

        var stream = new SynthesizedMp4Stream(new byte[16], [new CountingStream(content)]);
        var buffer = new byte[32];

        stream.Position = 16 + 2000;
        stream.ReadExactly(buffer);
        Assert.Equal(content[2000..2032], buffer);

        stream.Position = 16 + 100;
        stream.ReadExactly(buffer);
        Assert.Equal(content[100..132], buffer);
    }
}
