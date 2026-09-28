namespace PlumePdf.IO;

/// <summary>
/// Internal abstraction over a document's underlying bytes — a memory-mapped file
/// (<see cref="MemoryMappedByteSource"/>) or a buffered stream/in-memory buffer
/// (<see cref="StreamByteSource"/>). Every implementation upholds the
/// copy-on-materialize contract: <see cref="Read"/> always copies into a caller-owned
/// destination and never returns a view over source-owned memory, so a source's lifetime
/// can never be violated by a public-facing span outliving it.
/// </summary>
internal abstract class ByteSource : IDisposable
{
    /// <summary>The total number of bytes available in the source.</summary>
    public abstract long Length { get; }

    /// <summary>
    /// Copies up to <paramref name="destination"/>.Length bytes starting at
    /// <paramref name="position"/> into <paramref name="destination"/>. Returns the number
    /// of bytes actually copied — fewer than requested only when <paramref name="position"/>
    /// plus the requested length runs past <see cref="Length"/>. Never returns a view over
    /// source-owned memory.
    /// </summary>
    /// <param name="position">The zero-based byte offset to start reading from.</param>
    /// <param name="destination">The buffer to copy into.</param>
    public abstract int Read(long position, Span<byte> destination);

    /// <summary>
    /// Reads every remaining byte from <paramref name="offset"/> to <see cref="Length"/>
    /// into a freshly allocated array. Used by the cross-reference and recovery readers,
    /// whose section sizes aren't known up front; a known Phase 1 simplification — see
    /// <c>CrossReferenceReader</c>'s remarks for the tradeoff this accepts.
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// More than <see cref="int.MaxValue"/> bytes remain from <paramref name="offset"/> to
    /// <see cref="Length"/> — a single managed array can't hold that many (<c>PLUME1005</c>),
    /// so a source region this large needs a bounded window (<see cref="Read"/> directly)
    /// rather than this method.
    /// </exception>
    public byte[] ReadToEnd(long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var remaining = Math.Max(0L, Length - offset);
        if (remaining > int.MaxValue)
        {
            throw new PlumePdfException("PLUME1005", $"Cannot read {remaining} bytes starting at offset {offset} into a single in-memory array (exceeds the 2 GiB single-array limit); this source region needs a bounded read window instead of ByteSource.ReadToEnd.");
        }

        var count = (int)remaining;
        if (count == 0)
        {
            return [];
        }

        var buffer = new byte[count];
        Read(offset, buffer);
        return buffer;
    }

    /// <summary>
    /// Streams every byte of this source to <paramref name="destination"/> in fixed-size
    /// chunks — unlike <see cref="ReadToEnd(long)"/>, never materializes the whole source (or
    /// even one chunk beyond <paramref name="bufferSize"/>) as a single in-memory array, so
    /// this works regardless of source size (docs/architecture.md: "no whole-document byte
    /// buffer, ever"). Used by <c>PdfDocument.SaveIncremental</c> to copy the original bytes
    /// ahead of the appended revision when writing to a different path than the source.
    /// </summary>
    public void CopyTo(Stream destination, int bufferSize = 81920)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bufferSize, 0);

        var length = Length;
        var buffer = new byte[(int)Math.Min(bufferSize, Math.Max(1, length))];
        var position = 0L;
        while (position < length)
        {
            var toRead = (int)Math.Min(buffer.Length, length - position);
            var read = Read(position, buffer.AsSpan(0, toRead));
            if (read <= 0)
            {
                break;
            }

            destination.Write(buffer, 0, read);
            position += read;
        }
    }

    /// <inheritdoc/>
    public abstract void Dispose();
}
