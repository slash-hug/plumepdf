namespace PlumePdf.Filters;

/// <summary>
/// Container-agnostic parameters for a CCITT Group 3/4 decode: no
/// <see cref="IndirectReference"/>, no <see cref="PdfOptions"/> here — <see cref="CcittFaxFilterAdapter"/>
/// carries PDF <c>/DecodeParms</c> context, and <c>TiffFrameDecoder</c> (the TIFF reader)
/// builds one of these directly from TIFF tags when a strip is Compression 2/3/4.
/// </summary>
/// <param name="K">
/// ISO 32000-1 §7.4.6 Table 11 <c>/K</c>: <c>K &lt; 0</c> selects pure two-dimensional (G4/T.6)
/// coding, <c>K == 0</c> selects one-dimensional (MH/T.4) coding, and <c>K &gt; 0</c> selects
/// mixed one/two-dimensional (G3-2D/T.4) coding, where each row carries a leading 1-bit tag
/// (1 = this row is 1D, 0 = this row is 2D relative to the previous row).
/// </param>
/// <param name="Columns">The row width in pixels (<c>/Columns</c>, default 1728).</param>
/// <param name="Rows">
/// The expected row count (<c>/Rows</c>). Zero or negative means "unknown" — decoding
/// continues until an EOFB (two consecutive EOL codes) is seen, or the input is exhausted.
/// </param>
/// <param name="EncodedByteAlign">
/// <c>/EncodedByteAlign</c>: when <see langword="true"/>, each row's encoded data begins on
/// a byte boundary and any unused bits at the end of the previous row are skipped.
/// </param>
/// <param name="BlackIs1">
/// <c>/BlackIs1</c>: when <see langword="false"/> (the PDF default), a packed output bit of 0
/// means black and 1 means white ("the reverse of the normal PDF convention" is what
/// <c>BlackIs1 = true</c> selects — the reversed, 1-means-black convention). This only governs
/// the packed-byte materialization in <see cref="CcittFaxEngine"/>'s <c>Decode</c> methods; the bitmap
/// materialization JBIG2's MMR path uses (<see cref="CcittFaxEngine.DecodeMmrBitmap"/>) always
/// treats <see langword="true"/> as foreground/black, independent of this flag.
/// </param>
/// <param name="EndOfBlock">
/// <c>/EndOfBlock</c>: when <see langword="true"/> (the default) and <see cref="Rows"/> is
/// unknown, an EOFB marks the true end of data; when <see langword="false"/>, the caller's
/// <see cref="Rows"/> (or the stream's exhaustion) is the only end signal.
/// </param>
internal readonly record struct CcittFaxParameters(
    int K = 0,
    int Columns = 1728,
    int Rows = 0,
    bool EncodedByteAlign = false,
    bool BlackIs1 = false,
    bool EndOfBlock = true);

/// <summary>Why a CCITT row failed to decode fully — carried alongside the rows decoded so far (the decode-as-far-as-possible policy).</summary>
internal enum CcittRowError
{
    /// <summary>No error — decoding reached the end of input/row count cleanly.</summary>
    None,

    /// <summary>A run-length or mode code did not match any entry in the applicable Huffman table.</summary>
    BadCodeWord,

    /// <summary>The input ended before the current row's code word (or the row itself) finished.</summary>
    PrematureEndOfData,
}

/// <summary>
/// The <c>CCITTFaxDecode</c> filter (ISO 32000-1 §7.4.6): a clean-room C# port of the
/// Group 3 (1D/MH and mixed 1D/2D) and Group 4 (pure 2D) fax coding algorithms defined by
/// ITU-T Recommendations T.4 and T.6. Ported from pdf.js's <c>ccitt.js</c> (pinned tag
/// <c>v5.6.205</c> — the last plain-JS tag before pdf.js's WASM rewrite removed the readable
/// source) and cross-checked against PdfPig's port of Apache PDFBox's
/// <c>CCITTFaxDecoderStream</c> (both Apache-2.0). Both port sources are credited in NOTICE.
/// </summary>
/// <remarks>
/// <para>
/// <b>Algorithm shape.</b> Each row is described as a "coding line": an ascending list of
/// pixel-column positions where the pixel color changes, starting from an imaginary white
/// pixel at column -1. Two-dimensional coding (G3-2D and G4) predicts each row's changes
/// relative to the previous row's ("reference line") changes via three modes — Pass,
/// Horizontal, and Vertical(-3..+3) — using the standard changing-element definitions
/// (<c>a0</c>/<c>a1</c>/<c>a2</c> on the coding line, <c>b1</c>/<c>b2</c> on the reference
/// line). One-dimensional coding (MH) codes each run directly against the White/Black
/// terminating+makeup Huffman tables (ITU-T T.4 Tables 2-4) with no reference line at all.
/// </para>
/// <para>
/// <b>Leniency.</b> A run-length/mode code the applicable Huffman table has no
/// entry for, or input that runs out mid-code, stops decoding at that point: rows decoded
/// before the failure are returned, a <c>PLUME34xx</c> diagnostic records what happened, and
/// <see cref="PdfOptions.Strict"/> turns it into a thrown <see cref="PlumePdfException"/>
/// instead. A row whose decoded run lengths would overrun <see cref="CcittFaxParameters.Columns"/>
/// is clamped to the row width (also diagnosed) rather than discarded — the row stays usable.
/// If the very first row fails before any pixel data was produced, there is nothing partial
/// to return, so this throws even outside <c>Strict</c> (mirrors <c>LZWDecode</c>'s
/// byte-0-failure precedent).
/// </para>
/// </remarks>
internal static class CcittFaxEngine
{
    /// <summary>
    /// The pixel-budget cap this engine falls back to when a caller uses the convenience
    /// overload below rather than passing <see cref="PdfOptions.MaxImagePixels"/> explicitly.
    /// 128 Mpixels covers an A0 page at 600 DPI with headroom.
    /// </summary>
    internal const long DefaultMaxDecodedPixels = 1L << 27;

    /// <summary>
    /// Decodes CCITT-encoded <paramref name="data"/> into packed, row-major 1-bpp output —
    /// <c>ceil(Columns / 8)</c> bytes per row, MSB-first, bit meaning per
    /// <see cref="CcittFaxParameters.BlackIs1"/>. Enforces <see cref="PdfOptions.MaxImagePixels"/>
    /// as the decompression-bomb guard.
    /// </summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, CcittFaxParameters parms, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
        Decode(data, parms, options.MaxImagePixels, options, diagnostics, subject);

    /// <summary>Overload taking an explicit pixel-budget cap (bypasses <see cref="DefaultMaxDecodedPixels"/> — the seam the 5-arg overload above uses to pass <see cref="PdfOptions.MaxImagePixels"/> through).</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, CcittFaxParameters parms, long maxDecodedPixels, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var columns = parms.Columns > 0 ? parms.Columns : 1728;
        var rowBytes = (columns + 7) / 8;

        if (parms.Rows > 0 && (long)columns * parms.Rows > maxDecodedPixels)
        {
            throw new PlumePdfException("PLUME3405", $"CCITTFaxDecode: {columns}x{parms.Rows} exceeds the {maxDecodedPixels}-pixel decode cap - refusing to continue (possible decompression bomb).");
        }

        var rowsList = DecodeAllRows(data, parms, maxDecodedPixels, options, diagnostics, subject, out var error);

        // Robustness against a stream whose /EncodedByteAlign flag lies (set, but the rows
        // are NOT byte-aligned) desyncs mid-decode under the flag's per-row alignment skips —
        // yet PDFium renders such pages. Probe once with alignment disabled (silently — no
        // second wave of diagnostics) and keep whichever attempt decoded more rows; the
        // recovery is announced with its own deviation so the flag mismatch stays visible.
        if (error != CcittRowError.None && parms.EncodedByteAlign && parms.Rows > 0 && rowsList.Count < parms.Rows)
        {
            var unaligned = DecodeAllRows(data, parms with { EncodedByteAlign = false }, maxDecodedPixels, options, diagnostics: null, subject, out var retryError);
            if (unaligned.Count > rowsList.Count)
            {
                FilterDiagnostics.ReportDeviation("PLUME3404", $"CCITTFaxDecode: /EncodedByteAlign is set but the data only decodes without byte alignment ({rowsList.Count} vs {unaligned.Count} of {parms.Rows} rows); ignoring the flag.", options, diagnostics, subject);
                rowsList = unaligned;
                error = retryError;
            }
        }

        if (rowsList.Count == 0 && error != CcittRowError.None)
        {
            throw new PlumePdfException("PLUME3401", $"CCITTFaxDecode: the first row could not be decoded ({error}) - nothing to return.");
        }

        var output = new byte[rowsList.Count * rowBytes];
        var oneMeansWhite = !parms.BlackIs1; // default: bit 1 = white, bit 0 = black (ISO 32000-1 Table 11)
        for (var r = 0; r < rowsList.Count; r++)
        {
            PackRow(rowsList[r], columns, oneMeansWhite, output.AsSpan(r * rowBytes, rowBytes));
        }

        return output;
    }

    /// <summary>
    /// Decodes exactly one pure-G4 (T.6) bitmap of known dimensions directly from a bit
    /// position within <paramref name="data"/> — the shape JBIG2's MMR-coded generic region
    /// needs: no byte alignment, no EOL/EOFB scanning, foreground (<see langword="true"/>)
    /// always means black regardless of any PDF <c>/BlackIs1</c> convention.
    /// </summary>
    /// <param name="data">The bytes containing the MMR-coded bitmap.</param>
    /// <param name="bitPosition">
    /// The bit offset (MSB-first from <paramref name="data"/>[0] bit 7) to start reading at;
    /// updated in place to the bit position immediately after the decoded data.
    /// </param>
    /// <param name="columns">The bitmap width in pixels.</param>
    /// <param name="rows">The bitmap height in pixels.</param>
    /// <param name="maxDecodedPixels">
    /// The decoded-pixel-buffer cap (<see cref="PdfOptions.MaxImagePixels"/>): both
    /// dimensions come straight from a document-supplied JBIG2 region header, so the budget
    /// is enforced <b>before</b> the bitmap allocation, mirroring <c>Decode</c>'s own
    /// up-front PLUME3405 check.
    /// </param>
    /// <returns>A <paramref name="rows"/>-by-<paramref name="columns"/> bitmap, row-major, true = black/foreground.</returns>
    public static bool[,] DecodeMmrBitmap(ReadOnlySpan<byte> data, ref int bitPosition, int columns, int rows, long maxDecodedPixels)
    {
        if ((long)columns * rows > maxDecodedPixels)
        {
            throw new PlumePdfException("PLUME3405", $"CCITTFaxDecode: {columns}x{rows} exceeds the {maxDecodedPixels}-pixel decode cap - refusing to continue (possible decompression bomb).");
        }

        var reader = new CcittBitReader(data, bitPosition);
        var bitmap = new bool[rows, columns];
        var reference = new List<int>();
        var coding = new List<int>(64);

        for (var row = 0; row < rows; row++)
        {
            coding.Clear();
            var ok = TryDecode2DRow(ref reader, columns, reference, coding, out var error, out _);
            FillBitmapRow(bitmap, row, coding, columns);
            reference.Clear();
            reference.AddRange(coding);
            if (!ok)
            {
                // MMR bitmaps are dimensionally fixed by the JBIG2 region header; a decode
                // failure mid-bitmap still yields a best-effort row (white-padded) so the
                // caller gets a correctly-shaped bitmap rather than a truncated one - the
                // caller (Jbig2Decoder) is responsible for recording the deviation, since
                // this low-level entry point has no diagnostics/subject context.
                _ = error;
                break;
            }
        }

        bitPosition = reader.BitPosition;
        return bitmap;
    }

    private static List<List<int>> DecodeAllRows(ReadOnlySpan<byte> data, CcittFaxParameters parms, long maxDecodedPixels, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject, out CcittRowError error)
    {
        var reader = new CcittBitReader(data, 0);
        var columns = parms.Columns > 0 ? parms.Columns : 1728;
        var result = new List<List<int>>();
        var reference = new List<int>();
        var unknownRowCount = parms.Rows <= 0;
        var maxRows = unknownRowCount ? int.MaxValue : parms.Rows;
        var overrunReported = false;
        error = CcittRowError.None;

        while (result.Count < maxRows)
        {
            if (parms.EncodedByteAlign)
            {
                reader.ByteAlign();
            }

            if (reader.AtEnd)
            {
                if (result.Count < maxRows && !unknownRowCount)
                {
                    error = CcittRowError.PrematureEndOfData;
                    if (result.Count > 0)
                    {
                        // Nothing decoded yet (result.Count == 0): stay silent here and let
                        // Decode's byte-0-failure check throw a single, clearer PLUME3401 -
                        // reporting a "returning the rows decoded so far" diagnostic when
                        // there are zero such rows would be misleading.
                        FilterDiagnostics.ReportDeviation("PLUME3402", $"CCITTFaxDecode: input ended after {result.Count} of {parms.Rows} declared rows; returning the rows decoded so far.", options, diagnostics, subject);
                    }
                }

                break;
            }

            if (unknownRowCount && parms.EndOfBlock && TryConsumeEofb(ref reader))
            {
                break;
            }

            var is1D = parms.K == 0;
            if (parms.K > 0)
            {
                // Optional fill+EOL(s) (T.4 §4.1.2/§4.1.4) before the 1D/2D row tag bit -
                // lenient, fill-tolerant, and repeated (an RTC is six EOLs back to back).
                while (TryConsumeFillAndEol(ref reader))
                {
                }

                if (!reader.TryReadBit(out var tag))
                {
                    error = CcittRowError.PrematureEndOfData;
                    break;
                }

                is1D = tag == 1;
            }
            else
            {
                // K == 0 (pure 1D) and K < 0 (G4/T.6) both tolerate fill+EOL prefixes: T.6
                // defines no EOL, but real fax-hardware streams carry them anyway and
                // PDFium/poppler render those pages — a strict reader fails row 0 and the
                // whole image degrades. Unambiguous per TryConsumeFillAndEol's remarks.
                while (TryConsumeFillAndEol(ref reader))
                {
                }
            }

            var coding = new List<int>(64);
            bool ok;
            bool overran;
            if (is1D)
            {
                ok = TryDecode1DRow(ref reader, columns, coding, out error, out overran);
            }
            else
            {
                ok = TryDecode2DRow(ref reader, columns, reference, coding, out error, out overran);
            }

            if (overran && !overrunReported)
            {
                overrunReported = true;
                FilterDiagnostics.ReportDeviation("PLUME3403", $"CCITTFaxDecode: row {result.Count} decoded a run past Columns ({columns}); clamped to the row width.", options, diagnostics, subject);
            }

            if (!ok && coding.Count == 0)
            {
                // Nothing at all decoded for this row - stop here, keep everything before it.
                var code = error == CcittRowError.PrematureEndOfData ? "PLUME3402" : "PLUME3401";
                FilterDiagnostics.ReportDeviation(code, $"CCITTFaxDecode: row {result.Count} failed to decode ({error}); returning the {result.Count} row(s) decoded before it.", options, diagnostics, subject);
                break;
            }

            reference = coding;
            result.Add(coding);

            if ((long)columns * result.Count > maxDecodedPixels)
            {
                throw new PlumePdfException("PLUME3405", $"CCITTFaxDecode: decoded output exceeds the {maxDecodedPixels}-pixel decode cap - refusing to continue (possible decompression bomb).");
            }

            if (!ok)
            {
                // Partial row (some transitions decoded, then the row-terminating code word
                // failed): keep the partial row - PackRow trails it with the last color out
                // to Columns - and stop, since resynchronizing mid-stream isn't reliable.
                var code = error == CcittRowError.PrematureEndOfData ? "PLUME3402" : "PLUME3401";
                FilterDiagnostics.ReportDeviation(code, $"CCITTFaxDecode: row {result.Count - 1} ended early ({error}); kept its partial data and stopped.", options, diagnostics, subject);
                break;
            }
        }

        return result;
    }

    // --- 1D (Modified Huffman) row decode -----------------------------------------------

    private static bool TryDecode1DRow(ref CcittBitReader reader, int columns, List<int> coding, out CcittRowError error, out bool overran)
    {
        var position = 0;
        var white = true;
        error = CcittRowError.None;
        overran = false;

        while (position < columns)
        {
            if (!TryReadRun(ref reader, white, out var run, out error))
            {
                return false;
            }

            position += run;
            if (position > columns)
            {
                position = columns; // row overrun - clamp; caller reports PLUME3403.
                overran = true;
            }

            coding.Add(position);
            white = !white;
        }

        return true;
    }

    // --- 2D (G3-2D / G4) row decode ------------------------------------------------------

    private static bool TryDecode2DRow(ref CcittBitReader reader, int columns, List<int> referenceLine, List<int> coding, out CcittRowError error, out bool overran)
    {
        var a0 = -1;
        var white = true;
        error = CcittRowError.None;
        overran = false;

        while (a0 < columns)
        {
            var (b1, b2) = FindB1B2(referenceLine, a0, white, columns);

            if (!TryReadMode(ref reader, out var mode, out error))
            {
                return false;
            }

            switch (mode)
            {
                case CcittMode.Pass:
                    a0 = b2;
                    break;

                case CcittMode.Horizontal:
                    {
                        var start = a0 < 0 ? 0 : a0;
                        if (!TryReadRun(ref reader, white, out var run1, out error) ||
                            !TryReadRun(ref reader, !white, out var run2, out error))
                        {
                            return false;
                        }

                        var rawA1 = start + run1;
                        var rawA2 = rawA1 + run2;
                        if (rawA1 > columns || rawA2 > columns)
                        {
                            overran = true;
                        }

                        var a1 = Math.Min(rawA1, columns);
                        var a2 = Math.Min(rawA2, columns);
                        coding.Add(a1);
                        coding.Add(a2);
                        a0 = a2;
                        break;
                    }

                case CcittMode.VerticalV0:
                case CcittMode.VerticalR1:
                case CcittMode.VerticalR2:
                case CcittMode.VerticalR3:
                case CcittMode.VerticalL1:
                case CcittMode.VerticalL2:
                case CcittMode.VerticalL3:
                    {
                        var delta = mode switch
                        {
                            CcittMode.VerticalV0 => 0,
                            CcittMode.VerticalR1 => 1,
                            CcittMode.VerticalR2 => 2,
                            CcittMode.VerticalR3 => 3,
                            CcittMode.VerticalL1 => -1,
                            CcittMode.VerticalL2 => -2,
                            CcittMode.VerticalL3 => -3,
                            _ => 0,
                        };
                        var a1 = Math.Clamp(b1 + delta, 0, columns);
                        coding.Add(a1);
                        a0 = a1;
                        white = !white;
                        break;
                    }

                default:
                    error = CcittRowError.BadCodeWord;
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds b1 (the first changing element on the reference line strictly right of
    /// <paramref name="a0"/> whose new color is opposite <paramref name="a0Color"/>) and b2
    /// (the next changing element after b1). The reference line's changes strictly alternate
    /// new-color starting with black at index 0 (every row starts white), so once the first
    /// element past a0 is found, at most one step fixes the parity.
    /// </summary>
    private static (int B1, int B2) FindB1B2(List<int> referenceLine, int a0, bool a0Color, int columns)
    {
        var i = 0;
        var count = referenceLine.Count;
        while (i < count && referenceLine[i] <= a0)
        {
            i++;
        }

        // referenceLine[i]'s new color is black when i is even (0-based), white when odd.
        var wantBlackAtB1 = a0Color; // opposite of a0Color: b1's new color != a0Color; b1 is black-starting when a0Color is white.
        if (i < count && (i % 2 == 0) != wantBlackAtB1)
        {
            i++;
        }

        var b1 = i < count ? referenceLine[i] : columns;
        var b2 = i + 1 < count ? referenceLine[i + 1] : columns;
        return (b1, b2);
    }

    // --- Materialization ------------------------------------------------------------------

    private static void PackRow(List<int> coding, int columns, bool oneMeansWhite, Span<byte> row)
    {
        // Bytes start all-zero (Clear). A pixel needs its bit set to 1 exactly when the run
        // it's in is the color that maps to bit-value 1 under BlackIs1 - white when
        // oneMeansWhite (BlackIs1=false, the PDF default), black when !oneMeansWhite
        // (BlackIs1=true). The other color's runs are already correctly 0 and need no write.
        row.Clear();
        var position = 0;
        var white = true;
        foreach (var change in coding)
        {
            var end = Math.Min(change, columns);
            if (white == oneMeansWhite)
            {
                SetBits(row, position, end);
            }

            position = end;
            white = !white;
            if (position >= columns)
            {
                break;
            }
        }

        if (position < columns && white == oneMeansWhite)
        {
            SetBits(row, position, columns);
        }
    }

    private static void SetBits(Span<byte> row, int start, int end)
    {
        for (var col = start; col < end; col++)
        {
            row[col >> 3] |= (byte)(0x80 >> (col & 7));
        }
    }

    private static void FillBitmapRow(bool[,] bitmap, int row, List<int> coding, int columns)
    {
        var position = 0;
        var white = true;
        foreach (var change in coding)
        {
            var end = Math.Min(change, columns);
            if (!white)
            {
                for (var col = position; col < end; col++)
                {
                    bitmap[row, col] = true;
                }
            }

            position = end;
            white = !white;
            if (position >= columns)
            {
                break;
            }
        }

        if (position < columns && !white)
        {
            for (var col = position; col < columns; col++)
            {
                bitmap[row, col] = true;
            }
        }
    }

    // --- Mode + run-length Huffman decoding ------------------------------------------------

    private enum CcittMode
    {
        Pass,
        Horizontal,
        VerticalV0,
        VerticalR1,
        VerticalR2,
        VerticalR3,
        VerticalL1,
        VerticalL2,
        VerticalL3,
    }

    // ITU-T T.4 Table 1 (mode codes), prefix-free, tested shortest-first.
    private static bool TryReadMode(ref CcittBitReader reader, out CcittMode mode, out CcittRowError error)
    {
        mode = default;
        error = CcittRowError.None;
        var code = 0;
        for (var len = 1; len <= 7; len++)
        {
            if (!reader.TryReadBit(out var bit))
            {
                error = CcittRowError.PrematureEndOfData;
                return false;
            }

            code = (code << 1) | bit;

            switch (len)
            {
                case 1 when code == 0b1:
                    mode = CcittMode.VerticalV0;
                    return true;
                case 3 when code == 0b011:
                    mode = CcittMode.VerticalR1;
                    return true;
                case 3 when code == 0b010:
                    mode = CcittMode.VerticalL1;
                    return true;
                case 3 when code == 0b001:
                    mode = CcittMode.Horizontal;
                    return true;
                case 4 when code == 0b0001:
                    mode = CcittMode.Pass;
                    return true;
                case 6 when code == 0b000011:
                    mode = CcittMode.VerticalR2;
                    return true;
                case 6 when code == 0b000010:
                    mode = CcittMode.VerticalL2;
                    return true;
                case 7 when code == 0b0000011:
                    mode = CcittMode.VerticalR3;
                    return true;
                case 7 when code == 0b0000010:
                    mode = CcittMode.VerticalL3;
                    return true;
            }
        }

        error = CcittRowError.BadCodeWord;
        return false;
    }

    /// <summary>Reads one full run (summing makeup codes until a terminating code, ITU-T T.4 §4.1.1) for the given color.</summary>
    private static bool TryReadRun(ref CcittBitReader reader, bool white, out int run, out CcittRowError error)
    {
        run = 0;
        error = CcittRowError.None;
        while (true)
        {
            if (!TryReadRunCode(ref reader, white, out var value, out error))
            {
                return false;
            }

            run += value;
            if (value < 64)
            {
                return true; // terminating code
            }

            // Makeup code: keep accumulating. A run may mix color-specific makeup codes
            // (64-1728) with the shared extended makeup codes (1792-2560, T.4 Table 4).
        }
    }

    private static bool TryReadRunCode(ref CcittBitReader reader, bool white, out int runLength, out CcittRowError error)
    {
        var table = white ? CcittTables.White : CcittTables.Black;
        error = CcittRowError.None;
        var code = 0;
        for (var len = 1; len <= CcittTables.MaxCodeLength; len++)
        {
            if (!reader.TryReadBit(out var bit))
            {
                error = CcittRowError.PrematureEndOfData;
                runLength = 0;
                return false;
            }

            code = (code << 1) | bit;
            if (table[len].TryGetValue(code, out var value))
            {
                runLength = value;
                return true;
            }

            if (CcittTables.Extended[len].TryGetValue(code, out var extValue))
            {
                runLength = extValue;
                return true;
            }
        }

        error = CcittRowError.BadCodeWord;
        runLength = 0;
        return false;
    }

    // --- EOL / EOFB (lenient - only consumed on an exact match) ----------------------------

    // T.4 §4.1.2 permits variable-length FILL (zero bits) before an EOL, and real fax hardware
    // emits fill+EOL prefixes even ahead of G4 (T.6) data, where the spec defines no EOL at all
    // — PDFium renders such streams; a strict decoder dies on row one and the whole page
    // degrades. Consuming is unambiguous for every K: no valid 1D code begins with
    // 11 zero bits (the longest white make-up code carries 8 leading zeros) and no 2D mode code
    // begins with more than 7, so "eleven-plus zeros then a one" can only be fill+EOL. Bounded
    // so a hostile all-zeros stream cannot spin here; anything not matching rewinds fully.
    private const int MaxFillBitsBeforeEol = 4096;

    private static bool TryConsumeFillAndEol(ref CcittBitReader reader)
    {
        var mark = reader.BitPosition;
        var zeros = 0;
        while (zeros <= MaxFillBitsBeforeEol && reader.TryReadBit(out var bit))
        {
            if (bit == 1)
            {
                if (zeros >= 11)
                {
                    return true; // fill (zeros - 11 bits) + a complete 12-bit EOL.
                }

                break; // A real code word started — rewind untouched.
            }

            zeros++;
        }

        reader.Seek(mark);
        return false;
    }

    private static bool TryConsumeEofb(ref CcittBitReader reader)
    {
        var mark = reader.BitPosition;
        if (MatchesEol(ref reader) && MatchesEol(ref reader))
        {
            return true;
        }

        reader.Seek(mark);
        return false;
    }

    private static bool MatchesEol(ref CcittBitReader reader)
    {
        // 000000000001 - eleven zero bits then a one bit.
        for (var i = 0; i < 11; i++)
        {
            if (!reader.TryReadBit(out var bit) || bit != 0)
            {
                return false;
            }
        }

        return reader.TryReadBit(out var last) && last == 1;
    }
}

/// <summary>MSB-first bit reader over a byte span with absolute-position seek/byte-align support, shared by the row decoder and JBIG2's MMR entry point.</summary>
internal ref struct CcittBitReader(ReadOnlySpan<byte> data, int startBit)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _bitPosition = startBit;

    public readonly int BitPosition => _bitPosition;

    public readonly bool AtEnd => _bitPosition >= _data.Length * 8;

    public bool TryReadBit(out int bit)
    {
        var byteIndex = _bitPosition >> 3;
        if (byteIndex >= _data.Length)
        {
            bit = 0;
            return false;
        }

        var bitIndex = 7 - (_bitPosition & 7);
        bit = (_data[byteIndex] >> bitIndex) & 1;
        _bitPosition++;
        return true;
    }

    public void ByteAlign() => _bitPosition = (_bitPosition + 7) & ~7;

    public void Seek(int bitPosition) => _bitPosition = bitPosition;
}

/// <summary>
/// The White and Black run-length Huffman tables (ITU-T T.4 Tables 2 and 3 — terminating
/// codes 0-63 — plus Table 3's makeup codes 64-1728, and Table 4's extended makeup codes
/// 1792-2560 shared by both colors). Standard, publicly-published algorithm data — the same
/// numeric table underlies every conformant T.4/T.6 implementation (libtiff's <c>t4.h</c>,
/// pdf.js's <c>ccitt.js</c>, PDFBox's <c>CCITTFaxDecoderStream</c>) — not ISO spec prose.
/// Indexed by bit length so <see cref="CcittFaxEngine"/> can decode one bit at a time and
/// probe for a match at each length (the codes are a proper prefix-free/Huffman code, so at
/// most one length ever matches for a given bit sequence).
/// </summary>
internal static class CcittTables
{
    internal const int MaxCodeLength = 13;

    // A static constructor (rather than field initializers, which run in fragile textual
    // declaration order, ECMA-334 §15.5.6.2) so White/Black/Extended can be built from the
    // raw entry tables regardless of where those tables are declared in this file.
    internal static readonly Dictionary<int, int>[] White;
    internal static readonly Dictionary<int, int>[] Black;
    internal static readonly Dictionary<int, int>[] Extended;

    static CcittTables()
    {
        White = Build(WhiteEntries);
        Black = Build(BlackEntries);
        Extended = Build(ExtendedEntries);
    }

    private static Dictionary<int, int>[] Build((string Bits, int Run)[] entries)
    {
        var table = new Dictionary<int, int>[MaxCodeLength + 1];
        for (var i = 0; i <= MaxCodeLength; i++)
        {
            table[i] = [];
        }

        foreach (var (bits, run) in entries)
        {
            table[bits.Length][Convert.ToInt32(bits, 2)] = run;
        }

        return table;
    }

    // ITU-T T.4 Table 2/3: White terminating codes (run 0-63).
    private static readonly (string Bits, int Run)[] WhiteTerminating =
    [
        ("00110101", 0), ("000111", 1), ("0111", 2), ("1000", 3), ("1011", 4), ("1100", 5), ("1110", 6), ("1111", 7),
        ("10011", 8), ("10100", 9), ("00111", 10), ("01000", 11), ("001000", 12), ("000011", 13), ("110100", 14), ("110101", 15),
        ("101010", 16), ("101011", 17), ("0100111", 18), ("0001100", 19), ("0001000", 20), ("0010111", 21), ("0000011", 22), ("0000100", 23),
        ("0101000", 24), ("0101011", 25), ("0010011", 26), ("0100100", 27), ("0011000", 28), ("00000010", 29), ("00000011", 30), ("00011010", 31),
        ("00011011", 32), ("00010010", 33), ("00010011", 34), ("00010100", 35), ("00010101", 36), ("00010110", 37), ("00010111", 38), ("00101000", 39),
        ("00101001", 40), ("00101010", 41), ("00101011", 42), ("00101100", 43), ("00101101", 44), ("00000100", 45), ("00000101", 46), ("00001010", 47),
        ("00001011", 48), ("01010010", 49), ("01010011", 50), ("01010100", 51), ("01010101", 52), ("00100100", 53), ("00100101", 54), ("01011000", 55),
        // Root cause of a past decode bug: 61-63 were transcribed wrong (61/62 carried codes that are not
        // in T.4 Table 2 at all, and 63 carried 61's code) — so a white run of 61 decoded as 63
        // (+2 pixel shift, desyncing every following code word into garbage/overrun) and runs
        // of 62/63 were BadCodeWord truncations. Ordinary scanned text hits 61-63-pixel white
        // gaps constantly, which is why real-world CCITT pages truncated or "inverted" while
        // every synthetic fixture (whose content happened to avoid those run lengths) passed.
        ("01011001", 56), ("01011010", 57), ("01011011", 58), ("01001010", 59), ("01001011", 60), ("00110010", 61), ("00110011", 62), ("00110100", 63),
    ];

    // ITU-T T.4 Table 3: White makeup codes (run 64-1728).
    private static readonly (string Bits, int Run)[] WhiteMakeup =
    [
        ("11011", 64), ("10010", 128), ("010111", 192), ("0110111", 256), ("00110110", 320), ("00110111", 384), ("01100100", 448), ("01100101", 512),
        ("01101000", 576), ("01100111", 640), ("011001100", 704), ("011001101", 768), ("011010010", 832), ("011010011", 896), ("011010100", 960), ("011010101", 1024),
        ("011010110", 1088), ("011010111", 1152), ("011011000", 1216), ("011011001", 1280), ("011011010", 1344), ("011011011", 1408), ("010011000", 1472), ("010011001", 1536),
        ("010011010", 1600), ("011000", 1664), ("010011011", 1728),
    ];

    // ITU-T T.4 Table 2: Black terminating codes (run 0-63).
    private static readonly (string Bits, int Run)[] BlackTerminating =
    [
        ("0000110111", 0), ("010", 1), ("11", 2), ("10", 3), ("011", 4), ("0011", 5), ("0010", 6), ("00011", 7),
        ("000101", 8), ("000100", 9), ("0000100", 10), ("0000101", 11), ("0000111", 12), ("00000100", 13), ("00000111", 14), ("000011000", 15),
        ("0000010111", 16), ("0000011000", 17), ("0000001000", 18), ("00001100111", 19), ("00001101000", 20), ("00001101100", 21), ("00000110111", 22), ("00000101000", 23),
        ("00000010111", 24), ("00000011000", 25), ("000011001010", 26), ("000011001011", 27), ("000011001100", 28), ("000011001101", 29), ("000001101000", 30), ("000001101001", 31),
        ("000001101010", 32), ("000001101011", 33), ("000011010010", 34), ("000011010011", 35), ("000011010100", 36), ("000011010101", 37), ("000011010110", 38), ("000011010111", 39),
        ("000001101100", 40), ("000001101101", 41), ("000011011010", 42), ("000011011011", 43), ("000001010100", 44), ("000001010101", 45), ("000001010110", 46), ("000001010111", 47),
        ("000001100100", 48), ("000001100101", 49), ("000001010010", 50), ("000001010011", 51), ("000000100100", 52), ("000000110111", 53), ("000000111000", 54), ("000000100111", 55),
        ("000000101000", 56), ("000001011000", 57), ("000001011001", 58), ("000000101011", 59), ("000000101100", 60), ("000001011010", 61), ("000001100110", 62), ("000001100111", 63),
    ];

    // ITU-T T.4 Table 3: Black makeup codes (run 64-1728).
    private static readonly (string Bits, int Run)[] BlackMakeup =
    [
        ("0000001111", 64), ("000011001000", 128), ("000011001001", 192), ("000001011011", 256), ("000000110011", 320), ("000000110100", 384), ("000000110101", 448),
        ("0000001101100", 512), ("0000001101101", 576), ("0000001001010", 640), ("0000001001011", 704), ("0000001001100", 768), ("0000001001101", 832), ("0000001110010", 896),
        ("0000001110011", 960), ("0000001110100", 1024), ("0000001110101", 1088), ("0000001110110", 1152), ("0000001110111", 1216), ("0000001010010", 1280), ("0000001010011", 1344),
        ("0000001010100", 1408), ("0000001010101", 1472), ("0000001011010", 1536), ("0000001011011", 1600), ("0000001100100", 1664), ("0000001100101", 1728),
    ];

    // ITU-T T.4 Table 4: Extended makeup codes (run 1792-2560), shared by both colors.
    private static readonly (string Bits, int Run)[] ExtendedEntries =
    [
        ("00000001000", 1792), ("00000001100", 1856), ("00000001101", 1920), ("000000010010", 1984), ("000000010011", 2048), ("000000010100", 2112),
        ("000000010101", 2176), ("000000010110", 2240), ("000000010111", 2304), ("000000011100", 2368), ("000000011101", 2432), ("000000011110", 2496), ("000000011111", 2560),
    ];

    private static readonly (string Bits, int Run)[] WhiteEntries = [.. WhiteTerminating, .. WhiteMakeup];
    private static readonly (string Bits, int Run)[] BlackEntries = [.. BlackTerminating, .. BlackMakeup];
}

/// <summary>
/// Bridges <see cref="CcittFaxEngine"/> to the public <see cref="IPdfFilter"/> seam
/// (colocated per the <c>LzwFilterAdapter</c> pattern): reads <c>/K</c>, <c>/Columns</c>,
/// <c>/Rows</c>, <c>/EncodedByteAlign</c>, <c>/BlackIs1</c>, and <c>/EndOfBlock</c> from
/// <c>/DecodeParms</c> (ISO 32000-1 Table 11). This adapter is registered for
/// <c>CCITTFaxDecode</c>/<c>CCF</c> on <see cref="PdfFilterRegistry.Default"/>; constructing a
/// private registry and calling <see cref="PdfFilterRegistry.Register"/> exercises it standalone too.
/// </summary>
internal sealed class CcittFaxFilterAdapter : IPdfFilterWithDecodeParms
{
    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
        CcittFaxEngine.Decode(data.Span, default, options, diagnostics, subject);

    /// <inheritdoc/>
    public byte[] Decode(ReadOnlyMemory<byte> data, PdfDictionary? decodeParms, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject, Func<IndirectReference, object?>? resolver = null)
    {
        var parms = ReadParameters(decodeParms, options, diagnostics, subject);
        return CcittFaxEngine.Decode(data.Span, parms, options, diagnostics, subject);
    }

    private static CcittFaxParameters ReadParameters(PdfDictionary? decodeParms, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        if (decodeParms is null)
        {
            return new CcittFaxParameters();
        }

        return new CcittFaxParameters(
            K: GetInt(decodeParms, "K", 0),
            Columns: GetInt(decodeParms, "Columns", 1728),
            Rows: GetInt(decodeParms, "Rows", 0),
            EncodedByteAlign: GetBool(decodeParms, "EncodedByteAlign", false),
            BlackIs1: GetBool(decodeParms, "BlackIs1", false),
            EndOfBlock: GetBool(decodeParms, "EndOfBlock", true));

        // The two local functions below intentionally never throw or report a deviation for
        // a wrong-typed entry - a stray non-integer /K or non-boolean /BlackIs1 just falls
        // back to the ISO-defined default (the same leniency extends to malformed
        // /DecodeParms, matching PdfFilterRegistry.ApplyPredictor's own GetInt).
        static int GetInt(PdfDictionary dict, string key, int fallback) =>
            dict.TryGetValue(PdfName.Get(key), out var value) && value is PdfNumber { IsInteger: true } number && number.TryToInt32(out var converted) ? converted : fallback;

        static bool GetBool(PdfDictionary dict, string key, bool fallback) =>
            dict.TryGetValue(PdfName.Get(key), out var value) && value is PdfBoolean boolean ? boolean.Value : fallback;
    }
}
