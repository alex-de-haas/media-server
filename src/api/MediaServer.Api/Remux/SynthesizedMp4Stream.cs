namespace MediaServer.Api.Remux;

/// <summary>
/// Presents a computed header followed by one or more untouched files as a single seekable stream.
///
/// More than one file, because a sidecar dub is a second file whose samples have to appear in the same
/// output as the video's. An <c>mdat</c> is an opaque blob and a sample offset may point into any of them,
/// so the output is simply the header and then each wrapped file in turn.
///
/// Being seekable and knowing its length is what lets byte ranges be handled by the framework's own file
/// result rather than by hand — which matters, because AVFoundation refuses a server that will not declare
/// a total length, and reads a truncated answer to an explicit range as a failed request.
///
/// Nothing is buffered and nothing is produced ahead: a read below the header's length comes from memory,
/// and everything above it is a read of whichever file that offset falls in.
/// </summary>
internal sealed class SynthesizedMp4Stream : Stream
{
    private readonly byte[] _header;
    private readonly IReadOnlyList<Stream> _parts;
    private readonly long[] _starts;                // where each part begins in the output
    private long _position;

    public SynthesizedMp4Stream(byte[] header, IReadOnlyList<Stream> parts)
    {
        _header = header;
        _parts = parts;
        _starts = new long[parts.Count + 1];
        _starts[0] = header.Length;
        for (var i = 0; i < parts.Count; i++)
        {
            _starts[i + 1] = _starts[i] + parts[i].Length;
        }

        Length = _starts[^1];
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    /// <summary>
    /// Moves a part to where the next read starts, and **only when it is not already there**.
    ///
    /// Assigning <see cref="Stream.Position"/> discards a <see cref="FileStream"/>'s read buffer, so
    /// doing it unconditionally meant every read went to the kernel with nothing carried over — during
    /// playback, which is the one access pattern that is purely sequential and where the position has
    /// not moved at all.
    /// </summary>
    private static void Seek(Stream part, long to)
    {
        if (part.Position != to)
        {
            part.Position = to;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (!Locate(buffer.Length, out var part, out var within, out var take))
        {
            return 0;
        }

        int read;
        if (part < 0)
        {
            _header.AsSpan((int)_position, take).CopyTo(buffer);
            read = take;
        }
        else
        {
            Seek(_parts[part], within);
            read = _parts[part].Read(buffer[..take]);
        }

        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!Locate(buffer.Length, out var part, out var within, out var take))
        {
            return 0;
        }

        if (part < 0)
        {
            return Read(buffer.Span);
        }

        Seek(_parts[part], within);
        var read = await _parts[part].ReadAsync(buffer[..take], cancellationToken);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>
    /// Which part the current position falls in, where it sits inside it, and how much may be taken
    /// before the next part begins. A read never spans a boundary; the caller comes back for the rest,
    /// which every stream is entitled to make it do.
    /// </summary>
    private bool Locate(int wanted, out int part, out long within, out int take)
    {
        part = -1;
        within = 0;
        take = 0;

        if (_position >= Length || wanted == 0)
        {
            return false;
        }

        if (_position < _header.Length)
        {
            take = (int)Math.Min(wanted, _header.Length - _position);
            return true;
        }

        for (var i = 0; i < _parts.Count; i++)
        {
            if (_position < _starts[i + 1])
            {
                part = i;
                within = _position - _starts[i];
                take = (int)Math.Min(wanted, _starts[i + 1] - _position);
                return true;
            }
        }

        return false;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var wanted = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        // The Position setter refuses a negative; seeking must not be a way around it, or a read would
        // index the header from before its start.
        if (wanted < 0)
        {
            throw new IOException("Cannot seek before the beginning of the stream.");
        }

        _position = wanted;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var part in _parts)
            {
                part.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
