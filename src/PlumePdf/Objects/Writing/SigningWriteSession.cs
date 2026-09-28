using System.Globalization;
using System.Security.Cryptography;
using PlumePdf.IO;

namespace PlumePdf.Objects;

/// <summary>
/// Wraps exactly one <c>SaveIncremental</c> pass that is signing the resulting revision
/// (ISO 32000-1 §12.8): the caller registers a signature dictionary — via
/// <see cref="ObjectRegistry.AllocateNumber"/>/<see cref="ObjectRegistry.RegisterNew"/> —
/// whose <c>/Contents</c> is a <see cref="PdfContentsPlaceholder"/> and whose
/// <c>/ByteRange</c> is a <see cref="PdfByteRangePlaceholder"/>, then calls
/// <see cref="Create"/>. From that point the appendix is already fully written (including
/// its own cross-reference section and <c>%%EOF</c>) into an internal in-memory buffer — the
/// <c>/ByteRange</c> reservation is patched with real values immediately, since computing it
/// needs nothing beyond lengths already known. The caller then calls
/// <see cref="HashDocument"/> to feed a digest algorithm, produces the actual CMS/PKCS#7
/// signature bytes out-of-band (not this type's concern — a clean-room CMS builder, or a
/// remote/HSM signer), passes them to <see cref="PatchContents"/>, and finally flushes
/// everything via <see cref="WriteTo"/>.
/// </summary>
/// <remarks>
/// The destination <see cref="Stream"/> passed to a real save is written to exactly once, at
/// the very end (<see cref="WriteTo"/>) — every step before that (<see cref="Create"/>,
/// <see cref="HashDocument"/>, <see cref="PatchContents"/>) reads only from the source
/// document's own bytes and this session's private appendix buffer, never from the output
/// handle. This matters for a network/pipe destination that cannot be read back, and it is
/// also what makes the two-pass "write placeholders, compute the real values, patch in
/// place" technique work at all: nothing downstream of the placeholders' fixed width may
/// shift once <see cref="Create"/> returns, or every offset this type computed would go
/// stale.
/// </remarks>
/// <example>
/// <code>
/// var session = SigningWriteSession.Create(source, objects, pagesTreeDirty: false, topPagesReference, pages, previousStartXrefOffset, options);
/// using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
/// session.HashDocument(hash);
/// byte[] cms = signer.Sign(hash.GetHashAndReset());
/// session.PatchContents(cms);
/// session.WriteTo(output);
/// </code>
/// </example>
internal sealed class SigningWriteSession
{
    private const int ByteRangePrefixLength = 3; // "[0" + one space, before the first patchable field.
    private static readonly char[] UpperHexDigits = "0123456789ABCDEF".ToCharArray();

    private readonly ByteSource _source;
    private readonly byte[] _appendix;
    private readonly int _reservationBytes;
    private bool _contentsPatched;

    private SigningWriteSession(ByteSource source, byte[] appendix, long contentsOffset, int contentsLength, int reservationBytes, long byteRangeOffset)
    {
        _source = source;
        _appendix = appendix;
        ContentsOffset = contentsOffset;
        ContentsLength = contentsLength;
        _reservationBytes = reservationBytes;
        ByteRangeOffset = byteRangeOffset;
    }

    /// <summary>
    /// The final file's byte offset of the signature's <c>/Contents</c> value's opening
    /// <c>&lt;</c> delimiter — the first byte range this signature covers is
    /// <c>[0, ContentsOffset)</c>.
    /// </summary>
    public long ContentsOffset { get; }

    /// <summary>
    /// The <c>/Contents</c> value's full lexical span in bytes, delimiters included
    /// (<c>2 + reservationBytes * 2</c>) — the second byte range this signature covers is
    /// <c>[ContentsOffset + ContentsLength, totalLength)</c>.
    /// </summary>
    public int ContentsLength { get; }

    /// <summary>The final file's byte offset of the signature's <c>/ByteRange</c> array's opening <c>[</c>.</summary>
    public long ByteRangeOffset { get; }

    /// <summary>
    /// Writes one complete incremental-update appendix — exactly like
    /// <see cref="IncrementalUpdateWriter.WriteAppendix"/> — into a private in-memory buffer,
    /// locates the signature dictionary's placeholder entries within it, and patches
    /// <c>/ByteRange</c> with its real values (the total file length, and therefore every
    /// <c>/ByteRange</c> entry, is already fully determined at this point — only the
    /// signature bytes themselves are still outstanding).
    /// </summary>
    /// <param name="source">The source document's original bytes — never copied to any output by this call.</param>
    /// <param name="objects">The source document's object graph, with the signature dictionary already registered.</param>
    /// <param name="pagesTreeDirty">Whether <c>Pages</c> has been reordered or had a page removed since the document was opened.</param>
    /// <param name="topPagesReference">The catalog's original <c>/Pages</c> target, or <see langword="null"/> when unresolvable.</param>
    /// <param name="pages">The current pages, in final order.</param>
    /// <param name="previousStartXrefOffset">The offset the new trailer's <c>/Prev</c> chains onto.</param>
    /// <param name="options">Options controlling the write, notably <see cref="PdfOptions.Deterministic"/>.</param>
    /// <param name="securityHandler">See <see cref="IncrementalUpdateWriter.WriteAppendix"/>'s parameter of the same name.</param>
    /// <exception cref="PlumePdfException">
    /// The dirty object set registered with <paramref name="objects"/> does not contain
    /// exactly one <see cref="PdfContentsPlaceholder"/> and exactly one
    /// <see cref="PdfByteRangePlaceholder"/> (<c>PLUME5013</c> — an internal invariant the
    /// caller composing the signature dictionary is responsible for upholding), or a computed
    /// <c>/ByteRange</c> entry needs more decimal digits than <see cref="PdfByteRangePlaceholder.PadWidth"/>
    /// reserved (<c>PLUME5012</c> — retry with a wider placeholder).
    /// </exception>
    public static SigningWriteSession Create(
        ByteSource source,
        ObjectRegistry objects,
        bool pagesTreeDirty,
        IndirectReference? topPagesReference,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> pages,
        long previousStartXrefOffset,
        PdfOptions options,
        StandardSecurityHandler? securityHandler = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);

        long? contentsOffsetInBuffer = null;
        int? reservationBytes = null;
        var contentsHits = 0;

        long? byteRangeOffsetInBuffer = null;
        int? byteRangePadWidth = null;
        var byteRangeHits = 0;

        void OnPlaceholder(PdfObject placeholder, long absoluteOffset)
        {
            // absoluteOffset is already file-relative (OffsetPositionStream below) — converted
            // back to buffer-relative here so every downstream use in this type (PatchByteRange
            // indexing into _appendix, etc.) keeps working against the same 0-based buffer
            // offsets it always has; only the cross-reference table this fixes needed the raw
            // absolute value, and it read Position directly rather than through this callback.
            var offset = absoluteOffset - source.Length;
            switch (placeholder)
            {
                case PdfContentsPlaceholder contents:
                    contentsHits++;
                    contentsOffsetInBuffer = offset;
                    reservationBytes = contents.ReservationBytes;
                    break;

                case PdfByteRangePlaceholder byteRange:
                    byteRangeHits++;
                    byteRangeOffsetInBuffer = offset;
                    byteRangePadWidth = byteRange.PadWidth;
                    break;
            }
        }

        using var buffer = new MemoryStream();
        // Bug fix (found during integration): IncrementalUpdateWriter.WriteAppendix derives every
        // dirty object's cross-reference offset from the destination stream's own Position.
        // Writing straight into `buffer` (which starts at 0) made every such offset
        // buffer-relative instead of file-relative for every object this session doesn't track
        // by hand — silently producing a signed file whose xref table pointed at the wrong
        // bytes, masked in a single-pass sign by CrossReferenceReader's brute-force recovery
        // fallback but fatal to chaining a further revision onto it (PLUME5005) — see
        // OffsetPositionStream's remarks.
        var offsetBuffer = new OffsetPositionStream(buffer, source.Length);
        IncrementalUpdateWriter.WriteAppendix(offsetBuffer, objects, pagesTreeDirty, topPagesReference, pages, previousStartXrefOffset, options, securityHandler, OnPlaceholder);

        if (contentsHits != 1)
        {
            throw new PlumePdfException("PLUME5013", $"A signing pass must register exactly one PdfContentsPlaceholder (found {contentsHits}) among its dirty objects before calling SigningWriteSession.Create — register the signature dictionary via ObjectRegistry.RegisterNew first.");
        }

        if (byteRangeHits != 1)
        {
            throw new PlumePdfException("PLUME5013", $"A signing pass must register exactly one PdfByteRangePlaceholder (found {byteRangeHits}) among its dirty objects before calling SigningWriteSession.Create — register the signature dictionary via ObjectRegistry.RegisterNew first.");
        }

        var appendix = buffer.ToArray();
        var contentsOffset = source.Length + contentsOffsetInBuffer!.Value;
        var contentsLength = 2 + (reservationBytes!.Value * 2); // '<' + hex digits + '>'

        var session = new SigningWriteSession(source, appendix, contentsOffset, contentsLength, reservationBytes.Value, source.Length + byteRangeOffsetInBuffer!.Value);
        session.PatchByteRange(byteRangeOffsetInBuffer.Value, byteRangePadWidth!.Value, source.Length + appendix.Length);
        return session;
    }

    /// <summary>
    /// Feeds <paramref name="hash"/> the exact bytes the signature's <c>/ByteRange</c> covers
    /// — everything in the final file except the <c>/Contents</c> hex string itself. Reads
    /// only from the source document (streamed, never buffered whole) and this session's
    /// private appendix buffer; the destination the caller will eventually write to is never
    /// touched.
    /// </summary>
    /// <param name="hash">The digest accumulator to feed. Not owned by this call — the caller finalizes and disposes it.</param>
    public void HashDocument(IncrementalHash hash)
    {
        ArgumentNullException.ThrowIfNull(hash);

        // Segment 1, [0, ContentsOffset): the whole original source — streamed through
        // HashingReadStream wrapping a discard sink, so nothing is buffered — followed by
        // this appendix buffer's own prefix up to (not including) the placeholder's '<'.
        _source.CopyTo(new HashingReadStream(Stream.Null, hash));
        var prefixLength = checked((int)(ContentsOffset - _source.Length));
        hash.AppendData(_appendix, 0, prefixLength);

        // Segment 2, [ContentsOffset + ContentsLength, totalLength): always entirely inside
        // the appendix buffer — the new cross-reference section, trailer, and %%EOF this
        // revision adds never touch the original source's own bytes.
        var suffixStart = checked((int)(ContentsOffset + ContentsLength - _source.Length));
        hash.AppendData(_appendix, suffixStart, _appendix.Length - suffixStart);
    }

    /// <summary>
    /// Writes <paramref name="derCms"/> — the completed CMS/PKCS#7 signature — into the
    /// <c>/Contents</c> reservation as upper-hex, zero-padding whatever capacity the real
    /// signature doesn't use. Does not touch any output stream; call <see cref="WriteTo"/>
    /// afterward to actually flush the finished document.
    /// </summary>
    /// <param name="derCms">The signature's raw (non-hex-encoded) bytes.</param>
    /// <exception cref="PlumePdfException">
    /// <paramref name="derCms"/> is longer than the reservation <see cref="PdfContentsPlaceholder.ReservationBytes"/>
    /// requested when the signature dictionary was built (<c>PLUME5011</c>) —
    /// build a new appendix with a larger reservation and sign again; a signature can never
    /// be widened in place after the fact without moving every byte after it, which would
    /// invalidate every offset this session already computed.
    /// </exception>
    public void PatchContents(byte[] derCms)
    {
        ArgumentNullException.ThrowIfNull(derCms);

        if (derCms.Length > _reservationBytes)
        {
            throw new PlumePdfException("PLUME5011", $"The signature is {derCms.Length} bytes, but this session only reserved room for {_reservationBytes} — retry with a larger PdfContentsPlaceholder reservation.");
        }

        var hexStart = checked((int)(ContentsOffset - _source.Length)) + 1; // +1 skips the opening '<'.
        var cursor = hexStart;
        foreach (var b in derCms)
        {
            _appendix[cursor++] = (byte)UpperHexDigits[b >> 4];
            _appendix[cursor++] = (byte)UpperHexDigits[b & 0xF];
        }

        var hexEnd = hexStart + (_reservationBytes * 2);
        while (cursor < hexEnd)
        {
            _appendix[cursor++] = (byte)'0';
        }

        _contentsPatched = true;
    }

    /// <summary>
    /// Flushes the finished, signed document: the source document's original bytes, followed
    /// by this session's fully-patched appendix buffer. The only point in this session's
    /// lifecycle that touches <paramref name="output"/> at all.
    /// </summary>
    /// <param name="output">The destination stream, positioned at its start.</param>
    /// <exception cref="InvalidOperationException"><see cref="PatchContents"/> has not been called yet — writing now would ship an all-zero placeholder as the signature.</exception>
    public void WriteTo(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!_contentsPatched)
        {
            throw new InvalidOperationException("SigningWriteSession.WriteTo was called before PatchContents — the /Contents reservation still holds all-zero placeholder bytes.");
        }

        _source.CopyTo(output);
        output.Write(_appendix, 0, _appendix.Length);
    }

    // Field layout mirrors ObjectSerializer.WriteByteRangePlaceholder's wire format exactly:
    // "[0" + 3x(" " + padWidth zero digits) + "]", so each field's start is computable from
    // offsetInBuffer and padWidth alone.
    private void PatchByteRange(long offsetInBuffer, int padWidth, long totalLength)
    {
        var start2 = ContentsOffset + ContentsLength;
        var len2 = totalLength - start2;

        var field1Start = checked((int)offsetInBuffer) + ByteRangePrefixLength;
        var field2Start = field1Start + padWidth + 1;
        var field3Start = field2Start + padWidth + 1;

        WritePaddedDecimal(field1Start, padWidth, ContentsOffset);
        WritePaddedDecimal(field2Start, padWidth, start2);
        WritePaddedDecimal(field3Start, padWidth, len2);
    }

    private void WritePaddedDecimal(int start, int width, long value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Length > width)
        {
            throw new PlumePdfException("PLUME5012", $"The /ByteRange value {value} needs {text.Length} decimal digits, but this session's PdfByteRangePlaceholder only reserved {width} — the source document is too large for this placeholder width; retry with a wider one.");
        }

        var padStart = start + (width - text.Length);
        for (var i = 0; i < width - text.Length; i++)
        {
            _appendix[start + i] = (byte)'0';
        }

        for (var i = 0; i < text.Length; i++)
        {
            _appendix[padStart + i] = (byte)text[i];
        }
    }
}
