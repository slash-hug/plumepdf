namespace PlumePdf.Tests.Compression;

/// <summary>
/// A non-seekable view of another stream, standing in for an HTTP response body or an upload
/// stream: it reads or writes forward only, never reports a length or position, and records
/// whether it was flushed or disposed. Disposing it leaves <c>inner</c> open.
/// </summary>
internal sealed class ForwardOnlyStream(Stream inner, bool readable = true, bool writable = true) : Stream
{
    /// <summary>Called before every read; a test cancels or throws from it.</summary>
    public Action? OnRead { get; init; }

    /// <summary>Whether <see cref="Stream.Dispose()"/> was called.</summary>
    public bool Disposed { get; private set; }

    /// <summary>How many times <see cref="Flush"/> was called.</summary>
    public int Flushes { get; private set; }

    public override bool CanRead => readable && !Disposed;

    public override bool CanSeek => false;

    public override bool CanWrite => writable && !Disposed;

    public override long Length => throw new NotSupportedException("Forward-only.");

    public override long Position
    {
        get => throw new NotSupportedException("Forward-only.");
        set => throw new NotSupportedException("Forward-only.");
    }

    public override void Flush()
    {
        Flushes++;
        inner.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!readable)
        {
            throw new NotSupportedException("Write-only.");
        }

        OnRead?.Invoke();
        return inner.Read(buffer, offset, count);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("Forward-only.");

    public override void SetLength(long value) => throw new NotSupportedException("Forward-only.");

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (!writable)
        {
            throw new NotSupportedException("Read-only.");
        }

        inner.Write(buffer, offset, count);
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
