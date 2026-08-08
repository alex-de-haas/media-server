namespace MediaServer.Api.Remux;

/// <summary>
/// Presents a computed header followed by an untouched source file as one seekable stream.
///
/// This is what makes serving the whole thing ordinary: because it seeks and states a length, byte ranges
/// are handled by the framework's own file result rather than by hand — which matters, since AVFoundation
/// refuses a server that will not declare a total length, and reads a truncated answer to an explicit
/// range as a failed request rather than as a smaller one.
///
/// Nothing is buffered and nothing is produced ahead: a read below the header's length is served from
/// memory, and everything above it is a read of the source at the same offset less the header.
/// </summary>
internal sealed class SynthesizedMp4Stream(byte[] header, Stream source) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => header.Length + source.Length;

    public override long Position
    {
        get => _position;
        set => _position = value < 0 ? throw new ArgumentOutOfRangeException(nameof(value)) : value;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        int read;
        if (_position < header.Length)
        {
            // A read that spans the boundary stops at it; the caller comes back for the rest, which is
            // what every stream is entitled to do anyway.
            read = (int)Math.Min(buffer.Length, header.Length - _position);
            header.AsSpan((int)_position, read).CopyTo(buffer);
        }
        else
        {
            source.Position = _position - header.Length;
            read = source.Read(buffer[..(int)Math.Min(buffer.Length, Length - _position)]);
        }

        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= Length || buffer.IsEmpty)
        {
            return 0;
        }

        if (_position < header.Length)
        {
            return Read(buffer.Span);
        }

        source.Position = _position - header.Length;
        var read = await source.ReadAsync(
            buffer[..(int)Math.Min(buffer.Length, Length - _position)], cancellationToken);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

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
            source.Dispose();
        }

        base.Dispose(disposing);
    }
}
