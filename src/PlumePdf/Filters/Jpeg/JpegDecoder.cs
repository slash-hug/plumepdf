namespace PlumePdf.Filters;

/// <summary>The decoded result of <see cref="JpegDecoder.Decode"/>: pixel samples plus the metadata a caller (<c>DctFilterAdapter</c>, and eventually <c>RasterImage</c>) needs.</summary>
internal sealed class JpegDecodeResult
{
    /// <summary>Image width in pixels, as declared by the frame header.</summary>
    public required int Width { get; init; }

    /// <summary>Image height in pixels, as declared by the frame header.</summary>
    public required int Height { get; init; }

    /// <summary>1 (grayscale), 3 (RGB, converted from YCbCr unless the source already stored raw RGB), or 4 (CMYK, converted from YCCK when the Adobe transform says so).</summary>
    public required int ComponentCount { get; init; }

    /// <summary>Interleaved pixel samples, row-major, <see cref="ComponentCount"/> bytes per pixel.</summary>
    public required byte[] Pixels { get; init; }

    /// <summary>Horizontal resolution in dots per inch, from a JFIF or Exif marker, if either was present and declared an absolute (non-aspect-ratio) unit.</summary>
    public required double? XDpi { get; init; }

    /// <summary>Vertical resolution in dots per inch, from a JFIF or Exif marker, if either was present and declared an absolute (non-aspect-ratio) unit.</summary>
    public required double? YDpi { get; init; }

    /// <summary>
    /// The Adobe <c>APP14</c> transform this stream declared, if any. For a 4-component
    /// (CMYK) image this is the caller's signal that the emitted samples may need Adobe's
    /// storage-inversion convention undone at the color-space/<c>/Decode</c>-array layer
    /// (the codec itself neither inverts nor un-inverts; it returns exactly what the
    /// entropy-coded data decodes to, after only the YCCK-&gt;CMYK color transform).
    /// </summary>
    public required JpegAdobeTransform? AdobeTransform { get; init; }
}

/// <summary>
/// Baseline and progressive JPEG decode (ISO/IEC 10918-1): drives the marker stream
/// (<see cref="JpegMetadata"/>'s segment parsers), Huffman-decodes each scan's entropy-coded
/// data into per-block DCT coefficients (<see cref="JpegBitReader"/>), dequantizes and
/// inverse-transforms every block (<see cref="JpegIdct"/>), and combines components into
/// the requested pixel format (grayscale passthrough, YCbCr-&gt;RGB, or CMYK/YCCK per the
/// Adobe <c>APP14</c> transform). Arithmetic-coded and lossless/differential frame types are
/// coded refusals (<c>PLUME3200</c>/<c>PLUME3202</c>) - this codec supports only the
/// Huffman-coded sequential (<c>SOF0</c>/<c>SOF1</c>) and progressive (<c>SOF2</c>) frame
/// types real-world PDFs and PNG-adjacent tooling actually produce. Corrupt or truncated
/// scan data is lenient-by-default (the same policy <c>LZWDecode</c> follows):
/// decoded as far as the input allows, with a <c>PLUME3205</c>/<c>PLUME3207</c> diagnostic,
/// rather than losing an otherwise-decodable image to one bad byte.
/// </summary>
internal static class JpegDecoder
{
    private readonly record struct HuffmanKey(byte TableClass, byte Id);

    private sealed class ScanState
    {
        public required int[] DcPredictors { get; init; }
        public int EobRun;
    }

    /// <summary>Decodes a complete JPEG byte stream (<c>SOI</c> through <c>EOI</c>) into pixel samples.</summary>
    /// <param name="data">The complete encoded JPEG bytes, from <c>SOI</c> through <c>EOI</c>.</param>
    /// <param name="options">The active options, including resource limits.</param>
    /// <param name="diagnostics">The collection to append recoverable-deviation entries to, if any.</param>
    /// <param name="subject">The indirect object this data belongs to, for diagnostic context.</param>
    /// <param name="colorTransformOverride">
    /// A caller-supplied fallback color transform (e.g. from a PDF <c>DCTDecode</c> stream's
    /// <c>/ColorTransform</c> DecodeParms entry, ISO 32000-1 Table 13) used only when the
    /// stream itself carries no Adobe <c>APP14</c> marker - an <c>APP14</c> marker actually
    /// present in the stream always takes precedence, since it describes what transform the
    /// encoder actually applied.
    /// </param>
    public static JpegDecodeResult Decode(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject, JpegAdobeTransform? colorTransformOverride = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var bytes = data.ToArray();

        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != JpegMarkers.Soi)
        {
            throw new PlumePdfException("PLUME3201", "Input does not begin with a JPEG SOI marker (0xFFD8) - not a JPEG stream.");
        }

        var quantTables = new Dictionary<byte, JpegQuantTable>();
        var huffmanTables = new Dictionary<HuffmanKey, JpegHuffmanTable>();
        var restartInterval = 0;
        double? xDpiJfif = null, yDpiJfif = null, xDpiExif = null, yDpiExif = null;
        JpegAdobeTransform? adobeTransform = null;
        JpegFrameHeader? frame = null;
        short[][]? coefficients = null; // [componentIndex] -> flat coefficient plane, blockIndex*64 + i, natural order (one array per component instead of a heap short[64] per 8x8 block - a full-page scan allocated hundreds of thousands of them).
        int mcusPerLine = 0, mcusPerColumn = 0, hMax = 0, vMax = 0;
        var position = 2;
        var sawEoi = false;

        while (position < bytes.Length)
        {
            if (bytes[position] != 0xFF)
            {
                // Stray non-marker byte between segments (some encoders pad) - skip it and
                // keep looking, rather than treating the whole stream as unrecoverable.
                position++;
                continue;
            }

            // Skip 0xFF fill bytes (§B.1.1.5 permits an arbitrary run of them before a marker).
            while (position < bytes.Length && bytes[position] == 0xFF)
            {
                position++;
            }

            if (position >= bytes.Length)
            {
                break;
            }

            var marker = bytes[position++];
            if (marker == JpegMarkers.Eoi)
            {
                sawEoi = true;
                break;
            }

            if (marker == JpegMarkers.Tem || JpegMarkers.IsRestart(marker))
            {
                continue; // Standalone markers with no length/payload; a stray RSTn outside a scan is simply skipped.
            }

            if (position + 2 > bytes.Length)
            {
                FilterDiagnostics.ReportDeviation("PLUME3203", "JPEG stream truncated while reading a marker segment's length.", options, diagnostics, subject);
                break;
            }

            var length = (bytes[position] << 8) | bytes[position + 1];
            if (length < 2 || position + length > bytes.Length)
            {
                FilterDiagnostics.ReportDeviation("PLUME3203", $"JPEG marker 0xFF{marker:X2} declares a segment length ({length}) that runs past the end of the stream.", options, diagnostics, subject);
                break;
            }

            var payload = bytes.AsSpan(position + 2, length - 2);
            position += length;

            if (JpegMarkers.IsArithmeticSof(marker))
            {
                throw new PlumePdfException("PLUME3200", $"JPEG frame uses arithmetic entropy coding (SOF marker 0xFF{marker:X2}) - only Huffman coding is supported.");
            }

            if (JpegMarkers.IsUnsupportedSof(marker))
            {
                throw new PlumePdfException("PLUME3202", $"JPEG frame type (SOF marker 0xFF{marker:X2}) is lossless or differential - only baseline/extended-sequential (SOF0/SOF1) and progressive (SOF2) are supported.");
            }

            if (JpegMarkers.IsSupportedSof(marker))
            {
                frame = JpegMetadata.ParseSof(payload, marker);
                if (frame.Precision != 8)
                {
                    throw new PlumePdfException("PLUME3202", $"JPEG sample precision {frame.Precision} bits is not supported - only 8-bit precision is decoded.");
                }

                if (frame.Components.Count is not (1 or 3 or 4))
                {
                    throw new PlumePdfException("PLUME3204", $"JPEG frame declares {frame.Components.Count} components - only 1 (grayscale), 3 (RGB/YCbCr), or 4 (CMYK/YCCK) are supported.");
                }

                ValidateSamplingFactors(frame);

                hMax = frame.Components.Max(static c => (int)c.HorizontalSampling);
                vMax = frame.Components.Max(static c => (int)c.VerticalSampling);
                mcusPerLine = CeilDiv(frame.Width, 8 * hMax);
                mcusPerColumn = CeilDiv(frame.Height, 8 * vMax);

                CheckPixelBudget((long)frame.Width * frame.Height, options, subject);

                coefficients = new short[frame.Components.Count][];
                for (var c = 0; c < frame.Components.Count; c++)
                {
                    var comp = frame.Components[c];
                    var blocksPerLine = mcusPerLine * comp.HorizontalSampling;
                    var blocksPerColumn = mcusPerColumn * comp.VerticalSampling;
                    coefficients[c] = new short[checked(blocksPerLine * blocksPerColumn * 64)];
                }

                continue;
            }

            switch (marker)
            {
                case JpegMarkers.Dqt:
                    foreach (var table in JpegMetadata.ParseDqt(payload))
                    {
                        quantTables[table.Id] = table;
                    }

                    break;

                case JpegMarkers.Dht:
                    foreach (var entry in JpegMetadata.ParseDht(payload))
                    {
                        huffmanTables[new HuffmanKey(entry.TableClass, entry.Id)] = new JpegHuffmanTable(entry.Bits, entry.Values);
                    }

                    break;

                case JpegMarkers.Dri:
                    restartInterval = JpegMetadata.ParseDri(payload);
                    break;

                case JpegMarkers.App0:
                    var jfif = JpegMetadata.ParseApp0Jfif(payload);
                    if (jfif is { } j)
                    {
                        (xDpiJfif, yDpiJfif) = j;
                    }

                    break;

                case JpegMarkers.App1:
                    var exif = JpegMetadata.ParseApp1Exif(payload);
                    if (exif is { } e)
                    {
                        (xDpiExif, yDpiExif) = e;
                    }

                    break;

                case JpegMarkers.App14:
                    adobeTransform = JpegMetadata.ParseApp14Adobe(payload) ?? adobeTransform;
                    break;

                case JpegMarkers.Sos:
                    if (frame is null || coefficients is null)
                    {
                        throw new PlumePdfException("PLUME3203", "JPEG SOS marker appeared before any SOF frame header.");
                    }

                    var scan = JpegMetadata.ParseSos(payload);
                    position = DecodeScan(bytes, position, frame, scan, coefficients, quantTables, huffmanTables, restartInterval, mcusPerLine, mcusPerColumn, options, diagnostics, subject);
                    break;

                default:
                    break; // Unrecognized/uninteresting marker (COM, unused APPn, DNL, ...) - already skipped via `position += length` above.
            }
        }

        if (!sawEoi)
        {
            FilterDiagnostics.ReportDeviation("PLUME3207", "JPEG stream ended without an EOI marker; decoding whatever scan data was seen.", options, diagnostics, subject);
        }

        if (frame is null || coefficients is null)
        {
            throw new PlumePdfException("PLUME3203", "JPEG stream contains no SOF frame header - nothing to decode.");
        }

        // The stream's own APP14 marker (if any) drives the color-transform math and is what
        // JpegDecodeResult.AdobeTransform reports back (the CMYK/YCCK inversion signal needs to
        // know whether the FILE actually declared Adobe CMYK/YCCK, not merely what a PDF's
        // DecodeParms guessed); colorTransformOverride only fills in when the stream itself
        // is silent on the matter.
        var effectiveTransform = adobeTransform ?? colorTransformOverride;
        return Reconstruct(frame, coefficients, quantTables, hMax, vMax, mcusPerLine, mcusPerColumn, xDpiJfif ?? xDpiExif, yDpiJfif ?? yDpiExif ?? yDpiJfif, effectiveTransform, adobeTransform, options, diagnostics, subject);
    }

    private static void CheckPixelBudget(long pixelCount, PdfOptions options, IndirectReference? subject)
    {
        if (pixelCount > options.MaxImagePixels)
        {
            throw new PlumePdfException("PLUME3208", $"JPEG frame ({pixelCount} pixels) exceeds the {options.MaxImagePixels}-pixel cap (PdfOptions.MaxImagePixels) - refusing to allocate (possible decompression bomb).");
        }
    }

    // A malformed SOF's per-component sampling factors drive both the coefficient/plane
    // allocation geometry (blocksPerLine/blocksPerColumn = mcusPerLine/mcusPerColumn * Hi/Vi,
    // Reconstruct ~L756) and the reconstruction-time sampling math (SampleComponent's
    // sx = x*Hi/hMax, sy = y*Vi/vMax). A declared factor of 0 makes that component's
    // allocated plane zero-width or zero-height while SampleComponent still indexes into it
    // for every x/y in the frame - an uncaught IndexOutOfRangeException reachable from
    // RasterImage.Decode/Pdf.FromImages. ITU-T.81 Table B.2 reserves 0 outright, and every
    // interoperating encoder/decoder (e.g. libjpeg's MAX_SAMP_FACTOR) also caps at 4, so this
    // validates the full 1-4 range up front rather than merely rejecting 0 - it also keeps a
    // single component's amplified plane allocation from ballooning past what
    // CheckPixelBudget already capped for the frame as a whole.
    private static void ValidateSamplingFactors(JpegFrameHeader frame)
    {
        foreach (var comp in frame.Components)
        {
            if (comp.HorizontalSampling is < 1 or > 4 || comp.VerticalSampling is < 1 or > 4)
            {
                throw new PlumePdfException("PLUME3203", $"Malformed JPEG SOF: component {comp.Id} declares sampling factors {comp.HorizontalSampling}x{comp.VerticalSampling} - horizontal and vertical sampling factors must each be between 1 and 4.");
            }
        }
    }

    private static int CeilDiv(int numerator, int denominator) => (numerator + denominator - 1) / denominator;

    // Decodes one scan's entropy-coded data (immediately following the SOS payload at
    // `startPosition`) and returns the byte position just past it - either the terminating
    // marker's leading 0xFF (found by JpegBitReader, see its type summary), or the end of the
    // buffer if the scan data was truncated.
    private static int DecodeScan(
        byte[] bytes, int startPosition, JpegFrameHeader frame, JpegScanHeader scan,
        short[][] coefficients, Dictionary<byte, JpegQuantTable> quantTables, Dictionary<HuffmanKey, JpegHuffmanTable> huffmanTables,
        int restartInterval, int mcusPerLine, int mcusPerColumn, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var reader = new JpegBitReader(bytes, startPosition);
        var componentIndices = new int[scan.Components.Count];
        var dcTables = new JpegHuffmanTable[scan.Components.Count];
        var acTables = new JpegHuffmanTable[scan.Components.Count];

        for (var i = 0; i < scan.Components.Count; i++)
        {
            var idx = IndexOfComponent(frame, scan.Components[i].ComponentSelector);
            componentIndices[i] = idx;
            if (scan.SpectralStart == 0)
            {
                huffmanTables.TryGetValue(new HuffmanKey(0, scan.Components[i].DcTableId), out dcTables[i]!);
            }

            if (scan.SpectralEnd > 0)
            {
                huffmanTables.TryGetValue(new HuffmanKey(1, scan.Components[i].AcTableId), out acTables[i]!);
            }
        }

        var state = new ScanState { DcPredictors = new int[scan.Components.Count] };
        var truncated = false;

        if (scan.Components.Count > 1)
        {
            // Interleaved scan (baseline's single scan, or a progressive DC scan spanning
            // multiple components): iterate MCU by MCU, visiting each component's Hi x Vi
            // block sub-grid within the MCU (§B.2.3).
            var unitsSinceRestart = 0;
            var totalUnits = mcusPerLine * mcusPerColumn;
            var unitIndex = 0;
            for (var my = 0; my < mcusPerColumn && !truncated; my++)
            {
                for (var mx = 0; mx < mcusPerLine && !truncated; mx++)
                {
                    for (var ci = 0; ci < scan.Components.Count; ci++)
                    {
                        var comp = frame.Components[componentIndices[ci]];
                        var blocksPerLine = mcusPerLine * comp.HorizontalSampling;
                        for (var v = 0; v < comp.VerticalSampling; v++)
                        {
                            for (var h = 0; h < comp.HorizontalSampling; h++)
                            {
                                var by = (my * comp.VerticalSampling) + v;
                                var bx = (mx * comp.HorizontalSampling) + h;
                                var block = coefficients[componentIndices[ci]].AsSpan((((by * blocksPerLine) + bx) * 64), 64);
                                if (!DecodeBlock(reader, block, scan, dcTables[ci], acTables[ci], state, ci, options, diagnostics, subject))
                                {
                                    truncated = true;
                                }
                            }
                        }
                    }

                    unitIndex++;
                    unitsSinceRestart++;
                    if (!truncated && restartInterval > 0 && unitsSinceRestart == restartInterval && unitIndex < totalUnits)
                    {
                        truncated = !ResyncRestart(reader, state, options, diagnostics, subject);
                        unitsSinceRestart = 0;
                    }
                }
            }
        }
        else
        {
            // Non-interleaved scan (every progressive AC scan, and a progressive DC scan
            // that happens to name only one component): iterate the component's own compact
            // block grid (§B.2.3's non-interleaved data ordering), not the MCU-padded grid.
            var comp = frame.Components[componentIndices[0]];
            var sampledWidth = CeilDiv(frame.Width * comp.HorizontalSampling, hMaxOf(frame));
            var sampledHeight = CeilDiv(frame.Height * comp.VerticalSampling, vMaxOf(frame));
            var blocksPerLineActual = CeilDiv(sampledWidth, 8);
            var blocksPerColumnActual = CeilDiv(sampledHeight, 8);
            var blocksPerLineStorage = mcusPerLine * comp.HorizontalSampling;
            var totalUnits = blocksPerLineActual * blocksPerColumnActual;
            var unitsSinceRestart = 0;
            var unitIndex = 0;

            for (var by = 0; by < blocksPerColumnActual && !truncated; by++)
            {
                for (var bx = 0; bx < blocksPerLineActual && !truncated; bx++)
                {
                    var block = coefficients[componentIndices[0]].AsSpan((((by * blocksPerLineStorage) + bx) * 64), 64);
                    if (!DecodeBlock(reader, block, scan, dcTables[0], acTables[0], state, 0, options, diagnostics, subject))
                    {
                        truncated = true;
                    }

                    unitIndex++;
                    unitsSinceRestart++;
                    if (!truncated && restartInterval > 0 && unitsSinceRestart == restartInterval && unitIndex < totalUnits)
                    {
                        truncated = !ResyncRestart(reader, state, options, diagnostics, subject);
                        unitsSinceRestart = 0;
                    }
                }
            }
        }

        if (truncated)
        {
            FilterDiagnostics.ReportDeviation("PLUME3205", "JPEG entropy-coded scan data ended before all blocks were decoded; the remaining blocks are left at whatever was decoded before the truncation.", options, diagnostics, subject);
        }

        return reader.Position;
    }

    private static bool ResyncRestart(JpegBitReader reader, ScanState state, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        if (!reader.TryConsumeRestartMarker())
        {
            FilterDiagnostics.ReportDeviation("PLUME3206", "JPEG restart marker not found where the restart interval expected one; stopping this scan.", options, diagnostics, subject);
            return false;
        }

        Array.Clear(state.DcPredictors);
        state.EobRun = 0;
        return true;
    }

    private static int hMaxOf(JpegFrameHeader frame) => frame.Components.Max(static c => (int)c.HorizontalSampling);

    private static int vMaxOf(JpegFrameHeader frame) => frame.Components.Max(static c => (int)c.VerticalSampling);

    private static int IndexOfComponent(JpegFrameHeader frame, byte id)
    {
        for (var i = 0; i < frame.Components.Count; i++)
        {
            if (frame.Components[i].Id == id)
            {
                return i;
            }
        }

        throw new PlumePdfException("PLUME3203", $"JPEG scan references component id {id}, which the frame header did not declare.");
    }

    // Decodes one 8x8 block's coefficients for the current scan's spectral band into
    // `block` (natural order, 64 shorts, accumulated across scans for progressive frames).
    // Returns false the moment the bit reader can no longer supply bits (corrupt/truncated
    // data) - the caller stops the whole scan at that point rather than guessing.
    private static bool DecodeBlock(
        JpegBitReader reader, Span<short> block, JpegScanHeader scan, JpegHuffmanTable? dcTable, JpegHuffmanTable? acTable,
        ScanState state, int scanComponentIndex, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var progressive = scan.SuccessiveApproxHigh != 0 || scan.SuccessiveApproxLow != 0 || scan.SpectralStart != 0 || scan.SpectralEnd != 63;

        if (!progressive)
        {
            return DecodeBaselineBlock(reader, block, dcTable, acTable, state, scanComponentIndex, options, diagnostics, subject);
        }

        if (scan.SpectralStart == 0)
        {
            return scan.SuccessiveApproxHigh == 0
                ? DecodeDcFirst(reader, block, dcTable, state, scanComponentIndex, scan.SuccessiveApproxLow)
                : DecodeDcRefine(reader, block, scan.SuccessiveApproxLow);
        }

        return scan.SuccessiveApproxHigh == 0
            ? DecodeAcFirst(reader, block, acTable, state, scan.SpectralStart, scan.SpectralEnd, scan.SuccessiveApproxLow)
            : DecodeAcRefine(reader, block, acTable, state, scan.SpectralStart, scan.SpectralEnd, scan.SuccessiveApproxLow);
    }

    private static bool DecodeBaselineBlock(
        JpegBitReader reader, Span<short> block, JpegHuffmanTable? dcTable, JpegHuffmanTable? acTable,
        ScanState state, int scanComponentIndex, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        if (dcTable is null || acTable is null)
        {
            FilterDiagnostics.ReportDeviation("PLUME3209", "JPEG scan references a Huffman table id no DHT segment defined; stopping this scan.", options, diagnostics, subject);
            return false;
        }

        if (!reader.TryDecodeHuffman(dcTable, out var dcSize) || !reader.TryReceiveExtend(dcSize, out var diff))
        {
            return false;
        }

        state.DcPredictors[scanComponentIndex] += diff;
        block[0] = (short)state.DcPredictors[scanComponentIndex];

        var k = 1;
        while (k <= 63)
        {
            if (!reader.TryDecodeHuffman(acTable, out var rs))
            {
                return false;
            }

            var run = rs >> 4;
            var size = rs & 0x0F;

            if (size == 0)
            {
                if (run != 15)
                {
                    break; // EOB: all remaining coefficients are zero.
                }

                k += 16; // ZRL: 16 zero coefficients.
                continue;
            }

            k += run;
            if (k > 63)
            {
                break;
            }

            if (!reader.TryReceiveExtend(size, out var value))
            {
                return false;
            }

            block[JpegHuffmanTable.ZigzagToNatural[k]] = (short)value;
            k++;
        }

        return true;
    }

    private static bool DecodeDcFirst(JpegBitReader reader, Span<short> block, JpegHuffmanTable? dcTable, ScanState state, int scanComponentIndex, int approxLow)
    {
        if (dcTable is null)
        {
            return false;
        }

        if (!reader.TryDecodeHuffman(dcTable, out var size) || !reader.TryReceiveExtend(size, out var diff))
        {
            return false;
        }

        state.DcPredictors[scanComponentIndex] += diff;
        block[0] = (short)(state.DcPredictors[scanComponentIndex] << approxLow);
        return true;
    }

    private static bool DecodeDcRefine(JpegBitReader reader, Span<short> block, int approxLow)
    {
        if (!reader.TryReadBit(out var bit))
        {
            return false;
        }

        if (bit != 0)
        {
            block[0] = (short)((ushort)block[0] | (1 << approxLow));
        }

        return true;
    }

    // Progressive AC "first" scan (Ah=0, Ss>0, §G.1.2.2): introduces new nonzero
    // coefficients (scaled by approxLow) and tracks an EOB run - a count of trailing blocks
    // (this one included) whose remaining coefficients in this scan's band are all zero, so
    // the caller can skip straight past them without reading a symbol per block.
    private static bool DecodeAcFirst(JpegBitReader reader, Span<short> block, JpegHuffmanTable? acTable, ScanState state, int spectralStart, int spectralEnd, int approxLow)
    {
        if (state.EobRun > 0)
        {
            state.EobRun--;
            return true;
        }

        if (acTable is null)
        {
            return false;
        }

        var k = spectralStart;
        while (k <= spectralEnd)
        {
            if (!reader.TryDecodeHuffman(acTable, out var rs))
            {
                return false;
            }

            var run = rs >> 4;
            var size = rs & 0x0F;

            if (size == 0)
            {
                if (run < 15)
                {
                    // EOB run: 2^run + (run extra bits) blocks, including this one, have no
                    // more nonzero coefficients in this band.
                    var eobBits = 0;
                    if (run > 0 && !reader.TryReadBits(run, out eobBits))
                    {
                        return false;
                    }

                    state.EobRun = (1 << run) + eobBits - 1;
                    return true;
                }

                k += 16; // ZRL.
                continue;
            }

            k += run;
            if (k > spectralEnd)
            {
                return true;
            }

            if (!reader.TryReceiveExtend(size, out var value))
            {
                return false;
            }

            block[JpegHuffmanTable.ZigzagToNatural[k]] = (short)(value << approxLow);
            k++;
        }

        return true;
    }

    // Progressive AC "refinement" scan (Ah>0, Ss>0, §G.1.2.3): the most intricate part of
    // the JPEG algorithm - existing nonzero coefficients each get one correction bit, while
    // zero-run-coded symbols both introduce brand-new nonzero coefficients (with a sign bit)
    // and skip over already-nonzero coefficients along the way (which still each consume a
    // correction bit as the run passes them), and an EOB run defers only the *remaining*
    // coefficients' correction bits to later blocks, not the ones already visited.
    private static bool DecodeAcRefine(JpegBitReader reader, Span<short> block, JpegHuffmanTable? acTable, ScanState state, int spectralStart, int spectralEnd, int approxLow)
    {
        var positive = (short)(1 << approxLow);
        var negative = (short)(-1 << approxLow);
        var k = spectralStart;

        if (state.EobRun == 0)
        {
            if (acTable is null)
            {
                return false;
            }

            while (k <= spectralEnd)
            {
                if (!reader.TryDecodeHuffman(acTable, out var rs))
                {
                    return false;
                }

                var run = rs >> 4;
                var size = rs & 0x0F;
                var newValue = 0;

                if (size == 0)
                {
                    if (run < 15)
                    {
                        var eobBits = 0;
                        if (run > 0 && !reader.TryReadBits(run, out eobBits))
                        {
                            return false;
                        }

                        state.EobRun = (1 << run) + eobBits;
                        break; // Fall through to the EOB correction-bit pass below.
                    }

                    // ZRL: skip 16 zero-history coefficients, applying correction bits to
                    // any that are already nonzero along the way.
                }
                else
                {
                    if (!reader.TryReadBit(out var signBit))
                    {
                        return false;
                    }

                    // §G.1.2.3: a 1 bit means positive, a 0 bit means negative - the opposite
                    // reading is an easy transcription slip (confirmed against djpeg/PIL
                    // ground truth: every mismatched coefficient came out exactly sign-flipped
                    // before this fix).
                    newValue = signBit != 0 ? positive : negative;
                }

                // Walk forward `run` zero-history coefficients (correcting any nonzero ones
                // found along the way), then place newValue (if any) at the run's end.
                while (k <= spectralEnd)
                {
                    var natural = JpegHuffmanTable.ZigzagToNatural[k];
                    if (block[natural] != 0)
                    {
                        if (!reader.TryReadBit(out var correction))
                        {
                            return false;
                        }

                        if (correction != 0)
                        {
                            block[natural] += block[natural] > 0 ? positive : negative;
                        }
                    }
                    else
                    {
                        if (run == 0)
                        {
                            if (newValue != 0)
                            {
                                block[natural] = (short)newValue;
                            }

                            k++;
                            break;
                        }

                        run--;
                    }

                    k++;
                }
            }
        }

        if (state.EobRun > 0)
        {
            // EOB correction pass: every remaining coefficient in the band that is already
            // nonzero gets one correction bit; the band's zero coefficients stay zero for
            // this scan (a later scan may still introduce them).
            while (k <= spectralEnd)
            {
                var natural = JpegHuffmanTable.ZigzagToNatural[k];
                if (block[natural] != 0)
                {
                    if (!reader.TryReadBit(out var correction))
                    {
                        return false;
                    }

                    if (correction != 0)
                    {
                        block[natural] += block[natural] > 0 ? positive : negative;
                    }
                }

                k++;
            }

            state.EobRun--;
        }

        return true;
    }

    private static JpegDecodeResult Reconstruct(
        JpegFrameHeader frame, short[][] coefficients, Dictionary<byte, JpegQuantTable> quantTables,
        int hMax, int vMax, int mcusPerLine, int mcusPerColumn, double? xDpi, double? yDpi,
        JpegAdobeTransform? effectiveTransform, JpegAdobeTransform? reportedTransform, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var componentCount = frame.Components.Count;
        var planes = new byte[componentCount][];
        var planeStrides = new int[componentCount];

        Span<float> dequantized = stackalloc float[64];
        Span<float> spatial = stackalloc float[64];
        Span<float> quantF = stackalloc float[64];

        for (var c = 0; c < componentCount; c++)
        {
            var comp = frame.Components[c];
            if (!quantTables.TryGetValue(comp.QuantTableId, out var quant))
            {
                FilterDiagnostics.ReportDeviation("PLUME3209", $"JPEG component references quantization table id {comp.QuantTableId}, which no DQT segment defined; treating it as all-ones (no scaling).", options, diagnostics, subject);
                quant = new JpegQuantTable(comp.QuantTableId, CreateUnitQuantTable());
            }

            var blocksPerLine = mcusPerLine * comp.HorizontalSampling;
            var blocksPerColumn = mcusPerColumn * comp.VerticalSampling;
            var sampledWidth = blocksPerLine * 8;
            var sampledHeight = blocksPerColumn * 8;
            var plane = new byte[sampledWidth * sampledHeight];
            planeStrides[c] = sampledWidth;

            var blocks = coefficients[c];

            // The ushort->float dequant cast hoisted out of the per-block loop:
            // one table per component instead of 64 casts per block. (ushort -> float is exact,
            // so the products are bit-identical to the inline cast.)
            for (var i = 0; i < 64; i++)
            {
                quantF[i] = (float)quant.NaturalOrderValues[i];
            }

            for (var by = 0; by < blocksPerColumn; by++)
            {
                for (var bx = 0; bx < blocksPerLine; bx++)
                {
                    var block = blocks.AsSpan((((by * blocksPerLine) + bx) * 64), 64);
                    var originX = bx * 8;
                    var originY = by * 8;

                    // DC-only shortcut: scanned pages are dominated by blocks
                    // with no AC energy, and for those the full transform's zero terms contribute
                    // exact zeros — every output sample is the one value below, bit-identical to
                    // running Transform (JpegIdct's own accumulation reduces to exactly this
                    // expression when coefficients 1..63 are zero).
                    if (IsDcOnly(block))
                    {
                        var dequantizedDc = block[0] * quantF[0]; // float first, exactly like the dequant loop below.
                        var flat = ClampToByte((float)(0.5 * (JpegIdct.Basis00 * (0.5 * (JpegIdct.Basis00 * (double)dequantizedDc)))) + 128f);
                        for (var y = 0; y < 8; y++)
                        {
                            plane.AsSpan(((originY + y) * sampledWidth) + originX, 8).Fill(flat);
                        }

                        continue;
                    }

                    for (var i = 0; i < 64; i++)
                    {
                        dequantized[i] = block[i] * quantF[i];
                    }

                    JpegIdct.Transform(dequantized, spatial);

                    for (var y = 0; y < 8; y++)
                    {
                        var rowOffset = ((originY + y) * sampledWidth) + originX;
                        for (var x = 0; x < 8; x++)
                        {
                            plane[rowOffset + x] = ClampToByte(spatial[(y * 8) + x] + 128f);
                        }
                    }
                }
            }

            planes[c] = plane;
        }

        var pixels = new byte[frame.Width * frame.Height * componentCount];
        var useRawRgb = componentCount == 3 && effectiveTransform is JpegAdobeTransform.Unknown or null && IsRgbComponentIds(frame);
        var useNoTransformCmyk = componentCount == 4 && effectiveTransform is not JpegAdobeTransform.Ycck;

        // Nearest-neighbor (block-replication) upsampling — djpeg's `-nosmooth` interpretation,
        // which DjpegInteropTests deliberately matches. The sx = (x*Hi)/hMax and sy = (y*Vi)/vMax
        // sample selection is unchanged from the old per-pixel SampleComponent, but the two
        // integer divisions per component per pixel are hoisted: the horizontal
        // map is precomputed once per component, the vertical term once per component per row,
        // and the per-pixel component switch became one switch per row.
        var sxMaps = new int[componentCount][];
        for (var c = 0; c < componentCount; c++)
        {
            var h = frame.Components[c].HorizontalSampling;
            var map = new int[frame.Width];
            for (var x = 0; x < frame.Width; x++)
            {
                map[x] = (x * h) / hMax;
            }

            sxMaps[c] = map;
        }

        Span<int> rowBases = stackalloc int[componentCount];
        for (var y = 0; y < frame.Height; y++)
        {
            for (var c = 0; c < componentCount; c++)
            {
                rowBases[c] = ((y * frame.Components[c].VerticalSampling) / vMax) * planeStrides[c];
            }

            var outRow = pixels.AsSpan(y * frame.Width * componentCount, frame.Width * componentCount);
            switch (componentCount)
            {
                case 1:
                    {
                        var plane = planes[0];
                        var sx = sxMaps[0];
                        var rowBase = rowBases[0];
                        for (var x = 0; x < frame.Width; x++)
                        {
                            outRow[x] = plane[rowBase + sx[x]];
                        }

                        break;
                    }

                case 3 when useRawRgb:
                case 4 when useNoTransformCmyk:
                    {
                        for (var c = 0; c < componentCount; c++)
                        {
                            var plane = planes[c];
                            var sx = sxMaps[c];
                            var rowBase = rowBases[c];
                            var o = c;
                            for (var x = 0; x < frame.Width; x++, o += componentCount)
                            {
                                outRow[o] = plane[rowBase + sx[x]];
                            }
                        }

                        break;
                    }

                case 3:
                    {
                        var (py, pcb, pcr) = (planes[0], planes[1], planes[2]);
                        var (sy0, sy1, sy2) = (sxMaps[0], sxMaps[1], sxMaps[2]);
                        var (b0, b1, b2) = (rowBases[0], rowBases[1], rowBases[2]);
                        var o = 0;
                        for (var x = 0; x < frame.Width; x++, o += 3)
                        {
                            var yy = py[b0 + sy0[x]];
                            var cb = pcb[b1 + sy1[x]] - 128;
                            var cr = pcr[b2 + sy2[x]] - 128;
                            outRow[o] = ClampToByte(yy + (1.402f * cr));
                            outRow[o + 1] = ClampToByte(yy - (0.344136f * cb) - (0.714136f * cr));
                            outRow[o + 2] = ClampToByte(yy + (1.772f * cb));
                        }

                        break;
                    }

                case 4:
                    {
                        var (py, pcb, pcr, pk) = (planes[0], planes[1], planes[2], planes[3]);
                        var (sy0, sy1, sy2, sy3) = (sxMaps[0], sxMaps[1], sxMaps[2], sxMaps[3]);
                        var (b0, b1, b2, b3) = (rowBases[0], rowBases[1], rowBases[2], rowBases[3]);
                        var o = 0;
                        for (var x = 0; x < frame.Width; x++, o += 4)
                        {
                            var yy = py[b0 + sy0[x]];
                            var cb = pcb[b1 + sy1[x]] - 128;
                            var cr = pcr[b2 + sy2[x]] - 128;
                            var r = ClampToByte(yy + (1.402f * cr));
                            var g = ClampToByte(yy - (0.344136f * cb) - (0.714136f * cr));
                            var b = ClampToByte(yy + (1.772f * cb));
                            outRow[o] = (byte)(255 - r);
                            outRow[o + 1] = (byte)(255 - g);
                            outRow[o + 2] = (byte)(255 - b);
                            outRow[o + 3] = pk[b3 + sy3[x]];
                        }

                        break;
                    }
            }
        }

        return new JpegDecodeResult
        {
            Width = frame.Width,
            Height = frame.Height,
            ComponentCount = componentCount,
            Pixels = pixels,
            XDpi = xDpi,
            YDpi = yDpi,
            AdobeTransform = reportedTransform,
        };
    }

    /// <summary>Whether coefficients 1..63 are all zero — the DC-only reconstruction shortcut's guard.</summary>
    private static bool IsDcOnly(ReadOnlySpan<short> block)
    {
        for (var i = 1; i < 64; i++)
        {
            if (block[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static ushort[] CreateUnitQuantTable()
    {
        var table = new ushort[64];
        Array.Fill(table, (ushort)1);
        return table;
    }

    private static bool IsRgbComponentIds(JpegFrameHeader frame) =>
        frame.Components.Count == 3 &&
        frame.Components[0].Id == (byte)'R' && frame.Components[1].Id == (byte)'G' && frame.Components[2].Id == (byte)'B';

    private static byte ClampToByte(float value) => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);
}
