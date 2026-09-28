namespace PlumePdf.Objects;

/// <summary>
/// A write-only pass-through <see cref="Stream"/> decorator whose <see cref="Position"/>
/// reports a fixed base offset plus the wrapped stream's own position — so code that
/// derives byte offsets purely from <see cref="Position"/> (cross-reference table entries,
/// <see cref="SigningWriteSession"/>'s placeholder-offset callback) produces values already
/// correct for where this content will finally sit in the completed file, even while the
/// actual bytes are still being written into a private buffer that itself starts at position 0.
/// </summary>
/// <remarks>
/// Fixes a bug in <see cref="SigningWriteSession.Create"/> found during integration:
/// <see cref="IncrementalUpdateWriter.WriteAppendix"/> writes every
/// dirty object's cross-reference offset from the destination stream's own <c>Position</c>.
/// <see cref="SigningWriteSession"/> must build its appendix into an in-memory buffer first
/// (the total file length — needed for <c>/ByteRange</c> — isn't known until the appendix is
/// fully written), so without this wrapper every non-placeholder dirty object's xref entry
/// came out <em>buffer</em>-relative (0-based) instead of <em>file</em>-relative, silently
/// producing a signed file whose cross-reference table pointed at the wrong bytes for every
/// object except the ones this session tracked by hand — masked in a single-signature pass by
/// <c>CrossReferenceReader</c>'s brute-force recovery fallback, but fatal to any operation that
/// needs the recovered document's own <c>startxref</c> afterward (a second signature, DSS, or
/// document timestamp — <c>PLUME5005</c>).
/// </remarks>
internal sealed class OffsetPositionStream : Stream
{
    private readonly Stream _inner;
    private readonly long _baseOffset;

    /// <summary>Wraps <paramref name="inner"/>, reporting <see cref="Position"/> as <paramref name="baseOffset"/> plus <paramref name="inner"/>'s own position.</summary>
    /// <param name="inner">The stream every write is forwarded to, unmodified.</param>
    /// <param name="baseOffset">The absolute file offset <paramref name="inner"/>'s own position 0 corresponds to.</param>
    public OffsetPositionStream(Stream inner, long baseOffset)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfNegative(baseOffset);
        _inner = inner;
        _baseOffset = baseOffset;
    }

    /// <inheritdoc/>
    public override bool CanRead => false;

    /// <inheritdoc/>
    public override bool CanSeek => false;

    /// <inheritdoc/>
    public override bool CanWrite => true;

    /// <inheritdoc/>
    public override long Length => throw new NotSupportedException("OffsetPositionStream tracks position only, not a settled length.");

    /// <inheritdoc/>
    public override long Position
    {
        get => _baseOffset + _inner.Position;
        set => throw new NotSupportedException("OffsetPositionStream is forward-only; it does not support seeking.");
    }

    /// <inheritdoc/>
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    /// <inheritdoc/>
    public override void Flush() => _inner.Flush();

    /// <inheritdoc/>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("OffsetPositionStream is write-only.");

    /// <inheritdoc/>
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("OffsetPositionStream is forward-only; it does not support seeking.");

    /// <inheritdoc/>
    public override void SetLength(long value) => throw new NotSupportedException("OffsetPositionStream does not support resizing its inner stream.");
}
