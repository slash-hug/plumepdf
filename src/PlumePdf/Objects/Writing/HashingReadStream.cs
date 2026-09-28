using System.Security.Cryptography;

namespace PlumePdf.Objects;

/// <summary>
/// A pass-through <see cref="Stream"/> decorator that feeds every byte moving through it —
/// in either direction — into an <see cref="IncrementalHash"/> as a side effect, so a
/// document's original bytes can be hashed for a signature digest without a
/// second, dedicated read pass over the source. Two call shapes cover both consumers:
/// <c>ByteSource.CopyTo(destination)</c> only ever calls <see cref="Write"/> on its
/// destination, so wrapping the create-branch's output stream (or, for hashing alone,
/// <see cref="Stream.Null"/>) hashes the source's bytes exactly as they are copied; a caller
/// that instead wraps a readable inner stream and calls <see cref="Read"/> gets the same
/// hashing on the way out. Never seekable — hashing is meaningless out of byte order, so
/// this type deliberately refuses to support it rather than silently producing a wrong
/// digest for a caller who seeks.
/// </summary>
internal sealed class HashingReadStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _hash;
    private readonly bool _leaveOpen;

    /// <summary>Wraps <paramref name="inner"/>, appending every byte read from or written through this stream to <paramref name="hash"/>.</summary>
    /// <param name="inner">The stream to forward reads/writes to.</param>
    /// <param name="hash">The hash accumulator to feed. Not owned by this instance — the caller disposes it.</param>
    /// <param name="leaveOpen">When <see langword="false"/>, disposing this instance also disposes <paramref name="inner"/>.</param>
    public HashingReadStream(Stream inner, IncrementalHash hash, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(hash);
        _inner = inner;
        _hash = hash;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc/>
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => _inner.CanWrite;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException("HashingReadStream is forward-only; its length is whatever the caller streams through it, not knowable up front.");

    /// <inheritdoc/>
    public override long Position
    {
        get => throw new NotSupportedException("HashingReadStream is forward-only and tracks no position of its own.");
        set => throw new NotSupportedException("HashingReadStream is forward-only; seeking would hash bytes out of order.");
    }

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _hash.AppendData(buffer, offset, read);
        }

        return read;
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count > 0)
        {
            _hash.AppendData(buffer, offset, count);
        }

        _inner.Write(buffer, offset, count);
    }

    /// <inheritdoc/>
    public override void Flush() => _inner.Flush();

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) =>
        throw new NotSupportedException("HashingReadStream is forward-only; seeking would hash bytes out of order.");

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException("HashingReadStream does not support resizing its inner stream.");

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
