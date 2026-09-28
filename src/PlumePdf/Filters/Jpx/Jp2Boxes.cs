using System.Text;

namespace PlumePdf.Filters.Jpx;

/// <summary>
/// JP2 file-format box parser (T.800 Annex I): signature, <c>ftyp</c>, the <c>jp2h</c> superbox
/// (<c>ihdr</c>, <c>bpcc</c>, <c>colr</c>, <c>pclr</c>/<c>cmap</c>, <c>cdef</c>, <c>res </c>), and
/// the first contiguous <c>jp2c</c>. Full colour/box rules landed on top
/// of an earlier minimal box walk (jp2h's <c>pclr</c>/<c>cmap</c>/<c>cdef</c> and the
/// first <c>jp2c</c>) — the later pass added <c>ftyp</c>, <c>bpcc</c>, the full <c>colr</c> rule set
/// (METH 1/2/3, multiple <c>colr</c> boxes), and <c>res </c>.
/// </summary>
/// <remarks>
/// Box walk only — no <c>ihdr</c>/<c>SIZ</c> cross-check here (that lives in
/// <see cref="JpxImageDecoder.Decode"/>, which has both <see cref="Jp2Container.Ihdr"/> and the
/// parsed <see cref="JpxSiz"/> in hand). A malformed box (bad <c>LBox</c>/<c>XLBox</c>) stops the
/// walk at that point rather than failing the whole parse: whatever <c>jp2c</c> was already found
/// (or a later one still reachable by continuing past a *sub*-box's own malformed child, which
/// only stops that child superbox's walk, not the top-level one) still decodes. The one
/// recoverable top-level shape is a <c>jp2c</c> whose declared length runs past the end of the
/// data — a file cut short inside its codestream — whose available bytes are handed to the
/// codestream decoder (which reports <c>PLUME3701</c> and decodes every complete tile-part)
/// rather than refused as not-JPEG-2000 (<c>PLUME3700</c>).
/// </remarks>
internal static class Jp2Boxes
{
    /// <summary>Walks the box structure of <paramref name="data"/> and returns the codestream plus the colour facts.</summary>
    public static Jp2Container Parse(ReadOnlyMemory<byte> data, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(options);

        var span = data.Span;
        ReadOnlyMemory<byte>? codestream = null;
        var codestreamCount = 0;
        var colour = new JpxColourInfo();
        (int NC, int BPC)? ihdr = null;

        var pos = 0;
        while (pos + 8 <= span.Length)
        {
            if (!TryReadBoxHeader(span, pos, out var bodyStart, out var bodyLength, out var type, out var boxTotalLength, out var runsPastEnd))
            {
                if (runsPastEnd && type == "jp2c")
                {
                    // A 'jp2c' whose declared length runs past EOF is the signature of a file cut
                    // short inside its codestream (a partial download, an interrupted export) —
                    // not of "not JPEG 2000". Hand the codestream decoder every byte that IS
                    // there: it reports the truncation as PLUME3701 and decodes every complete
                    // tile-part, exactly as it would for a raw .j2k cut at the same point, instead
                    // of this layer refusing the whole image with PLUME3700. The box-length disagreement itself is still a structural
                    // deviation of the wrapper and is recorded as such.
                    FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.Jp2BoxInvalid, $"'jp2c' box at offset {pos} declares a length that runs past the end of the data ({span.Length - bodyStart} byte(s) remain); decoding the available bytes as a truncated codestream.", options, diagnostics, null);
                    codestreamCount++;
                    codestream ??= data.Slice(bodyStart);
                    break;
                }

                FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.Jp2BoxInvalid, $"JP2 box at offset {pos} has an invalid LBox/XLBox length; stopping the box walk there.", options, diagnostics, null);
                break;
            }

            switch (type)
            {
                case "ftyp":
                    ParseFtyp(span.Slice(bodyStart, bodyLength), options, diagnostics);
                    break;
                case "jp2h":
                    ParseJp2Header(span.Slice(bodyStart, bodyLength), options, diagnostics, colour, ref ihdr);
                    break;
                case "jp2c":
                    codestreamCount++;
                    codestream ??= data.Slice(bodyStart, bodyLength);
                    break;
            }

            pos += boxTotalLength;
        }

        if (codestreamCount > 1)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.MultipleCodestreams, $"{codestreamCount} 'jp2c' codestream boxes (or a fragment table) found; decoding only the first contiguous codestream.", options, diagnostics, null);
        }

        if (codestream is not { } found)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.NotJpeg2000, "The JP2 file has no 'jp2c' codestream box.");
        }

        return new Jp2Container { Codestream = found, Colour = colour, Ihdr = ihdr };
    }

    /// <summary>
    /// Reads a <c>ftyp</c> box (I.5.2): <c>Brand</c>, <c>MinV</c>, and a compatibility list. This
    /// decoder targets ISO/IEC 15444-1 (Part 1) exclusively; a <c>ftyp</c> that names
    /// neither <c>Brand</c> nor any compatibility-list entry as <c>jp2 </c> is a real deviation
    /// from a conforming JP2 file, but <c>ftyp</c> declares compatibility, it does not gate
    /// decodability — the <c>jp2c</c> codestream is still attempted (lenient-by-default).
    /// </summary>
    private static void ParseFtyp(ReadOnlySpan<byte> body, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        const string Jp2Brand = "jp2 ";

        if (body.Length < 8)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.Jp2BoxInvalid, "ftyp box is too short to contain Brand/MinV; ignoring it.", options, diagnostics, null);
            return;
        }

        var brand = Encoding.ASCII.GetString(body[..4]);
        if (brand == Jp2Brand)
        {
            return;
        }

        for (var pos = 8; pos + 4 <= body.Length; pos += 4)
        {
            if (Encoding.ASCII.GetString(body.Slice(pos, 4)) == Jp2Brand)
            {
                return;
            }
        }

        FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.Jp2BoxInvalid, $"ftyp box declares brand '{brand}' with no 'jp2 ' compatibility-list entry; this decoder targets ISO/IEC 15444-1 (Part 1) JP2 files. Decoding the 'jp2c' codestream anyway.", options, diagnostics, null);
    }

    /// <summary>Reads one box header at <paramref name="pos"/>: <c>LBox</c>/<c>XLBox</c>, <c>TBox</c>, and where its body starts/ends.</summary>
    private static bool TryReadBoxHeader(ReadOnlySpan<byte> data, int pos, out int bodyStart, out int bodyLength, out string type, out int boxTotalLength) =>
        TryReadBoxHeader(data, pos, out bodyStart, out bodyLength, out type, out boxTotalLength, out _);

    /// <summary>
    /// As <see cref="TryReadBoxHeader(ReadOnlySpan{byte}, int, out int, out int, out string, out int)"/>, also
    /// distinguishing the one failure shape a caller may recover from: <paramref name="runsPastEnd"/>
    /// is <see langword="true"/> when the header itself is well-formed (<paramref name="type"/> and
    /// <paramref name="bodyStart"/> are then valid) but the declared length reaches beyond
    /// <paramref name="data"/> — a truncated file — as opposed to a length shorter than the header
    /// or a header cut off before its own length field.
    /// </summary>
    private static bool TryReadBoxHeader(ReadOnlySpan<byte> data, int pos, out int bodyStart, out int bodyLength, out string type, out int boxTotalLength, out bool runsPastEnd)
    {
        bodyStart = 0;
        bodyLength = 0;
        type = string.Empty;
        boxTotalLength = 0;
        runsPastEnd = false;

        if (pos + 8 > data.Length)
        {
            return false;
        }

        var lbox = ReadUInt32(data, pos);
        var tbox = Encoding.ASCII.GetString(data.Slice(pos + 4, 4));

        ulong total;
        int headerLength;
        if (lbox == 1)
        {
            if (pos + 16 > data.Length)
            {
                return false;
            }

            total = ReadUInt64(data, pos + 8);
            headerLength = 16;
        }
        else if (lbox == 0)
        {
            total = (ulong)(data.Length - pos);
            headerLength = 8;
        }
        else
        {
            total = lbox;
            headerLength = 8;
        }

        if (total < (ulong)headerLength)
        {
            return false;
        }

        type = tbox;
        bodyStart = pos + headerLength;
        if (total > int.MaxValue || (ulong)pos + total > (ulong)data.Length)
        {
            runsPastEnd = true;
            return false;
        }

        bodyLength = (int)total - headerLength;
        boxTotalLength = (int)total;
        return true;
    }

    /// <summary>Walks the <c>jp2h</c> superbox's children: <c>ihdr</c>, the first recognised <c>colr</c>, <c>pclr</c>/<c>cmap</c>, <c>cdef</c>, <c>res </c>.</summary>
    private static void ParseJp2Header(ReadOnlySpan<byte> body, PdfOptions options, DiagnosticCollection? diagnostics, JpxColourInfo colour, ref (int NC, int BPC)? ihdr)
    {
        (int Depth, int Entries, byte[] Table)? palette = null;
        int[]? channelMap = null;
        var colourResolved = false;

        var pos = 0;
        while (pos + 8 <= body.Length)
        {
            if (!TryReadBoxHeader(body, pos, out var bodyStart, out var bodyLength, out var type, out var boxTotalLength))
            {
                FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.Jp2BoxInvalid, "A sub-box inside 'jp2h' has an invalid LBox/XLBox length; stopping the jp2h walk there.", options, diagnostics, null);
                break;
            }

            var sub = body.Slice(bodyStart, bodyLength);
            switch (type)
            {
                case "ihdr":
                    if (ihdr is null && sub.Length >= 14)
                    {
                        ihdr = (ReadUInt16(sub, 8), sub[10]);
                    }

                    break;
                case "bpcc":
                    ParseBpcc(sub, options, diagnostics, ihdr);
                    break;
                case "colr":
                    if (!colourResolved)
                    {
                        colourResolved = TryParseColr(sub, options, diagnostics, colour);
                    }
                    else
                    {
                        // I.5.3.3 permits multiple 'colr' boxes; this decoder keeps the first one
                        // it can recognise (EnumCS 16/17/18/12, or an ICC profile mapped by
                        // component count). A later box that recognises to the SAME colour space
                        // is a genuine duplicate — real-world encoders repeat an identical colr
                        // box deliberately (veraPDF 6.2.8.3-t02-fail-a) — and decodes normally
                        // with no diagnostic at all; only a later box that disagrees (a different
                        // EnumCS/ICC component count) or fails to parse is a real deviation, and
                        // it goes through ReportDeviation like every other recoverable JP2
                        // deviation (Warning; throws under PdfOptions.Strict) —
                        // the first box still wins and the decode is unchanged, but a file whose
                        // colour boxes contradict each other is exactly what Strict exists to
                        // refuse. Parsed into a scratch
                        // JpxColourInfo (diagnostics suppressed for that parse) so a malformed
                        // duplicate never double-reports the structural deviations TryParseColr
                        // already reports for a first box.
                        var candidate = new JpxColourInfo();
                        var candidateResolved = TryParseColr(sub, options, diagnostics: null, candidate);
                        var agrees = candidateResolved
                            && candidate.EnumeratedColourSpace == colour.EnumeratedColourSpace
                            && candidate.IccComponentCount == colour.IccComponentCount;

                        if (!agrees)
                        {
                            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, "An additional 'colr' box disagrees with (or is less well-formed than) the recognised colour space an earlier 'colr' box already established; ignoring it (I.5.3.3 permits multiple boxes, the first recognised one wins).", options, diagnostics, null);
                        }
                    }

                    break;
                case "pclr":
                    palette ??= ParsePclr(sub, options, diagnostics);
                    break;
                case "cmap":
                    channelMap ??= ParseCmap(sub, options, diagnostics);
                    break;
                case "cdef":
                    ParseCdef(sub, options, diagnostics, colour);
                    break;
                case "res ":
                    ParseRes(sub, colour);
                    break;
            }

            pos += boxTotalLength;
        }

        if (palette is { } paletteValue)
        {
            colour.Palette = paletteValue;
            colour.ChannelMap = channelMap;
        }
    }

    /// <summary>Reads one <c>colr</c> box (I.5.3.3): METH 1 EnumCS (only 16 sRGB/17 greyscale/18 sYCC/12 CMYK recognised), or METH 2/3's ICC profile mapped by its data-colour-space signature (only 1/3/4-channel GRAY/RGB/CMYK recognised, never actually applied). Returns whether a recognised colour space was found.</summary>
    private static bool TryParseColr(ReadOnlySpan<byte> body, PdfOptions options, DiagnosticCollection? diagnostics, JpxColourInfo colour)
    {
        if (body.Length < 3)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, "colr box is too short to contain METH/PREC/APPROX; ignoring it.", options, diagnostics, null);
            return false;
        }

        var meth = body[0];
        if (meth == 1)
        {
            if (body.Length < 7)
            {
                FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, "colr box (METH 1) is too short to contain EnumCS; ignoring it.", options, diagnostics, null);
                return false;
            }

            var enumCs = (int)ReadUInt32(body, 3);
            if (enumCs is 16 or 17 or 18 or 12)
            {
                colour.EnumeratedColourSpace = enumCs;
                return true;
            }

            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, $"colr box declares an unrecognised EnumCS ({enumCs}); colour space will be derived from the colour-channel count instead.", options, diagnostics, null);
            return false;
        }

        if (meth is 2 or 3)
        {
            var profile = body[3..];
            if (profile.Length >= 20)
            {
                var space = Encoding.ASCII.GetString(profile.Slice(16, 4));
                var count = space switch
                {
                    "GRAY" => 1,
                    "RGB " => 3,
                    "CMYK" => 4,
                    _ => (int?)null,
                };

                if (count is { } channelCount)
                {
                    colour.IccComponentCount = channelCount;
                    return true;
                }
            }

            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, "colr box (ICC) profile's data colour space is not one of GRAY/RGB/CMYK; colour space will be derived from the colour-channel count instead.", options, diagnostics, null);
            return false;
        }

        FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, $"colr box declares an unrecognised METH ({meth}); colour space will be derived from the colour-channel count instead.", options, diagnostics, null);
        return false;
    }

    /// <summary>
    /// Reads a <c>bpcc</c> box (I.5.3.2): one byte per component, in the same bit-depth/sign
    /// encoding as <c>ihdr</c>'s own <c>BPC</c> field, present only when that field is <c>0xFF</c>
    /// (bit depth/signedness varies per component). Validated for structural consistency against
    /// <c>ihdr</c> only — the per-component values themselves are not propagated further: the
    /// codestream's own <c>SIZ</c> segment is the authoritative source
    /// <see cref="JpxImageDecoder"/>'s <c>ihdr</c>/<c>SIZ</c> cross-check already compares against,
    /// and a <c>bpcc</c> box that agrees with <c>ihdr</c>'s component count adds nothing <c>SIZ</c>
    /// doesn't already say more completely.
    /// </summary>
    private static void ParseBpcc(ReadOnlySpan<byte> body, PdfOptions options, DiagnosticCollection? diagnostics, (int NC, int BPC)? ihdr)
    {
        if (body.Length == 0)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.Jp2BoxInvalid, "bpcc box is empty; ignoring it.", options, diagnostics, null);
            return;
        }

        if (ihdr is not { } value)
        {
            return;
        }

        if (value.BPC != 0xFF)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.Jp2BoxInvalid, $"bpcc box is present but ihdr's BPC field (0x{value.BPC:X2}) does not declare 0xFF (per-component bit depth); ignoring the bpcc box.", options, diagnostics, null);
            return;
        }

        if (body.Length != value.NC)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.Jp2BoxInvalid, $"bpcc box declares {body.Length} component byte(s) but ihdr's NC is {value.NC}; ignoring the bpcc box.", options, diagnostics, null);
        }
    }

    /// <summary>
    /// Reads a <c>pclr</c> box (I.5.3.4): <c>NE</c> entries, <c>NPC</c> columns, each column's own
    /// bit depth <c>Bi</c> normalised to the table's widest column so
    /// <see cref="JpxComponentTransform.ExpandPalette"/>'s single-<c>Depth</c> contract holds even
    /// when columns declare different depths.
    /// </summary>
    /// <remarks>
    /// Validates <c>NE</c>/<c>NPC</c> against the box's own body length BEFORE indexing into it or
    /// sizing the output table — an adversarial or truncated box (e.g. 65,535 declared entries in
    /// a 15-byte body) would otherwise walk <paramref name="body"/> past its end (a bare
    /// <see cref="IndexOutOfRangeException"/>) after already allocating an entries×columns table.
    /// A too-short box is reported (<c>PLUME3715</c>) and ignored, per this box family's documented
    /// "pclr/cmap/cdef inconsistent — box ignored" contract, rather than thrown.
    /// </remarks>
    private static (int Depth, int Entries, byte[] Table)? ParsePclr(ReadOnlySpan<byte> body, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (body.Length < 3)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, "pclr box is too short to contain NE/NPC; ignoring it.", options, diagnostics, null);
            return null;
        }

        var entries = ReadUInt16(body, 0);
        var columns = body[2];
        if (entries == 0 || columns == 0)
        {
            // I.5.3.4: NE is 1..1024 and NPC is 1..255. A palette with no entries or no columns
            // maps every index to nothing; ignoring the box keeps the index plane as the image.
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, $"pclr box declares {entries} entries x {columns} column(s); both must be at least 1. Ignoring it.", options, diagnostics, null);
            return null;
        }

        if (body.Length < 3 + columns)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, $"pclr box is too short to hold its {columns} declared column bit-depth byte(s); ignoring it.", options, diagnostics, null);
            return null;
        }

        var columnDepths = new int[columns];
        var maxDepth = 0;
        var sourceBytesPerEntry = 0;
        for (var c = 0; c < columns; c++)
        {
            var depth = (body[3 + c] & 0x7F) + 1;
            if (depth > 16)
            {
                // Bi allows up to 38 bits per palette entry; this decoder's planes carry at most
                // 16 (the same ceiling PLUME3705 enforces for codestream components), and a
                // silently truncated 38-bit entry would be wrong colour, not a narrow one.
                FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, $"pclr box column {c} declares {depth}-bit entries; palette depths above 16 bits are not supported. Ignoring the palette.", options, diagnostics, null);
                return null;
            }

            columnDepths[c] = depth;
            maxDepth = Math.Max(maxDepth, depth);
            sourceBytesPerEntry += (depth + 7) / 8;
        }

        var expectedLength = 3 + columns + ((long)entries * sourceBytesPerEntry);
        if (body.Length < expectedLength)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, $"pclr box declares {entries} entries x {columns} column(s) ({expectedLength} bytes needed) but its body is only {body.Length} byte(s); ignoring it.", options, diagnostics, null);
            return null;
        }

        var bytesPerChannel = maxDepth <= 8 ? 1 : 2;
        var table = new byte[entries * columns * bytesPerChannel];
        var pos = 3 + columns;
        for (var e = 0; e < entries; e++)
        {
            for (var c = 0; c < columns; c++)
            {
                var sourceBytes = (columnDepths[c] + 7) / 8;
                long value = 0;
                for (var b = 0; b < sourceBytes; b++)
                {
                    value = (value << 8) | body[pos++];
                }

                var outOffset = ((e * columns) + c) * bytesPerChannel;
                if (bytesPerChannel == 1)
                {
                    table[outOffset] = (byte)value;
                }
                else
                {
                    table[outOffset] = (byte)(value >> 8);
                    table[outOffset + 1] = (byte)value;
                }
            }
        }

        return (maxDepth, entries, table);
    }

    /// <summary>The most output channels a <c>cmap</c> box may define: one per palette column (I.5.3.4 caps <c>NPC</c> at 255) or per codestream component used directly, and no JP2 producer emits a palette image with more — a longer box is an amplification attempt, not a colour mapping.</summary>
    private const int MaxCmapEntries = 255;

    /// <summary>
    /// Reads a <c>cmap</c> box (I.5.3.5): one output-channel entry per 4 bytes (<c>CMP</c>,
    /// <c>MTYP</c>, <c>PCOL</c>). A palette-mapped entry (<c>MTYP</c> 1) is recorded as its palette
    /// column <c>PCOL</c>; a direct-use entry (<c>MTYP</c> 0) as <c>~CMP</c> (negative), the encoding
    /// <see cref="JpxComponentTransform.ExpandPalette"/> reads. A box longer than
    /// <see cref="MaxCmapEntries"/> entries, or one whose length is not a whole number of entries,
    /// is reported (<c>PLUME3715</c>) and ignored.
    /// </summary>
    private static int[]? ParseCmap(ReadOnlySpan<byte> body, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        var count = body.Length / 4;
        if (count == 0 || body.Length % 4 != 0 || count > MaxCmapEntries)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, $"cmap box body of {body.Length} byte(s) is not 1..{MaxCmapEntries} whole 4-byte channel entries; ignoring it.", options, diagnostics, null);
            return null;
        }

        var map = new int[count];
        for (var i = 0; i < count; i++)
        {
            var offset = i * 4;
            var cmp = ReadUInt16(body, offset);
            var mtyp = body[offset + 2];
            var pcol = body[offset + 3];
            if (mtyp > 1)
            {
                FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, $"cmap entry {i} declares an unknown MTYP ({mtyp}); ignoring the cmap box.", options, diagnostics, null);
                return null;
            }

            map[i] = mtyp == 1 ? pcol : ~cmp;
        }

        return map;
    }

    /// <summary>
    /// Reads a <c>cdef</c> box (I.5.3.6): the FIRST channel definition entry whose <c>Typ</c> is
    /// opacity (1) or premultiplied opacity (2) becomes <see cref="JpxColourInfo.AlphaChannelIndex"/>;
    /// later opacity entries are ignored (a PDF soft mask is one channel). The channel index is
    /// validated against the decoded plane count by <see cref="JpxImageDecoder"/>, which is the
    /// first place that count is known — an out-of-range index there is reported
    /// (<c>PLUME3715</c>) and the box ignored, never left to index past the planes.
    /// </summary>
    private static void ParseCdef(ReadOnlySpan<byte> body, PdfOptions options, DiagnosticCollection? diagnostics, JpxColourInfo colour)
    {
        if (body.Length < 2)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, "cdef box is too short to hold its entry count; ignoring it.", options, diagnostics, null);
            return;
        }

        var count = ReadUInt16(body, 0);
        for (var i = 0; i < count; i++)
        {
            var offset = 2 + (i * 6);
            if (offset + 6 > body.Length)
            {
                FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.ColourBoxInvalid, $"cdef box declares {count} entries but holds only {i}; ignoring the rest.", options, diagnostics, null);
                break;
            }

            var channel = ReadUInt16(body, offset);
            var typ = ReadUInt16(body, offset + 2);
            if (typ is 1 or 2)
            {
                colour.AlphaChannelIndex = channel;
                colour.AlphaPremultiplied = typ == 2;
                break;
            }
        }
    }

    /// <summary>Reads a <c>res </c> superbox (I.5.3.7): prefers a nested <c>resd</c> (display) over <c>resc</c> (capture), each ten bytes (<c>VRcN</c>/<c>VRcD</c>/<c>HRcN</c>/<c>HRcD</c> as <see cref="ushort"/>, then <c>VRcE</c>/<c>HRcE</c> as signed byte exponents), value <c>(N/D)·10^E</c> pixels per metre per axis.</summary>
    private static void ParseRes(ReadOnlySpan<byte> body, JpxColourInfo colour)
    {
        (double X, double Y)? resc = null;
        (double X, double Y)? resd = null;

        var pos = 0;
        while (pos + 8 <= body.Length)
        {
            if (!TryReadBoxHeader(body, pos, out var bodyStart, out var bodyLength, out var type, out var boxTotalLength))
            {
                break;
            }

            if ((type == "resc" || type == "resd") && bodyLength >= 10)
            {
                var sub = body.Slice(bodyStart, bodyLength);
                var vN = ReadUInt16(sub, 0);
                var vD = ReadUInt16(sub, 2);
                var hN = ReadUInt16(sub, 4);
                var hD = ReadUInt16(sub, 6);
                var vE = unchecked((sbyte)sub[8]);
                var hE = unchecked((sbyte)sub[9]);
                var vRes = (vD == 0 ? 0.0 : (double)vN / vD) * Math.Pow(10, vE);
                var hRes = (hD == 0 ? 0.0 : (double)hN / hD) * Math.Pow(10, hE);
                if (type == "resd")
                {
                    resd = (hRes, vRes);
                }
                else
                {
                    resc = (hRes, vRes);
                }
            }

            pos += boxTotalLength;
        }

        colour.PixelsPerMetre = resd ?? resc;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset)
    {
        ulong value = 0;
        for (var i = 0; i < 8; i++)
        {
            value = (value << 8) | data[offset + i];
        }

        return value;
    }
}
