namespace PlumePdf.IO;

/// <summary>
/// A write-only wrapper over a file PlumePDF itself opened, converting an
/// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> raised by the file —
/// on a write, a flush, a seek, or the final flush on dispose (a full disk) — into the coded
/// <see cref="PlumePdfException"/> <c>wrap</c> builds. Only failures of the wrapped file are
/// converted: an exception thrown by the code writing into this stream (a source read, a
/// cancellation, an already-coded failure) passes through untouched, because it never comes
/// from a call on the inner stream.
/// </summary>
internal sealed class CodedWriteStream : Stream
{
    private readonly Stream _inner;
    private readonly Func<Exception, PlumePdfException> _wrap;

    /// <summary>Wraps <paramref name="inner"/>, which this stream owns and disposes.</summary>
    public CodedWriteStream(Stream inner, Func<Exception, PlumePdfException> wrap)
    {
        _inner = inner;
        _wrap = wrap;
    }

    /// <summary>Whether <paramref name="ex"/> is a file-system failure this layer codes.</summary>
    public static bool IsFileSystemFailure(Exception ex) => ex is IOException or UnauthorizedAccessException;

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc/>
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc/>
    public override long Length
    {
        get
        {
            try
            {
                return _inner.Length;
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                throw _wrap(ex);
            }
        }
    }

    /// <inheritdoc/>
    public override long Position
    {
        get
        {
            try
            {
                return _inner.Position;
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                throw _wrap(ex);
            }
        }

        set
        {
            try
            {
                _inner.Position = value;
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                throw _wrap(ex);
            }
        }
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Write-only.");

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin)
    {
        try
        {
            return _inner.Seek(offset, origin);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw _wrap(ex);
        }
    }

    /// <inheritdoc/>
    public override void SetLength(long value)
    {
        try
        {
            _inner.SetLength(value);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw _wrap(ex);
        }
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        try
        {
            _inner.Write(buffer, offset, count);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw _wrap(ex);
        }
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            _inner.Write(buffer);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw _wrap(ex);
        }
    }

    /// <inheritdoc/>
    public override void WriteByte(byte value)
    {
        try
        {
            _inner.WriteByte(value);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw _wrap(ex);
        }
    }

    /// <inheritdoc/>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc/>
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw _wrap(ex);
        }
    }

    /// <inheritdoc/>
    public override void Flush()
    {
        try
        {
            _inner.Flush();
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw _wrap(ex);
        }
    }

    /// <inheritdoc/>
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw _wrap(ex);
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _inner.Dispose();
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                throw _wrap(ex);
            }
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw _wrap(ex);
        }

        GC.SuppressFinalize(this);
    }
}
