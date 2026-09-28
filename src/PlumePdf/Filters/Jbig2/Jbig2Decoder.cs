namespace PlumePdf.Filters.Jbig2;

/// <summary>One JBIG2 symbol: a small bilevel bitmap (a character shape, typically) held in a symbol dictionary and referenced by ID from a text region.</summary>
internal sealed class Jbig2Symbol(bool[,] bitmap)
{
    public bool[,] Bitmap { get; } = bitmap;

    public int Width => Bitmap.GetLength(1);

    public int Height => Bitmap.GetLength(0);
}

/// <summary>
/// The result of decoding a JBIG2 stream: the assembled page bitmap plus
/// its dimensions. <see cref="Jbig2FilterAdapter"/> packs <see cref="Page"/> into PDF's
/// 1-bpp <c>ImageMask</c>/<c>DeviceGray</c> byte convention.
/// </summary>
internal sealed class Jbig2DecodeResult(bool[,] page, int width, int height)
{
    public bool[,] Page { get; } = page;

    public int Width { get; } = width;

    public int Height { get; } = height;
}

/// <summary>
/// Decodes a JBIG2 embedded stream (ITU-T T.88): arithmetic-coded generic
/// regions (templates 0-3, TPGDON), MMR-coded generic regions (delegated to
/// <see cref="CcittFaxEngine"/>), generic refinement regions (templates 0-1, no
/// TPGRON), symbol dictionaries, and text regions - all arithmetic-coded only. Huffman-coded
/// symbol dictionaries/text regions, halftone regions, and TPGRON-enabled refinement are
/// coded per-segment fallbacks (<see cref="FilterDiagnostics.ReportDeviation"/>,
/// the region is skipped rather than the whole page failing) — 1.x scope.
/// Ported from the structure of pdf.js's <c>jbig2.js</c> (pinned tag <c>v5.6.205</c>);
/// the porting-source cross-checks that informed this file's context-template tables, the
/// integer-decoding procedure, and the symbol/text-region field layouts are documented in NOTICE.
/// </summary>
internal static class Jbig2Decoder
{
    /// <summary>Fallback segment-count cap for callers that don't have a <see cref="PdfOptions"/> instance's <see cref="PdfOptions.MaxJbig2Segments"/> in hand already.</summary>
    internal const int DefaultMaxSegments = 100_000;

    /// <summary>Fallback symbol-count cap for callers that don't have a <see cref="PdfOptions"/> instance's <see cref="PdfOptions.MaxJbig2Symbols"/> in hand already.</summary>
    internal const int DefaultMaxSymbols = 100_000;

    /// <summary>Fallback pixel-budget cap for callers that don't have a <see cref="PdfOptions"/> instance's <see cref="PdfOptions.MaxImagePixels"/> in hand already.</summary>
    internal const long DefaultMaxDecodedPixels = 1L << 27;

    // ITU-T T.88 Table 6 (generic region coding templates 0-3): each entry is a causal
    // {x,y} offset from the pixel being decoded. AT pixels are appended and the combined
    // list re-sorted by (y, x) before use (verified against pdf.js's decodeBitmap).
    private static readonly (int X, int Y)[][] CodingTemplates =
    [
        [(-1, -2), (0, -2), (1, -2), (-2, -1), (-1, -1), (0, -1), (1, -1), (2, -1), (-4, 0), (-3, 0), (-2, 0), (-1, 0)],
        [(-1, -2), (0, -2), (1, -2), (2, -2), (-2, -1), (-1, -1), (0, -1), (1, -1), (2, -1), (-3, 0), (-2, 0), (-1, 0)],
        [(-1, -2), (0, -2), (1, -2), (-2, -1), (-1, -1), (0, -1), (1, -1), (-2, 0), (-1, 0)],
        [(-3, -1), (-2, -1), (-1, -1), (0, -1), (1, -1), (-4, 0), (-3, 0), (-2, 0), (-1, 0)],
    ];

    // The typical-prediction (TPGDON) SLTP pseudo-pixel context value per template - fixed
    // constants (T.88 6.2.5.7), one per GBTEMPLATE.
    private static readonly int[] ReusedContexts = [0x9B25, 0x0795, 0x00E5, 0x0195];

    // ITU-T T.88 Table 12 (refinement templates 0-1): "coding" pixels look at the bitmap
    // under construction (causal); "reference" pixels look at the (already fully decoded)
    // reference bitmap and may be non-causal (any direction) since that bitmap is complete.
    private static readonly (int X, int Y)[][] RefinementCodingTemplates =
    [
        [(0, -1), (1, -1), (-1, 0)],
        [(-1, -1), (0, -1), (1, -1), (-1, 0)],
    ];

    private static readonly (int X, int Y)[][] RefinementReferenceTemplates =
    [
        [(0, -1), (1, -1), (-1, 0), (0, 0), (1, 0), (-1, 1), (0, 1), (1, 1)],
        [(0, -1), (-1, 0), (0, 0), (1, 0), (0, 1), (1, 1)],
    ];

    /// <summary>Decodes a JBIG2 embedded stream (optionally preceded by a globals stream's symbol dictionaries) into a page bitmap. Enforces <see cref="PdfOptions.MaxJbig2Segments"/>/<see cref="PdfOptions.MaxJbig2Symbols"/>/<see cref="PdfOptions.MaxImagePixels"/> as the decompression-bomb guards.</summary>
    public static Jbig2DecodeResult Decode(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte>? globals, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
        Decode(data, globals, options.MaxJbig2Segments, options.MaxJbig2Symbols, options.MaxImagePixels, options, diagnostics, subject);

    public static Jbig2DecodeResult Decode(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte>? globals, int maxSegments, int maxSymbols, long maxDecodedPixels, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject) =>
        Decode(data, globals, maxSegments, maxSymbols, maxDecodedPixels, options, diagnostics, subject, disableGenericFastPath: false);

    /// <summary><paramref name="disableGenericFastPath"/> forces the general template walk in <see cref="DecodeGenericBitmapCore"/> — test-only, for the differential regression test that pins the sliding-window fast path byte-for-byte to the general loop.</summary>
    internal static Jbig2DecodeResult Decode(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte>? globals, int maxSegments, int maxSymbols, long maxDecodedPixels, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject, bool disableGenericFastPath)
    {
        var state = new PageState();
        var symbolDictionaries = new Dictionary<uint, List<Jbig2Symbol>>();
        var totalSymbols = 0;

        if (globals is { } globalBytes)
        {
            var globalSegments = Jbig2SegmentReader.ParseSegments(globalBytes.Span, maxSegments, out var globalsStoppedAt);
            ReportAbandonedWalk("the /JBIG2Globals stream", globalBytes.Length, globalsStoppedAt, globalSegments.Count, maxSegments, state, options, diagnostics, subject);
            ProcessSegments(globalBytes.Span, globalSegments, state, symbolDictionaries, ref totalSymbols, maxSymbols, maxDecodedPixels, options, diagnostics, subject, disableGenericFastPath);
        }

        var segments = Jbig2SegmentReader.ParseSegments(data.Span, maxSegments, out var stoppedAt);
        if (segments.Count == 0)
        {
            throw new PlumePdfException("PLUME3501", "JBIG2: no segment headers could be parsed - nothing to decode.");
        }

        ReportAbandonedWalk("the embedded stream", data.Length, stoppedAt, segments.Count, maxSegments, state, options, diagnostics, subject);

        ProcessSegments(data.Span, segments, state, symbolDictionaries, ref totalSymbols, maxSymbols, maxDecodedPixels, options, diagnostics, subject, disableGenericFastPath);

        if (state.Bitmap is null)
        {
            throw new PlumePdfException("PLUME3501", "JBIG2: no region segment produced any page content.");
        }

        // When every content region was skipped as unsupported (Huffman-coded
        // symbol dictionaries/text regions, halftone regions, TPGRON refinement, or a failed
        // segment), the page bitmap holds only the page-info default pixel - rendering it
        // would paint a SOLID sheet (black for default-pixel-1 pages, or black via a
        // /Decode [1 0] on default-0 pages) over content every conformant viewer shows.
        // Painting nothing plus the coded diagnostics already recorded above is the honest
        // degradation for an undecodable image; a page-info-only stream
        // with NO skipped segments (a legitimately blank page) still returns its background.
        // "Nothing was painted" is judged on pixels, not on whether a region object was composed:
        // a mis-picked terminator or a zero-row region composes an empty region and would
        // otherwise ship a blank page behind a mere Warning.
        if (state.SkippedUnsupportedSegment && (!state.ComposedAnyRegion || PageIsBlank(state)))
        {
            throw new PlumePdfException("PLUME3501", "JBIG2: every content segment was skipped as unsupported (see the preceding diagnostics); refusing to substitute the bare page background for the document's content.");
        }

        return new Jbig2DecodeResult(state.Bitmap, state.Width, state.Height);
    }

    /// <summary>Reports a segment-header walk that stopped short of the stream's end: on a header-sized remainder it could not parse (a scanned-document producer family hit exactly this and rendered blank with no diagnostic), or on the <see cref="PdfOptions"/> segment cap. A remainder shorter than a segment header is trailing padding and is ignored, as PDFium does (defensive — no fetched pdf.js bitmap-* fixture actually carries one).</summary>
    private static void ReportAbandonedWalk(string what, int length, int stoppedAt, int segmentCount, int maxSegments, PageState state, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var remaining = length - stoppedAt;
        if (remaining < Jbig2SegmentReader.MinimumHeaderSize)
        {
            return;
        }

        state.SkippedUnsupportedSegment = true;
        var reason = segmentCount >= maxSegments
            ? $"the segment count reached the {maxSegments}-segment cap"
            : $"the segment header at offset {stoppedAt} could not be parsed";
        FilterDiagnostics.ReportDeviation("PLUME3552", $"JBIG2: {reason}; the remaining {remaining} byte(s) of {what} were not decoded.", options, diagnostics, subject);
    }

    /// <summary>Whether every page pixel still holds the page's default value — i.e. no region contributed a single pixel.</summary>
    private static bool PageIsBlank(PageState state)
    {
        if (state.Bitmap is not { } bitmap)
        {
            return true;
        }

        var height = bitmap.GetLength(0);
        var width = bitmap.GetLength(1);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (bitmap[y, x] != state.DefaultPixel)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private sealed class PageState
    {
        public bool[,]? Bitmap;
        public int Width;
        public int Height;
        public bool DefaultPixel;
        public bool HeightKnown;
        public bool ComposedAnyRegion;
        public bool SkippedUnsupportedSegment;
    }

    private static void ProcessSegments(ReadOnlySpan<byte> buffer, List<Jbig2Segment> segments, PageState state, Dictionary<uint, List<Jbig2Symbol>> symbolDictionaries, ref int totalSymbols, int maxSymbols, long maxDecodedPixels, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject, bool disableGenericFastPath = false)
    {
        foreach (var segment in segments)
        {
            var segData = buffer.Slice(segment.DataStart, segment.DataLength);
            try
            {
                switch (segment.Type)
                {
                    case Jbig2SegmentType.PageInfo:
                        ProcessPageInfo(segData, state, maxDecodedPixels);
                        break;

                    case Jbig2SegmentType.SymbolDictionary:
                        {
                            var input = CollectInputSymbols(segment, symbolDictionaries);
                            var exported = DecodeSymbolDictionary(segData, input, maxSymbols - totalSymbols, maxDecodedPixels, options, diagnostics, subject);
                            if (exported is null)
                            {
                                state.SkippedUnsupportedSegment = true;
                            }

                            if (exported is not null)
                            {
                                symbolDictionaries[segment.Number] = exported;
                                totalSymbols += exported.Count;
                                if (totalSymbols > maxSymbols)
                                {
                                    throw new PlumePdfException("PLUME3502", $"JBIG2: total symbol count exceeds the {maxSymbols}-symbol cap - refusing to continue (possible decompression bomb).");
                                }
                            }

                            break;
                        }

                    case Jbig2SegmentType.IntermediateTextRegion:
                    case Jbig2SegmentType.ImmediateTextRegion:
                    case Jbig2SegmentType.ImmediateLosslessTextRegion:
                        {
                            var input = CollectInputSymbols(segment, symbolDictionaries);
                            var region = DecodeTextRegion(segData, input, maxDecodedPixels, options, diagnostics, subject);
                            if (region is { } r)
                            {
                                ComposeRegion(state, r.Bitmap, r.Info, maxDecodedPixels);
                            }
                            else
                            {
                                state.SkippedUnsupportedSegment = true;
                            }

                            break;
                        }

                    case Jbig2SegmentType.IntermediateGenericRegion:
                    case Jbig2SegmentType.ImmediateGenericRegion:
                    case Jbig2SegmentType.ImmediateLosslessGenericRegion:
                        {
                            var region = DecodeGenericRegionSegment(segData, maxDecodedPixels, options, diagnostics, subject, disableGenericFastPath, segment.UnknownLengthRowCount);
                            if (region is { } r)
                            {
                                ComposeRegion(state, r.Bitmap, r.Info, maxDecodedPixels);
                            }
                            else
                            {
                                state.SkippedUnsupportedSegment = true;
                            }

                            break;
                        }

                    case Jbig2SegmentType.IntermediateGenericRefinementRegion:
                    case Jbig2SegmentType.ImmediateGenericRefinementRegion:
                    case Jbig2SegmentType.ImmediateLosslessGenericRefinementRegion:
                        {
                            var region = DecodeGenericRefinementRegionSegment(segData, state, maxDecodedPixels, options, diagnostics, subject);
                            if (region is { } r)
                            {
                                ComposeRegion(state, r.Bitmap, r.Info, maxDecodedPixels);
                            }
                            else
                            {
                                state.SkippedUnsupportedSegment = true;
                            }

                            break;
                        }

                    case Jbig2SegmentType.PatternDictionary:
                    case Jbig2SegmentType.IntermediateHalftoneRegion:
                    case Jbig2SegmentType.ImmediateHalftoneRegion:
                    case Jbig2SegmentType.ImmediateLosslessHalftoneRegion:
                        state.SkippedUnsupportedSegment = true;
                        FilterDiagnostics.ReportDeviation("PLUME3550", $"JBIG2: segment {segment.Number} is a halftone/pattern-dictionary segment (type {segment.Type}) - halftone regions are not supported; skipping it.", options, diagnostics, subject);
                        break;

                    case Jbig2SegmentType.EndOfPage:
                    case Jbig2SegmentType.EndOfStripe:
                    case Jbig2SegmentType.EndOfFile:
                    case Jbig2SegmentType.Profiles:
                    case Jbig2SegmentType.Tables:
                    case Jbig2SegmentType.Extension:
                        break; // Structural/no-op segment types - nothing to decode.

                    default:
                        FilterDiagnostics.ReportDeviation("PLUME3551", $"JBIG2: segment {segment.Number} has an unrecognized type ({segment.Type}); skipping it (per-segment fallback).", options, diagnostics, subject);
                        break;
                }
            }
            catch (PlumePdfException ex) when (ex.Code is not "PLUME3502")
            {
                state.SkippedUnsupportedSegment = true;
                FilterDiagnostics.ReportDeviation("PLUME3552", $"JBIG2: segment {segment.Number} (type {segment.Type}) failed to decode ({ex.Message}); skipping it.", options, diagnostics, subject);
            }
        }
    }

    /// <summary>
    /// A bomb guard: refuses a document-supplied bitmap size <b>before</b> any
    /// pixel-buffer allocation. Every <c>new bool[height, width]</c> in this decoder is preceded
    /// by this check so a hostile segment header can't force an out-of-cap allocation (let
    /// alone an uncatchable <see cref="OutOfMemoryException"/>) - the same up-front cap shape
    /// <see cref="CcittFaxEngine"/>'s <c>Decode</c> applies via PLUME3405.
    /// </summary>
    private static void EnsurePixelBudget(int width, int height, long maxDecodedPixels)
    {
        if ((long)width * height > maxDecodedPixels)
        {
            throw new PlumePdfException("PLUME3503", $"JBIG2: a {width}x{height} bitmap would exceed the {maxDecodedPixels}-pixel decode cap - refusing to allocate it (possible decompression bomb).");
        }
    }

    private static void ProcessPageInfo(ReadOnlySpan<byte> data, PageState state, long maxDecodedPixels)
    {
        if (data.Length < 19)
        {
            return;
        }

        var width = (int)ReadUInt32(data, 0);
        var height = (int)ReadUInt32(data, 4);
        var flags = data[16];
        state.DefaultPixel = (flags & 0x04) != 0;

        if (width > 0 && height > 0 && height != unchecked((int)0xFFFFFFFF))
        {
            EnsurePixelBudget(width, height, maxDecodedPixels);
            state.Width = width;
            state.Height = height;
            state.HeightKnown = true;
            state.Bitmap = new bool[height, width];
            if (state.DefaultPixel)
            {
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        state.Bitmap[y, x] = true;
                    }
                }
            }
        }
        else if (width > 0)
        {
            state.Width = width; // Height unknown - resolved incrementally as regions land (ComposeRegion grows the bitmap).
        }
    }

    private static List<Jbig2Symbol> CollectInputSymbols(Jbig2Segment segment, Dictionary<uint, List<Jbig2Symbol>> symbolDictionaries)
    {
        var result = new List<Jbig2Symbol>();
        foreach (var referred in segment.ReferredTo)
        {
            if (symbolDictionaries.TryGetValue(referred, out var syms))
            {
                result.AddRange(syms);
            }
        }

        return result;
    }

    // --- Region composition -----------------------------------------------------------------

    private readonly record struct DecodedRegion(bool[,] Bitmap, Jbig2RegionInfo Info);

    private static void ComposeRegion(PageState state, bool[,] region, Jbig2RegionInfo info, long maxDecodedPixels)
    {
        state.ComposedAnyRegion = true;
        var regionHeight = region.GetLength(0);
        var regionWidth = region.GetLength(1);

        // Long math: info.X/info.Y are document-supplied and can be near int.MaxValue, so
        // int addition could wrap negative and slip past both the cap and the array bounds.
        var neededHeightLong = (long)info.Y + regionHeight;
        var neededWidthLong = Math.Max(state.Width, (long)info.X + regionWidth);

        if (neededWidthLong > int.MaxValue || neededHeightLong > int.MaxValue ||
            neededWidthLong * Math.Max(neededHeightLong, state.Height) > maxDecodedPixels)
        {
            throw new PlumePdfException("PLUME3503", $"JBIG2: page bitmap would exceed the {maxDecodedPixels}-pixel decode cap - refusing to continue (possible decompression bomb).");
        }

        var neededHeight = (int)neededHeightLong;
        var neededWidth = (int)neededWidthLong;

        if (state.Bitmap is null || neededHeight > state.Height || neededWidth > state.Width)
        {
            var grown = new bool[Math.Max(neededHeight, state.Height), neededWidth];
            if (state.Bitmap is { } existing)
            {
                for (var y = 0; y < state.Height; y++)
                {
                    for (var x = 0; x < state.Width; x++)
                    {
                        grown[y, x] = existing[y, x];
                    }
                }
            }

            state.Bitmap = grown;
            state.Width = neededWidth;
            state.Height = Math.Max(neededHeight, state.Height);
        }

        for (var y = 0; y < regionHeight; y++)
        {
            var py = info.Y + y;
            for (var x = 0; x < regionWidth; x++)
            {
                var px = info.X + x;
                var src = region[y, x];
                state.Bitmap[py, px] = info.CombinationOperator switch
                {
                    0 => state.Bitmap[py, px] || src, // OR
                    1 => state.Bitmap[py, px] && src, // AND
                    2 => state.Bitmap[py, px] ^ src, // XOR
                    3 => !(state.Bitmap[py, px] ^ src), // XNOR
                    _ => src, // 4 = REPLACE
                };
            }
        }
    }

    // --- Generic region ----------------------------------------------------------------------

    private static DecodedRegion? DecodeGenericRegionSegment(ReadOnlySpan<byte> data, long maxDecodedPixels, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject, bool disableGenericFastPath = false, int? unknownLengthRowCount = null)
    {
        if (data.Length < Jbig2RegionInfo.ByteSize + 1)
        {
            return null;
        }

        var info = Jbig2RegionInfo.Parse(data, 0);
        if (unknownLengthRowCount is { } rows)
        {
            // T.88 §7.4.6.4: the row count after the terminator is "the actual number of rows
            // contained in this segment", never more than the declared height (the segment reader
            // already rejected larger candidates); a smaller one means the encoder stopped early -
            // decode only those rows (jbig2dec does the same). A region-info height of 0xFFFFFFFF
            // is NOT a T.88 shape (only the PAGE height may be unknown, §7.4.8) and PDFium/pdf.js
            // refuse it; PlumePDF tolerantly takes the row count as the height instead.
            if (info.Height == -1 || (rows > 0 && rows < info.Height))
            {
                info = info with { Height = rows };
            }
        }
        var flags = data[Jbig2RegionInfo.ByteSize];
        var mmr = (flags & 0x01) != 0;
        var templateIndex = (flags >> 1) & 0x03;
        var tpgdon = (flags & 0x08) != 0;
        var pos = Jbig2RegionInfo.ByteSize + 1;

        (int X, int Y)[] at = [];
        if (!mmr)
        {
            var atCount = templateIndex == 0 ? 4 : 1;
            if (pos + (atCount * 2) > data.Length)
            {
                return null;
            }

            at = new (int, int)[atCount];
            for (var i = 0; i < atCount; i++)
            {
                at[i] = (unchecked((sbyte)data[pos]), unchecked((sbyte)data[pos + 1]));
                pos += 2;
            }
        }

        if (info.Width <= 0 || info.Height <= 0 || info.X < 0 || info.Y < 0)
        {
            FilterDiagnostics.ReportDeviation("PLUME3552", $"JBIG2: generic region declares an invalid geometry ({info.Width}x{info.Height} at {info.X},{info.Y}); skipping it.", options, diagnostics, subject);
            return null;
        }

        bool[,] bitmap;
        if (mmr)
        {
            var remaining = data[pos..].ToArray();
            var bitPosition = 0;
            bitmap = CcittFaxEngine.DecodeMmrBitmap(remaining, ref bitPosition, info.Width, info.Height, maxDecodedPixels);
        }
        else
        {
            var decoder = new Jbig2ArithmeticDecoder(data.ToArray(), pos, data.Length);
            var contexts = new byte[1 << 16];
            bitmap = DecodeGenericBitmap(info.Width, info.Height, templateIndex, at, tpgdon, decoder, contexts, maxDecodedPixels, disableGenericFastPath);
        }

        return new DecodedRegion(bitmap, info);
    }

    private static bool[,] DecodeGenericBitmap(int width, int height, int templateIndex, (int X, int Y)[] at, bool tpgdon, Jbig2ArithmeticDecoder decoder, byte[] contexts, long maxDecodedPixels) =>
        DecodeGenericBitmap(width, height, templateIndex, at, tpgdon, decoder, contexts, maxDecodedPixels, disableFastPath: false);

    /// <summary>
    /// Generic-region decode (T.88 6.2.5) — a performance pass found this loop was 71%
    /// exclusive CPU of an MRC-scan render. Decodes into flat 0/1 byte rows (a
    /// <c>bool[height, width]</c> paid a bounds check + dimension multiply per template read,
    /// ~16 of them per pixel), with a sliding-window fast path for the overwhelmingly common
    /// template 0 + nominal-AT shape (jbig2enc and mainstream scanner encoders), and converts
    /// once at the end via <see cref="Buffer.BlockCopy"/> (bool and byte are both one byte, and
    /// every value is strictly 0/1). <paramref name="disableFastPath"/> exists for the
    /// differential regression test, which pins the fast path byte-for-byte to the general
    /// loop on the same stream.
    /// </summary>
    internal static bool[,] DecodeGenericBitmapCore(int width, int height, int templateIndex, (int X, int Y)[] at, bool tpgdon, Jbig2ArithmeticDecoder decoder, byte[] contexts, long maxDecodedPixels, bool disableFastPath) =>
        DecodeGenericBitmap(width, height, templateIndex, at, tpgdon, decoder, contexts, maxDecodedPixels, disableFastPath);

    private static bool[,] DecodeGenericBitmap(int width, int height, int templateIndex, (int X, int Y)[] at, bool tpgdon, Jbig2ArithmeticDecoder decoder, byte[] contexts, long maxDecodedPixels, bool disableFastPath)
    {
        EnsurePixelBudget(width, height, maxDecodedPixels);

        // Decode directly into the bool[,]'s own memory, viewed as flat 0/1 byte rows: a 2-D
        // array's cells are CLR-guaranteed contiguous and bool is exactly one byte, so this is
        // the same storage without the per-read bounds-check + dimension-multiply the bool[,]
        // indexer pays (and without a separate flat buffer + copy). Only 0/1 is ever written,
        // so every cell remains a valid bool; a fresh array reads as all-zero, which is the
        // T.88 value for not-yet-decoded pixels (reachable via forward-pointing AT offsets).
        var total = checked(width * height);
        var bitmap = new bool[height, width];
        var flat = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref System.Runtime.CompilerServices.Unsafe.As<bool, byte>(ref bitmap[0, 0]), total);

        if (!disableFastPath && templateIndex == 0 && IsNominalTemplate0At(at))
        {
            DecodeGenericTemplate0Nominal(width, height, tpgdon, decoder, contexts, flat);
        }
        else
        {
            DecodeGenericAnyTemplate(width, height, templateIndex, at, tpgdon, decoder, contexts, flat);
        }

        return bitmap;
    }

    /// <summary>The T.88 6.2.5.3 nominal AT positions for template 0 — the values every mainstream encoder writes, and the precondition for the sliding-window fast path's fixed window layout.</summary>
    private static bool IsNominalTemplate0At((int X, int Y)[] at) =>
        at.Length == 4 && at[0] == (3, -1) && at[1] == (-3, -1) && at[2] == (2, -2) && at[3] == (-2, -2);

    /// <summary>
    /// Template 0 with nominal ATs, decoded with sliding context windows. The sorted template
    /// (the general loop sorts by row then column) merges into three CONSECUTIVE column runs —
    /// row y−2 spans x−2..x+2 (5 bits, AT4/AT3 at the ends), row y−1 spans x−3..x+3 (7 bits,
    /// AT2/AT1 at the ends), row y spans x−4..x−1 (4 bits) — so the general loop's MSB-first
    /// context label is exactly <c>(w2 &lt;&lt; 11) | (w1 &lt;&lt; 4) | w0</c>, and stepping x
    /// shifts each window left and admits one new column bit. Identical label sequence in,
    /// identical arithmetic-decoder bit stream out: byte-identical to the general loop by
    /// construction (and pinned by the differential test).
    /// </summary>
    private static void DecodeGenericTemplate0Nominal(int width, int height, bool tpgdon, Jbig2ArithmeticDecoder decoder, byte[] contexts, Span<byte> flat)
    {
        var ltp = false;
        var sltpContext = ReusedContexts[0];

        for (var y = 0; y < height; y++)
        {
            if (tpgdon)
            {
                var sltp = decoder.ReadBit(contexts, sltpContext) == 1;
                ltp ^= sltp;
                if (ltp)
                {
                    if (y > 0)
                    {
                        flat.Slice((y - 1) * width, width).CopyTo(flat.Slice(y * width, width));
                    }

                    continue;
                }
            }

            var row0 = y * width;
            var rowM1 = row0 - width;
            var rowM2 = row0 - (2 * width);
            var hasM1 = y >= 1;
            var hasM2 = y >= 2;

            // Seed the windows for x = 0 (columns left of the bitmap read 0, T.88 6.2.5.2).
            var w2 = 0;
            var w1 = 0;
            var w0 = 0;
            for (var col = -2; col <= 2; col++)
            {
                w2 = (w2 << 1) | (hasM2 && col >= 0 && col < width ? flat[rowM2 + col] : 0);
            }

            for (var col = -3; col <= 3; col++)
            {
                w1 = (w1 << 1) | (hasM1 && col >= 0 && col < width ? flat[rowM1 + col] : 0);
            }

            for (var x = 0; x < width; x++)
            {
                var bit = decoder.ReadBit(contexts, (w2 << 11) | (w1 << 4) | w0);
                flat[row0 + x] = (byte)bit;

                // Slide to x+1: each window shifts left, drops its MSB, and admits the next
                // column (out-of-bitmap columns read 0; w0 admits the pixel just decoded).
                w0 = ((w0 << 1) | bit) & 0xF;
                var c2 = x + 3;
                w2 = ((w2 << 1) | (hasM2 && c2 < width ? flat[rowM2 + c2] : 0)) & 0x1F;
                var c1 = x + 4;
                w1 = ((w1 << 1) | (hasM1 && c1 < width ? flat[rowM1 + c1] : 0)) & 0x7F;
            }
        }
    }

    /// <summary>The general path — any template, any AT positions. The same per-pixel template walk as always, over flat rows instead of a <c>bool[,]</c>.</summary>
    private static void DecodeGenericAnyTemplate(int width, int height, int templateIndex, (int X, int Y)[] at, bool tpgdon, Jbig2ArithmeticDecoder decoder, byte[] contexts, Span<byte> flat)
    {
        var template = new List<(int X, int Y)>(CodingTemplates[templateIndex]);
        template.AddRange(at);
        template.Sort(static (a, b) => a.Y != b.Y ? a.Y - b.Y : a.X - b.X);
        var templateArray = template.ToArray();
        var templateLength = templateArray.Length;

        var ltp = false;
        var sltpContext = ReusedContexts[templateIndex];

        for (var y = 0; y < height; y++)
        {
            if (tpgdon)
            {
                var sltp = decoder.ReadBit(contexts, sltpContext) == 1;
                ltp ^= sltp;
                if (ltp)
                {
                    if (y > 0)
                    {
                        flat.Slice((y - 1) * width, width).CopyTo(flat.Slice(y * width, width));
                    }

                    continue;
                }
            }

            for (var x = 0; x < width; x++)
            {
                var contextLabel = 0;
                for (var k = 0; k < templateLength; k++)
                {
                    var j0 = x + templateArray[k].X;
                    var i0 = y + templateArray[k].Y;

                    // AT pixels are signed bytes, so i0/j0 can point below OR above/right of
                    // the bitmap - a pixel outside the bitmap reads as 0 (T.88 6.2.5.2's
                    // defined out-of-bitmap behavior), so both bounds must be checked.
                    var bit = j0 >= 0 && j0 < width && i0 >= 0 && i0 < height ? flat[(i0 * width) + j0] : 0;
                    contextLabel = (contextLabel << 1) | bit;
                }

                flat[(y * width) + x] = (byte)decoder.ReadBit(contexts, contextLabel);
            }
        }
    }

    // --- Generic refinement region -----------------------------------------------------------

    private static DecodedRegion? DecodeGenericRefinementRegionSegment(ReadOnlySpan<byte> data, PageState state, long maxDecodedPixels, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        if (data.Length < Jbig2RegionInfo.ByteSize + 1)
        {
            return null;
        }

        var info = Jbig2RegionInfo.Parse(data, 0);
        var flags = data[Jbig2RegionInfo.ByteSize];
        var templateIndex = flags & 0x01;
        var tpgron = (flags & 0x02) != 0;
        var pos = Jbig2RegionInfo.ByteSize + 1;

        (int X, int Y) at0 = default, at1 = default;
        if (templateIndex == 0)
        {
            if (pos + 4 > data.Length)
            {
                return null;
            }

            at0 = (unchecked((sbyte)data[pos]), unchecked((sbyte)data[pos + 1]));
            at1 = (unchecked((sbyte)data[pos + 2]), unchecked((sbyte)data[pos + 3]));
            pos += 4;
        }

        if (tpgron)
        {
            FilterDiagnostics.ReportDeviation("PLUME3553", "JBIG2: generic refinement region uses TPGRON typical prediction, which is not supported; skipping it.", options, diagnostics, subject);
            return null;
        }

        if (info.Width <= 0 || info.Height <= 0 || info.X < 0 || info.Y < 0 || state.Bitmap is null)
        {
            return null;
        }

        EnsurePixelBudget(info.Width, info.Height, maxDecodedPixels);

        // The reference bitmap is the page's own current content at the region's placement
        // (T.88 6.3: refining "in place" against what's already composed there) - the common
        // real-world case (progressive refinement of a previously-decoded region). The copy
        // bounds use long math: info.X/info.Y are document-supplied and could wrap an int sum.
        var reference = new bool[info.Height, info.Width];
        for (var y = 0; y < info.Height && (long)info.Y + y < state.Height; y++)
        {
            for (var x = 0; x < info.Width && (long)info.X + x < state.Width; x++)
            {
                reference[y, x] = state.Bitmap[info.Y + y, info.X + x];
            }
        }

        var decoder = new Jbig2ArithmeticDecoder(data.ToArray(), pos, data.Length);
        var contexts = new byte[1 << 13];
        var bitmap = DecodeRefinementBitmap(info.Width, info.Height, templateIndex, at0, at1, reference, 0, 0, decoder, contexts, maxDecodedPixels);
        return new DecodedRegion(bitmap, info with { CombinationOperator = 4 }); // REPLACE: refinement supersedes what's there.
    }

    private static bool[,] DecodeRefinementBitmap(int width, int height, int templateIndex, (int X, int Y) at0, (int X, int Y) at1, bool[,] reference, int referenceDx, int referenceDy, Jbig2ArithmeticDecoder decoder, byte[] contexts, long maxDecodedPixels)
    {
        EnsurePixelBudget(width, height, maxDecodedPixels);

        var codingTemplate = new List<(int X, int Y)>(RefinementCodingTemplates[templateIndex]);
        var referenceTemplate = new List<(int X, int Y)>(RefinementReferenceTemplates[templateIndex]);
        if (templateIndex == 0)
        {
            codingTemplate.Add(at0);
            referenceTemplate.Add(at1);
        }

        var refHeight = reference.GetLength(0);
        var refWidth = reference.GetLength(1);
        var bitmap = new bool[height, width];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var contextLabel = 0;
                foreach (var (dx, dy) in codingTemplate)
                {
                    var j0 = x + dx;
                    var i0 = y + dy;

                    // AT0 is a signed byte, so i0 can exceed height-1 as well as go negative;
                    // out-of-bitmap coding pixels read as 0 (same shape as DecodeGenericBitmap).
                    var bit = j0 >= 0 && j0 < width && i0 >= 0 && i0 < height && bitmap[i0, j0] ? 1 : 0;
                    contextLabel = (contextLabel << 1) | bit;
                }

                foreach (var (dx, dy) in referenceTemplate)
                {
                    var j0 = x - referenceDx + dx;
                    var i0 = y - referenceDy + dy;
                    var bit = j0 >= 0 && j0 < refWidth && i0 >= 0 && i0 < refHeight && reference[i0, j0] ? 1 : 0;
                    contextLabel = (contextLabel << 1) | bit;
                }

                bitmap[y, x] = decoder.ReadBit(contexts, contextLabel) == 1;
            }
        }

        return bitmap;
    }

    // --- Symbol dictionary ---------------------------------------------------------------------

    private static List<Jbig2Symbol>? DecodeSymbolDictionary(ReadOnlySpan<byte> data, List<Jbig2Symbol> inputSymbols, int remainingSymbolBudget, long maxDecodedPixels, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        if (data.Length < 2)
        {
            return null;
        }

        var flags = ReadUInt16(data, 0);
        var huffman = (flags & 0x01) != 0;
        var refinement = (flags & 0x02) != 0;
        var template = (flags >> 10) & 0x03;
        var refinementTemplate = (flags >> 12) & 0x01;
        var pos = 2;

        if (huffman)
        {
            FilterDiagnostics.ReportDeviation("PLUME3554", "JBIG2: Huffman-coded symbol dictionary is not supported; skipping it.", options, diagnostics, subject);
            return null;
        }

        (int X, int Y)[] at = [];
        var atCount = template == 0 ? 4 : 1;
        if (pos + (atCount * 2) > data.Length)
        {
            return null;
        }

        at = new (int, int)[atCount];
        for (var i = 0; i < atCount; i++)
        {
            at[i] = (unchecked((sbyte)data[pos]), unchecked((sbyte)data[pos + 1]));
            pos += 2;
        }

        (int X, int Y) rAt0 = default, rAt1 = default;
        if (refinement && refinementTemplate == 0)
        {
            if (pos + 4 > data.Length)
            {
                return null;
            }

            rAt0 = (unchecked((sbyte)data[pos]), unchecked((sbyte)data[pos + 1]));
            rAt1 = (unchecked((sbyte)data[pos + 2]), unchecked((sbyte)data[pos + 3]));
            pos += 4;
        }

        if (pos + 8 > data.Length)
        {
            return null;
        }

        var numberOfExportedSymbols = (int)ReadUInt32(data, pos);
        var numberOfNewSymbols = (int)ReadUInt32(data, pos + 4);
        pos += 8;

        if (numberOfNewSymbols < 0 || numberOfNewSymbols > remainingSymbolBudget)
        {
            throw new PlumePdfException("PLUME3502", $"JBIG2: symbol dictionary declares {numberOfNewSymbols} new symbols, exceeding the remaining symbol budget - refusing to continue (possible decompression bomb).");
        }

        var decoder = new Jbig2ArithmeticDecoder(data.ToArray(), pos, data.Length);
        var intContexts = new Jbig2IntegerContexts();
        var genericContexts = new byte[1 << 16];
        var refinementContexts = new byte[1 << 13];

        var newSymbols = new List<Jbig2Symbol>(Math.Min(numberOfNewSymbols, 4096));
        var currentHeight = 0;
        var symbolCodeLength = SymbolCodeLength(inputSymbols.Count + numberOfNewSymbols);

        while (newSymbols.Count < numberOfNewSymbols)
        {
            var deltaHeight = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IADH"));
            if (deltaHeight is null)
            {
                break;
            }

            currentHeight += deltaHeight.Value;
            if (currentHeight <= 0 || currentHeight > 1 << 20)
            {
                break;
            }

            var currentWidth = 0;
            while (true)
            {
                var deltaWidth = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IADW"));
                if (deltaWidth is null)
                {
                    break; // OOB: end of this height class.
                }

                currentWidth += deltaWidth.Value;
                if (currentWidth <= 0 || currentWidth > 1 << 20 || newSymbols.Count >= numberOfNewSymbols)
                {
                    break;
                }

                if (!refinement)
                {
                    var bitmap = DecodeGenericBitmap(currentWidth, currentHeight, template, at, tpgdon: false, decoder, genericContexts, maxDecodedPixels);
                    newSymbols.Add(new Jbig2Symbol(bitmap));
                }
                else
                {
                    // Refinement/aggregate-coded new symbols (SDREFAGG=1): a symbol is either
                    // a direct refinement of exactly one existing symbol (aggregate instance
                    // count 1) or a small aggregated text region (count > 1). The
                    // single-refinement case is implemented (the common real-world one);
                    // aggregated (>1) is a per-segment fallback.
                    var allSymbols = new List<Jbig2Symbol>(inputSymbols);
                    allSymbols.AddRange(newSymbols);
                    var aggregateCount = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IAAI"));
                    if (aggregateCount != 1)
                    {
                        FilterDiagnostics.ReportDeviation("PLUME3555", "JBIG2: symbol dictionary uses aggregate refinement coding (SDREFAGG with >1 instance) - not supported; stopping this dictionary early.", options, diagnostics, subject);
                        return newSymbols.Count > 0 ? ExportSymbols(decoder, intContexts, inputSymbols, newSymbols, options, diagnostics, subject) : null;
                    }

                    // Root cause of a past decode bug: the IAID code length here is SBSYMCODELEN =
                    // ceil(log2(SDNUMINSYMS + SDNUMNEWSYMS)) - the DECLARED totals (T.88
                    // 6.5.8.2.3; pdf.js v5.6.205 decodeSymbolDictionary uses its once-computed
                    // symbolCodeLength) - never the running count. Reading ceil(log2(running))
                    // bits consumes fewer bits than the encoder wrote whenever the running
                    // count trails the total, silently desyncing the arithmetic stream: every
                    // later height/width/bitmap decode reads garbage, producing dense black
                    // symbols with no diagnostics (or, when the garbage trips the Int32 guard,
                    // the all-segments-skipped refusal). jbig2enc never emits SDREFAGG
                    // dictionaries, which is why no synthetic ever exercised this path -
                    // ABBYY/LuraTech-class scanner encoders do.
                    var symbolId = Jbig2ArithmeticInteger.DecodeIaid(decoder, intContexts.GetIaid(symbolCodeLength), symbolCodeLength);
                    var rdx = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IARDX")) ?? 0;
                    var rdy = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IARDY")) ?? 0;
                    if (symbolId < 0 || symbolId >= allSymbols.Count)
                    {
                        FilterDiagnostics.ReportDeviation("PLUME3555", "JBIG2: symbol dictionary refinement referenced an out-of-range symbol ID; stopping this dictionary early.", options, diagnostics, subject);
                        return newSymbols.Count > 0 ? ExportSymbols(decoder, intContexts, inputSymbols, newSymbols, options, diagnostics, subject) : null;
                    }

                    var reference = allSymbols[symbolId].Bitmap;
                    var bitmap = DecodeRefinementBitmap(currentWidth, currentHeight, refinementTemplate, rAt0, rAt1, reference, rdx, rdy, decoder, refinementContexts, maxDecodedPixels);
                    newSymbols.Add(new Jbig2Symbol(bitmap));
                }
            }
        }

        return ExportSymbols(decoder, intContexts, inputSymbols, newSymbols, options, diagnostics, subject);
    }

    private static List<Jbig2Symbol> ExportSymbols(Jbig2ArithmeticDecoder decoder, Jbig2IntegerContexts intContexts, List<Jbig2Symbol> inputSymbols, List<Jbig2Symbol> newSymbols, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        var pool = new List<Jbig2Symbol>(inputSymbols);
        pool.AddRange(newSymbols);

        var exported = new List<Jbig2Symbol>();
        var currentFlag = false;
        var index = 0;
        var iaexContext = intContexts.Get("IAEX");
        var guard = 0;

        while (index < pool.Count && guard++ < pool.Count + 16)
        {
            var runLength = Jbig2ArithmeticInteger.DecodeInteger(decoder, iaexContext);
            if (runLength is null || runLength.Value < 0)
            {
                break;
            }

            for (var i = 0; i < runLength.Value && index < pool.Count; i++, index++)
            {
                if (currentFlag)
                {
                    exported.Add(pool[index]);
                }
            }

            currentFlag = !currentFlag;
        }

        if (exported.Count == 0 && newSymbols.Count > 0)
        {
            // A dictionary that produced symbols but whose export run-lengths never marked
            // any as exported is almost certainly a decode desync - exporting everything
            // newly decoded is the LZW-precedent "decode as far as possible, return
            // something usable" fallback rather than silently exporting nothing.
            FilterDiagnostics.ReportDeviation("PLUME3556", "JBIG2: symbol dictionary's export flags selected zero symbols despite decoding some; exporting all newly-decoded symbols instead.", options, diagnostics, subject);
            return newSymbols;
        }

        return exported;
    }

    // ITU-T T.88 §7.4.3.1.7 (SBSYMCODELEN) / §6.5.8.2.3's SDNEWSYMWIDTHS aggregate-coding
    // analogue: ceil(log2(symbolCount)), which is 0 - not 1 - when symbolCount <= 1 (a single
    // available symbol needs zero bits to select: Jbig2ArithmeticInteger.DecodeIaid with
    // codeLength 0 already returns 0 without reading the decoder at all). Verified against
    // pdf.js's own log2 helper (core_utils.js, pinned v5.6.205): `x > 0 ? Math.ceil(Math.log2(x))
    // : 0` — no minimum-1 floor for the arithmetic-coded case (pdf.js applies Math.max(_, 1)
    // only inside decodeSymbolDictionary's `if (huffman)` branch, which this decoder never
    // reaches: Huffman symbol dictionaries degrade via PLUME3554 before this helper runs).
    // A wrongly-forced minimum of 1 here reads one extra, never-encoded arithmetic-coded bit
    // for every single-symbol IAID decode, permanently desyncing every text-region symbol
    // placement that follows - the root cause of a real-world blank-page regression
    // (bitmap-symbol-big-segmentid.pdf: SSIM 0.87 vs the 0.90 floor, both of its single-symbol
    // text regions decoding zero placed instances).
    private static int SymbolCodeLength(int symbolCount) =>
        symbolCount > 0 ? (int)Math.Ceiling(Math.Log2(symbolCount)) : 0;

    // --- Text region ---------------------------------------------------------------------------

    private static DecodedRegion? DecodeTextRegion(ReadOnlySpan<byte> data, List<Jbig2Symbol> symbols, long maxDecodedPixels, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference? subject)
    {
        if (data.Length < Jbig2RegionInfo.ByteSize + 2)
        {
            return null;
        }

        var info = Jbig2RegionInfo.Parse(data, 0);
        var flags = ReadUInt16(data, Jbig2RegionInfo.ByteSize);
        var huffman = (flags & 0x01) != 0;
        var refine = (flags & 0x02) != 0;
        var logStripSize = (flags >> 2) & 0x03;
        var referenceCorner = (flags >> 4) & 0x03;
        var transposed = ((flags >> 6) & 0x01) != 0;
        var combOp = (flags >> 7) & 0x03;
        var defaultPixel = ((flags >> 9) & 0x01) != 0;
        var dsOffset = ((int)flags << 17) >> 27; // Sign-extends bits 10-14 (verified against pdf.js).
        var refinementTemplate = (flags >> 15) & 0x01;
        var pos = Jbig2RegionInfo.ByteSize + 2;

        if (huffman)
        {
            FilterDiagnostics.ReportDeviation("PLUME3557", "JBIG2: Huffman-coded text region is not supported; skipping it.", options, diagnostics, subject);
            return null;
        }

        (int X, int Y) rAt0 = default, rAt1 = default;
        if (refine && refinementTemplate == 0)
        {
            if (pos + 4 > data.Length)
            {
                return null;
            }

            rAt0 = (unchecked((sbyte)data[pos]), unchecked((sbyte)data[pos + 1]));
            rAt1 = (unchecked((sbyte)data[pos + 2]), unchecked((sbyte)data[pos + 3]));
            pos += 4;
        }

        if (pos + 4 > data.Length || info.Width <= 0 || info.Height <= 0 || info.X < 0 || info.Y < 0)
        {
            return null;
        }

        EnsurePixelBudget(info.Width, info.Height, maxDecodedPixels);

        var numberOfInstances = (int)ReadUInt32(data, pos);
        pos += 4;
        if (numberOfInstances < 0 || numberOfInstances > 1 << 24)
        {
            return null;
        }

        // A text region that declares instances but has NO symbols
        // from its referred dictionaries previously fell through to an empty bitmap with no
        // diagnostic — and an empty region composed silently renders as a solid sheet through
        // the image's polarity. Whatever broke the dictionaries (an undecoded globals stream,
        // a dangling segment reference), painting nothing loudly is the honest posture.
        if (symbols.Count == 0 && numberOfInstances > 0)
        {
            FilterDiagnostics.ReportDeviation("PLUME3559", $"JBIG2: text region declares {numberOfInstances} symbol instances but its referred segments supplied no symbols (missing, failed, or unresolved symbol dictionary); skipping it.", options, diagnostics, subject);
            return null;
        }

        var stripSize = 1 << logStripSize;
        var symbolCodeLength = SymbolCodeLength(symbols.Count);

        var bitmap = new bool[info.Height, info.Width];
        if (defaultPixel)
        {
            for (var y = 0; y < info.Height; y++)
            {
                for (var x = 0; x < info.Width; x++)
                {
                    bitmap[y, x] = true;
                }
            }
        }

        var decoder = new Jbig2ArithmeticDecoder(data.ToArray(), pos, data.Length);
        var intContexts = new Jbig2IntegerContexts();
        var refinementContexts = new byte[1 << 13];

        var stripT0 = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IADT"));
        if (stripT0 is null)
        {
            return new DecodedRegion(bitmap, info with { CombinationOperator = combOp <= 1 ? (byte)combOp : (byte)0 });
        }

        var stripT = -stripT0.Value * stripSize;
        var firstS = 0;
        var placed = 0;
        var guard = 0;
        var maxIterations = numberOfInstances + 4096;

        while (placed < numberOfInstances && guard++ < maxIterations)
        {
            var deltaT = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IADT"));
            if (deltaT is null)
            {
                break;
            }

            stripT += deltaT.Value * stripSize;

            var deltaFirstS = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IAFS"));
            if (deltaFirstS is null)
            {
                break;
            }

            firstS += deltaFirstS.Value;
            var currentS = firstS;
            var first = true;

            while (placed < numberOfInstances)
            {
                if (!first)
                {
                    var deltaS = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IADS"));
                    if (deltaS is null)
                    {
                        break; // OOB: end of this strip.
                    }

                    currentS += deltaS.Value + dsOffset;
                }

                first = false;

                var currentT = stripSize == 1 ? 0 : Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IAIT")) ?? 0;
                var t = stripT + currentT;

                if (symbols.Count == 0)
                {
                    break;
                }

                var symbolId = Jbig2ArithmeticInteger.DecodeIaid(decoder, intContexts.GetIaid(symbolCodeLength), symbolCodeLength);
                if (symbolId < 0 || symbolId >= symbols.Count)
                {
                    break;
                }

                var symbolBitmap = symbols[symbolId].Bitmap;

                if (refine)
                {
                    var applyRefinement = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IARI"));
                    if (applyRefinement == 1)
                    {
                        var rdw = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IARDW")) ?? 0;
                        var rdh = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IARDH")) ?? 0;
                        var rdx = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IARDX")) ?? 0;
                        var rdy = Jbig2ArithmeticInteger.DecodeInteger(decoder, intContexts.Get("IARDY")) ?? 0;
                        var refW = symbols[symbolId].Width + rdw;
                        var refH = symbols[symbolId].Height + rdh;
                        if (refW > 0 && refH > 0 && refW <= 1 << 16 && refH <= 1 << 16)
                        {
                            var refDx = (rdw >> 1) + rdx;
                            var refDy = (rdh >> 1) + rdy;
                            symbolBitmap = DecodeRefinementBitmap(refW, refH, refinementTemplate, rAt0, rAt1, symbolBitmap, refDx, refDy, decoder, refinementContexts, maxDecodedPixels);
                        }
                    }
                }

                var symbolWidth = symbolBitmap.GetLength(1);
                var symbolHeight = symbolBitmap.GetLength(0);

                // Reference-corner advancement per T.88 6.4.5 3(c)(x)/(xi), matching pdf.js
                // v5.6.205 decodeTextRegion exactly: right corners (non-transposed) and bottom
                // corners (transposed) PRE-advance CURS before the draw offsets are computed,
                // so the symbol occupies the same cells as the left/top-corner case and CURS
                // ends past its far edge; left/top corners advance AFTER via . The
                // prior shape skipped the pre-advance (drawing one symbol-extent short and
                // drifting CURS per symbol on right-corner strips) and never advanced at all
                // for transposed top corners.
                var increment = 0;
                if (!transposed)
                {
                    if (referenceCorner > 1)
                    {
                        currentS += symbolWidth - 1;
                    }
                    else
                    {
                        increment = symbolWidth - 1;
                    }
                }
                else if ((referenceCorner & 1) == 0)
                {
                    currentS += symbolHeight - 1;
                }
                else
                {
                    increment = symbolHeight - 1;
                }

                var offsetT = t - ((referenceCorner & 1) != 0 ? 0 : symbolHeight - 1);
                var offsetS = currentS - ((referenceCorner & 2) != 0 ? symbolWidth - 1 : 0);
                if (!transposed)
                {
                    CompositeSymbol(bitmap, symbolBitmap, offsetS, offsetT, combOp);
                }
                else
                {
                    CompositeSymbol(bitmap, symbolBitmap, offsetT, offsetS, combOp);
                }

                currentS += increment;
                placed++;
            }
        }

        return new DecodedRegion(bitmap, info with { CombinationOperator = 4 });
    }

    private static void CompositeSymbol(bool[,] region, bool[,] symbol, int destX, int destY, int combOp)
    {
        var height = region.GetLength(0);
        var width = region.GetLength(1);
        var symbolHeight = symbol.GetLength(0);
        var symbolWidth = symbol.GetLength(1);

        for (var y = 0; y < symbolHeight; y++)
        {
            var py = destY + y;
            if (py < 0 || py >= height)
            {
                continue;
            }

            for (var x = 0; x < symbolWidth; x++)
            {
                var px = destX + x;
                if (px < 0 || px >= width)
                {
                    continue;
                }

                var src = symbol[y, x];
                region[py, px] = combOp switch
                {
                    1 => region[py, px] && src,
                    2 => region[py, px] ^ src,
                    3 => !(region[py, px] ^ src),
                    _ => region[py, px] || src, // 0 = OR (default/most common SBCOMBOP).
                };
            }
        }
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
