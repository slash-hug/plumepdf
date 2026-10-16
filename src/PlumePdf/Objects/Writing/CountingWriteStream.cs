namespace PlumePdf.Objects;

/// <summary>
/// A write-only, forward-only stream that counts the bytes written through it, passing them on
/// to an inner stream or, with none, discarding them. The writers record object offsets from
/// <see cref="Position"/>, so wrapping any writable stream in one gives offsets relative to where
/// writing began: the inner stream may be non-seekable, or already positioned past 0. Neither
/// flushing nor disposing this stream closes the inner one.
/// </summary>
internal sealed class CountingWriteStream : Stream
{
    private readonly Stream? _inner;
    private long _count;

    /// <summary>A counter that discards what it is given: the linearizer's measurement sink.</summary>
    public CountingWriteStream()
    {
    }

    /// <summary>A counter that writes through to <paramref name="inner"/>.</summary>
    /// <param name="inner">The stream the bytes go to. It is never disposed by this one.</param>
    public CountingWriteStream(Stream inner)
    {
        _inner = inner;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => _count;

    /// <summary>The number of bytes written so far.</summary>
    public override long Position
    {
        get => _count;
        set => throw new NotSupportedException("CountingWriteStream is forward-only.");
    }

    public override void Flush() => _inner?.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("CountingWriteStream is write-only.");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("CountingWriteStream is forward-only.");

    public override void SetLength(long value) => throw new NotSupportedException("CountingWriteStream is forward-only.");

    public override void Write(byte[] buffer, int offset, int count)
    {
        _inner?.Write(buffer, offset, count);
        _count += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        _inner?.Write(buffer);
        _count += buffer.Length;
    }

    public override void WriteByte(byte value)
    {
        _inner?.WriteByte(value);
        _count++;
    }
}
