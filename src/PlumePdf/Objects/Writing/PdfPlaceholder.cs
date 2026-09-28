namespace PlumePdf.Objects;

/// <summary>
/// Stands in for a signature dictionary's <c>/Contents</c> entry (ISO 32000-1 §12.8.1) while
/// a <see cref="SigningWriteSession"/> pass is under way: the real CMS/PKCS#7
/// signature cannot be computed until the exact bytes surrounding it — including the
/// appendix's own cross-reference section and trailer — are already final, so this reserves
/// <see cref="ReservationBytes"/> raw bytes' worth of hex digit space up front.
/// <see cref="ObjectSerializer.WriteValue"/> recognizes this type and writes it as a hex
/// string of that many NUL bytes (<c>&lt;0000...0000&gt;</c>); <see cref="SigningWriteSession.PatchContents"/>
/// later overwrites the digit span in place with the real signature, without changing the
/// appendix's length or any other byte's offset.
/// </summary>
/// <remarks>
/// Deliberately not one of the eight ISO 32000-1 §7.3 object kinds <see cref="PdfObject"/>'s
/// own remarks describe — this is writer-internal plumbing that only ever exists inside a
/// <see cref="SigningWriteSession"/>'s dirty-object set and is never resolvable through
/// <c>doc.Objects</c> or seen by a reader.
/// </remarks>
internal sealed class PdfContentsPlaceholder : PdfObject
{
    /// <summary>Reserves space for a signature exactly <paramref name="reservationBytes"/> raw bytes long.</summary>
    /// <param name="reservationBytes">The number of raw signature bytes to reserve room for. Must be positive.</param>
    public PdfContentsPlaceholder(int reservationBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reservationBytes);
        ReservationBytes = reservationBytes;
    }

    /// <summary>The number of raw signature bytes this placeholder reserves room for (the hex string itself is twice this many characters).</summary>
    public int ReservationBytes { get; }
}

/// <summary>
/// Stands in for a signature dictionary's <c>/ByteRange</c> entry (ISO 32000-1 §12.8.1) while
/// a <see cref="SigningWriteSession"/> pass is under way: the two byte ranges the
/// signature covers aren't known until the whole appendix — including this array's own final
/// text — has already been written, so this reserves a fixed-width numeric field per entry
/// up front. <see cref="ObjectSerializer.WriteValue"/> writes it as
/// <c>[0 &lt;padWidth zeros&gt; &lt;padWidth zeros&gt; &lt;padWidth zeros&gt;]</c>;
/// <see cref="SigningWriteSession"/> overwrites each zero-padded field in place with the real
/// value once the appendix's total length is known, never changing the array's byte length.
/// </summary>
internal sealed class PdfByteRangePlaceholder : PdfObject
{
    /// <summary>Reserves each of the three variable entries exactly <paramref name="padWidth"/> decimal digits wide.</summary>
    /// <param name="padWidth">The fixed digit width for each of the three variable /ByteRange entries. Must be positive.</param>
    public PdfByteRangePlaceholder(int padWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(padWidth);
        PadWidth = padWidth;
    }

    /// <summary>The fixed decimal digit width reserved for each of the three variable /ByteRange entries.</summary>
    public int PadWidth { get; }
}
