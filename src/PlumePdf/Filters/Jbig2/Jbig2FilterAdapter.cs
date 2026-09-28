namespace PlumePdf.Filters.Jbig2;

/// <summary>
/// Bridges <see cref="Jbig2Decoder"/> to the public <see cref="IPdfFilter"/> seam,
/// reading a <c>/JBIG2Globals</c> entry from <c>/DecodeParms</c> — direct or indirect —
/// and resolving it via <see cref="IPdfFilterWithDecodeParms.Decode"/>'s <c>resolver</c>
/// parameter.
/// </summary>
/// <remarks>
/// <c>/JBIG2Globals</c> is conventionally an <em>indirect</em> reference to a separate stream
/// object holding shared symbol dictionaries — one level deeper than <c>/DecodeParms</c>
/// itself, which <see cref="PdfFilterRegistry.Decode"/> already resolves before this type
/// ever sees it. This adapter is the one filter that
/// needs the resolver again, inside its own already-resolved <c>/DecodeParms</c> dictionary,
/// which is exactly what the interface's <c>resolver</c> parameter carries through for. No
/// resolver supplied (or the resolved value isn't a stream) falls back to decoding without
/// globals — <see cref="DecodeWithGlobals"/> below is the same entry point a caller with an
/// already-resolved globals byte array in hand (no resolver needed) can call directly.
/// </remarks>
internal sealed class Jbig2FilterAdapter : IPdfFilterWithDecodeParms
{
    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
        DecodeCore(data, globals: null, columns: null, options, diagnostics, subject);

    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfDictionary? decodeParms, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject, Func<IndirectReference, object?>? resolver = null)
    {
        ReadOnlyMemory<byte>? globals = null;
        int? columns = null;

        if (decodeParms is not null)
        {
            if (decodeParms.TryGetValue(PdfName.Get("JBIG2Globals"), out var globalsValue))
            {
                if (globalsValue is PdfReference globalsReference)
                {
                    // The common case: an indirect reference to a separate stream object.
                    globalsValue = resolver?.Invoke(globalsReference.Target) as PdfObject ?? PdfNull.Instance;
                }

                if (globalsValue is PdfStream globalsStream)
                {
                    globals = ResolveGlobalsBytes(globalsStream, options, diagnostics, subject, resolver);
                }
            }

            if (decodeParms.TryGetValue(PdfName.Columns, out var columnsValue) && columnsValue is PdfNumber { IsInteger: true } columnsNumber && columnsNumber.TryToInt32(out var columnsInt))
            {
                columns = columnsInt;
            }
        }

        return DecodeCore(data, globals, columns, options, diagnostics, subject);
    }

    /// <summary>
    /// Resolves a <c>/JBIG2Globals</c> stream to the raw JBIG2 segment bytes the decoder
    /// needs by running the stream's OWN filter chain (real-world globals streams
    /// are routinely Flate-wrapped — LuraTech/ABBYY-class producers compress them like any
    /// other stream. The prior code passed <see cref="PdfStream.RawBytes"/> straight through,
    /// so a compressed globals stream fed zlib bytes to the segment parser, which silently
    /// yielded no symbol dictionaries; the text region then found zero symbols and painted
    /// nothing — rendered as a solid sheet through the image's own polarity, with no
    /// diagnostic anywhere).
    /// </summary>
    private static ReadOnlyMemory<byte> ResolveGlobalsBytes(PdfStream globalsStream, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject, Func<IndirectReference, object?>? resolver)
    {
        // A globals stream's payload is JBIG2 segment data, never itself JBIG2Decode-filtered;
        // a chain that says otherwise is malformed and would recurse through this adapter
        // (potentially unboundedly via cyclic indirect references), so it is refused up front.
        if (NamesJbig2Decode(globalsStream.Dictionary, resolver))
        {
            throw new PlumePdfException("PLUME3504", "JBIG2: the /JBIG2Globals stream declares /JBIG2Decode in its own filter chain - a globals stream holds raw JBIG2 segment bytes and cannot be JBIG2-coded itself (malformed or hostile document).");
        }

        try
        {
            return options.Filters.Decode(globalsStream.Dictionary, globalsStream.RawBytes.Span, options, diagnostics, subject, resolver);
        }
        catch (PlumePdfException ex)
        {
            // Without its globals the image's symbol dictionaries are gone and the decode
            // would silently paint nothing - re-code the failure so the caller's undecodable-
            // image posture (paint nothing, loudly) reports the real reason instead.
            throw new PlumePdfException("PLUME3504", $"JBIG2: the /JBIG2Globals stream's own filter chain failed to decode ({ex.Message}); the shared symbol dictionaries are unavailable.");
        }
    }

    /// <summary>Whether the stream's <c>/Filter</c> entry (name or array, direct or indirect) names <c>JBIG2Decode</c>.</summary>
    private static bool NamesJbig2Decode(PdfDictionary dictionary, Func<IndirectReference, object?>? resolver)
    {
        if (!dictionary.TryGetValue(PdfName.Filter, out var filterValue))
        {
            return false;
        }

        if (filterValue is PdfReference reference)
        {
            filterValue = resolver?.Invoke(reference.Target) as PdfObject ?? PdfNull.Instance;
        }

        return filterValue switch
        {
            PdfName name => name.Value == "JBIG2Decode",
            PdfArray array => array.OfType<PdfName>().Any(static n => n.Value == "JBIG2Decode"),
            _ => false,
        };
    }

    /// <summary>
    /// Decodes with an already-resolved globals byte array — for a caller that has already
    /// fetched an indirect <c>/JBIG2Globals</c> stream's bytes itself and doesn't need this
    /// adapter's own <c>/DecodeParms</c>-driven resolution (the more common path, used
    /// automatically by <see cref="PdfFilterRegistry.Decode"/> when a resolver is supplied).
    /// </summary>
    public byte[] DecodeWithGlobals(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte>? globals, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
        DecodeCore(data, globals, columns: null, options, diagnostics, subject);

    private static byte[] DecodeCore(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte>? globals, int? columns, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var result = Jbig2Decoder.Decode(data, globals, options, diagnostics, subject);
        return PackToDeviceGray(result.Page, result.Width, result.Height, columns);
    }

    /// <summary>
    /// Packs the decoded bitmap (true = black/foreground) into PDF's 1-bpp image convention:
    /// bit 0 = black, bit 1 = white (ISO 32000-1 §7.4.7's own JBIG2Decode note: "the decoding
    /// result shall always have one bit per pixel... 0 shall represent a black pixel and 1
    /// shall represent a white pixel" - JBIG2Decode has no <c>/BlackIs1</c>-style parameter;
    /// this polarity is fixed). <paramref name="columns"/>, when a caller's <c>/DecodeParms</c>
    /// declares one, only affects row padding - the JBIG2 stream's own page width is
    /// authoritative for pixel content.
    /// </summary>
    private static byte[] PackToDeviceGray(bool[,] page, int width, int height, int? columns)
    {
        var effectiveWidth = columns ?? width;
        var rowBytes = (effectiveWidth + 7) / 8;
        var output = new byte[rowBytes * height];

        // page[y,x] == true means black/foreground; ISO 32000-1's fixed JBIG2Decode polarity
        // wants bit 0 for black, so only white (page[y,x] == false) pixels set a bit.
        for (var y = 0; y < height; y++)
        {
            var rowOffset = y * rowBytes;
            var limit = Math.Min(effectiveWidth, width);
            for (var x = 0; x < limit; x++)
            {
                if (!page[y, x])
                {
                    output[rowOffset + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
        }

        return output;
    }
}
