namespace PlumePdf.Filters.Jpx;

/// <summary>
/// Codestream marker-segment parser (T.800 Annex A): <c>SOC</c>, <c>SIZ</c>, <c>COD</c>/<c>COC</c>,
/// <c>QCD</c>/<c>QCC</c>, <c>RGN</c> (refused), <c>POC</c>, <c>PPM</c>/<c>PPT</c> (refused), <c>TLM</c>,
/// <c>PLM</c>, <c>PLT</c>, <c>CRG</c>, <c>COM</c>, <c>SOT</c>/<c>SOD</c>, <c>EOC</c>; precedence and
/// placement rules; tile-part assembly.
/// </summary>
internal static class JpxCodestream
{
    private const ushort Soc = 0xFF4F;
    private const ushort SizMarker = 0xFF51;
    private const ushort CodMarker = 0xFF52;
    private const ushort CocMarker = 0xFF53;
    private const ushort QcdMarker = 0xFF5C;
    private const ushort QccMarker = 0xFF5D;
    private const ushort RgnMarker = 0xFF5E;
    private const ushort PocMarker = 0xFF5F;
    private const ushort PpmMarker = 0xFF60;
    private const ushort PptMarker = 0xFF61;
    private const ushort SotMarker = 0xFF90;
    private const ushort SodMarker = 0xFF93;
    private const ushort EocMarker = 0xFFD9;

    /// <summary>Largest <c>M_b</c> (E-2) whose bit-planes fit a 32-bit sign-magnitude coefficient.</summary>
    internal const int MaxBitPlanes = 31;

    /// <summary>Table A.15: the largest number of decomposition levels, so <c>REpoc</c> (Table A.32) is at most 33.</summary>
    private const int MaxResolutionLevels = 33;

    /// <summary>
    /// Internal control-flow signal for "ran out of bytes" — never surfaced to a caller. Caught
    /// once, at the top of <see cref="Parse"/>, and translated into either a
    /// <see cref="JpxDiagnosticCodes.Truncated"/> deviation (main header already complete: SIZ,
    /// COD and QCD all resolved, so whatever tile-parts decoded so far are usable) or a thrown
    /// <see cref="JpxDiagnosticCodes.HeaderInvalid"/> (truncated before the main header itself
    /// finished — nothing usable can be returned).
    /// </summary>
    private sealed class TruncatedSignal(string reason) : Exception(reason);

    /// <summary>Parses the main header and every tile-part header of <paramref name="codestream"/> (a raw J2K codestream starting at <c>SOC</c>).</summary>
    public static JpxCodestreamHeader Parse(ReadOnlyMemory<byte> codestream, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(options);
        var data = codestream.Span;

        if (data.Length < 4 || ReadUInt16(data, 0) != Soc)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.NotJpeg2000, "Input does not begin with a JPEG 2000 codestream SOC marker (0xFF4F).");
        }

        JpxSiz? siz = null;
        JpxCodingStyle? mainCod = null;
        JpxQuantization? mainQcd = null;
        var componentCoc = new Dictionary<int, JpxCodingStyle>();
        var componentQcc = new Dictionary<int, JpxQuantization>();
        var mainPoc = Array.Empty<JpxProgressionVolume>();
        var tileParts = new List<JpxTilePart>();
        var tileOverrides = new Dictionary<int, (JpxCodingStyle[] Coding, JpxQuantization[] Quant)>();
        var nextTPsotByTile = new Dictionary<int, int>();
        var tnsotByTile = new Dictionary<int, int>();
        var truncatedMessage = (string?)null;

        try
        {
            var position = 2;

            // ---- main header: SIZ must be the very first segment after SOC ----
            RequireBytes(data, position, 2, "the first main-header marker");
            if (ReadUInt16(data, position) != SizMarker)
            {
                throw HeaderInvalid("The first marker segment after SOC must be SIZ.");
            }

            siz = ParseSiz(data, ref position);

            while (true)
            {
                var marker = PeekMarkerOrThrow(data, position);
                if (marker == SotMarker || marker == EocMarker)
                {
                    break;
                }

                switch (marker)
                {
                    case CodMarker:
                        if (mainCod is not null)
                        {
                            throw HeaderInvalid("Duplicate COD marker segment in the main header.");
                        }

                        mainCod = ParseCod(data, ref position);
                        break;
                    case CocMarker:
                        if (mainCod is null)
                        {
                            throw HeaderInvalid("COC marker segment appeared before COD in the main header.");
                        }

                        var (cocComponent, cocStyle) = ParseCoc(data, ref position, siz.Components.Length, mainCod);
                        componentCoc[cocComponent] = cocStyle;
                        break;
                    case QcdMarker:
                        if (mainQcd is not null)
                        {
                            throw HeaderInvalid("Duplicate QCD marker segment in the main header.");
                        }

                        mainQcd = ParseQcd(data, ref position);
                        break;
                    case QccMarker:
                        if (mainQcd is null)
                        {
                            throw HeaderInvalid("QCC marker segment appeared before QCD in the main header.");
                        }

                        var (qccComponent, qccQuant) = ParseQcc(data, ref position, siz.Components.Length);
                        componentQcc[qccComponent] = qccQuant;
                        break;
                    case RgnMarker:
                        throw new PlumePdfException(JpxDiagnosticCodes.RoiUnsupported, "RGN (region-of-interest) marker segment is present — not supported.");
                    case PocMarker:
                        if (TryParsePoc(data, ref position, siz.Components.Length, out var pocEntries))
                        {
                            mainPoc = pocEntries;
                        }
                        else
                        {
                            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.PocInvalid, "Main-header POC segment is malformed; ignoring it (the COD progression order applies).", options, diagnostics, null);
                        }

                        break;
                    case PpmMarker:
                    case PptMarker:
                        throw new PlumePdfException(JpxDiagnosticCodes.PackedHeadersUnsupported, "PPM/PPT (packed packet headers) marker segment is present — not supported.");
                    default:
                        SkipSegment(data, ref position);
                        break;
                }
            }

            if (mainCod is null)
            {
                throw HeaderInvalid("COD marker segment is required in the main header.");
            }

            if (mainQcd is null)
            {
                throw HeaderInvalid("QCD marker segment is required in the main header.");
            }

            var coding = new JpxCodingStyle[siz.Components.Length];
            var quant = new JpxQuantization[siz.Components.Length];
            for (var c = 0; c < siz.Components.Length; c++)
            {
                coding[c] = componentCoc.TryGetValue(c, out var s) ? s : mainCod;
                quant[c] = componentQcc.TryGetValue(c, out var q) ? q : mainQcd;
            }

            ReportShortQuantisation(coding, quant, "the main header", options, diagnostics);
            var tileCount = (long)siz.NumXTiles * siz.NumYTiles;

            // ---- tile-parts: SOT, its header segments, SOD, body — repeated until EOC ----
            while (true)
            {
                RequireBytes(data, position, 2, "an SOT or EOC marker");
                var marker = ReadUInt16(data, position);
                if (marker == EocMarker)
                {
                    position += 2;
                    break;
                }

                if (marker != SotMarker)
                {
                    // Not a marker this parser expects between tile-parts — lenient: skip it by
                    // its own declared length rather than failing the whole decode over it.
                    SkipSegment(data, ref position);
                    continue;
                }

                var tilePartStart = position;
                var sot = ParseSot(data, ref position);
                var tileIndexInRange = sot.TileIndex < tileCount;
                if (!tileIndexInRange)
                {
                    FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.TilePartSequence, $"SOT names tile {sot.TileIndex}, but the SIZ tile grid has only {tileCount} tile(s); skipping this tile-part.", options, diagnostics, null);
                }

                // A.4.2 tile-part sequencing: TPsot must arrive in order per tile, and once a
                // non-zero TNsot is declared for a tile, no TPsot may reach or exceed it. A
                // violation is recoverable — the tile-part's own header/body still parse fine —
                // so it is reported and every consistent part is kept, never thrown.
                var expectedTPsot = nextTPsotByTile.GetValueOrDefault(sot.TileIndex);
                var sequenceBroken = sot.TPsot != expectedTPsot;
                if (tnsotByTile.TryGetValue(sot.TileIndex, out var knownTNsot) && knownTNsot != 0 && sot.TPsot >= knownTNsot)
                {
                    sequenceBroken = true;
                }

                if (sequenceBroken)
                {
                    FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.TilePartSequence, $"Tile {sot.TileIndex} tile-part sequencing is inconsistent (TPsot={sot.TPsot}, expected {expectedTPsot}); keeping every tile-part that parses.", options, diagnostics, null);
                }

                nextTPsotByTile[sot.TileIndex] = sot.TPsot + 1;
                if (sot.TNsot != 0)
                {
                    tnsotByTile[sot.TileIndex] = sot.TNsot;
                }

                JpxCodingStyle? tpCod = null;
                JpxQuantization? tpQcd = null;
                var tpCoc = new Dictionary<int, JpxCodingStyle>();
                var tpQcc = new Dictionary<int, JpxQuantization>();
                JpxProgressionVolume[]? tpPoc = null;

                while (true)
                {
                    RequireBytes(data, position, 2, "a tile-part header marker or SOD");
                    var tpMarker = ReadUInt16(data, position);
                    if (tpMarker == SodMarker)
                    {
                        position += 2;
                        break;
                    }

                    switch (tpMarker)
                    {
                        case CodMarker:
                            if (sot.TPsot != 0)
                            {
                                throw HeaderInvalid("COD marker segment appeared in a non-first tile-part.");
                            }

                            tpCod = ParseCod(data, ref position);
                            break;
                        case CocMarker:
                            if (sot.TPsot != 0)
                            {
                                throw HeaderInvalid("COC marker segment appeared in a non-first tile-part.");
                            }

                            var (tpCocComponent, tpCocStyle) = ParseCoc(data, ref position, siz.Components.Length, tpCod ?? mainCod);
                            tpCoc[tpCocComponent] = tpCocStyle;
                            break;
                        case QcdMarker:
                            if (sot.TPsot != 0)
                            {
                                throw HeaderInvalid("QCD marker segment appeared in a non-first tile-part.");
                            }

                            tpQcd = ParseQcd(data, ref position);
                            break;
                        case QccMarker:
                            if (sot.TPsot != 0)
                            {
                                throw HeaderInvalid("QCC marker segment appeared in a non-first tile-part.");
                            }

                            var (tpQccComponent, tpQccQuant) = ParseQcc(data, ref position, siz.Components.Length);
                            tpQcc[tpQccComponent] = tpQccQuant;
                            break;
                        case RgnMarker:
                            throw new PlumePdfException(JpxDiagnosticCodes.RoiUnsupported, "RGN (region-of-interest) marker segment is present — not supported.");
                        case PocMarker:
                            if (TryParsePoc(data, ref position, siz.Components.Length, out var tpPocEntries))
                            {
                                tpPoc = tpPocEntries;
                            }
                            else
                            {
                                FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.PocInvalid, $"Tile-part POC segment (tile {sot.TileIndex}) is malformed; ignoring it.", options, diagnostics, null);
                            }

                            break;
                        case PpmMarker:
                        case PptMarker:
                            throw new PlumePdfException(JpxDiagnosticCodes.PackedHeadersUnsupported, "PPM/PPT (packed packet headers) marker segment is present — not supported.");
                        default:
                            SkipSegment(data, ref position);
                            break;
                    }
                }

                var bodyStart = position;

                // A.4.2: Psot = 0 legitimately means "this tile-part runs to the terminating EOC
                // marker" — the EOC's own 2 bytes are NOT part of the tile-part's packet data.
                // Including them (the old `data.Length` end point) fed the packet decoder two
                // bogus trailing body bytes AND left `position` sitting past the EOC, so the next
                // loop iteration's "expect an SOT or EOC marker" read found nothing and signalled
                // a truncation deviation on an otherwise perfectly conformant stream. When the
                // data does end with EOC (the expected case), stop one marker short of it so the
                // outer loop's own EOC handling runs normally; a stream that does NOT end with
                // EOC there is already non-conformant, so the prior (consume-to-end) behaviour is
                // kept as the fallback rather than guessing at where the real end is.
                var endsWithEoc = data.Length >= bodyStart + 2 && data[data.Length - 2] == 0xFF && data[data.Length - 1] == 0xD9;
                var psot = sot.Psot;
                if (psot != 0 && tilePartStart + (long)psot < bodyStart)
                {
                    // A.4.2: Psot counts from the SOT marker's first byte to the end of the
                    // tile-part's data, so it can never be shorter than the tile-part header
                    // just parsed. A value that is (1, 5, 13 …) is inconsistent — reported, and
                    // read as the "to EOC" meaning of Psot = 0 rather than as an empty body that
                    // would leave the packet bytes to be re-parsed as marker segments.
                    FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.TilePartSequence, $"Tile {sot.TileIndex} tile-part {sot.TPsot} declares Psot = {psot}, shorter than its own header ({bodyStart - tilePartStart} bytes); treating it as running to EOC.", options, diagnostics, null);
                    psot = 0;
                }

                var tilePartEndLong = psot == 0
                    ? (endsWithEoc ? (long)data.Length - 2 : data.Length)
                    : tilePartStart + (long)psot;
                if (tilePartEndLong > data.Length)
                {
                    throw new TruncatedSignal($"Tile-part {sot.TPsot} of tile {sot.TileIndex} declares {psot} byte(s) starting at offset {tilePartStart}, which runs past the end of the codestream data.");
                }

                var tilePartEnd = (int)Math.Max(tilePartEndLong, bodyStart);

                if (!tileIndexInRange)
                {
                    position = tilePartEnd;
                    continue;
                }

                tileParts.Add(new JpxTilePart
                {
                    TileIndex = sot.TileIndex,
                    TPsot = sot.TPsot,
                    TNsot = sot.TNsot,
                    Data = codestream.Slice(bodyStart, tilePartEnd - bodyStart),
                    Poc = tpPoc,
                });

                if (sot.TPsot == 0 && (tpCod is not null || tpCoc.Count > 0 || tpQcd is not null || tpQcc.Count > 0))
                {
                    var tileCoding = new JpxCodingStyle[siz.Components.Length];
                    var tileQuant = new JpxQuantization[siz.Components.Length];
                    for (var c = 0; c < siz.Components.Length; c++)
                    {
                        tileCoding[c] = tpCoc.TryGetValue(c, out var s) ? s : tpCod ?? coding[c];
                        tileQuant[c] = tpQcc.TryGetValue(c, out var q) ? q : tpQcd ?? quant[c];
                    }

                    tileOverrides[sot.TileIndex] = (tileCoding, tileQuant);
                    ReportShortQuantisation(tileCoding, tileQuant, $"tile {sot.TileIndex}'s first tile-part header", options, diagnostics);
                }

                position = tilePartEnd;
            }
        }
        catch (TruncatedSignal signal)
        {
            if (siz is null || mainCod is null || mainQcd is null)
            {
                throw HeaderInvalid($"Codestream truncated before the main header completed: {signal.Message}");
            }

            truncatedMessage = signal.Message;
        }

        if (truncatedMessage is not null)
        {
            FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.Truncated, $"Codestream truncated ({truncatedMessage}); {tileParts.Count} complete tile-part(s) kept.", options, diagnostics, null);
        }

        var finalCoding = new JpxCodingStyle[siz!.Components.Length];
        var finalQuant = new JpxQuantization[siz.Components.Length];
        for (var c = 0; c < siz.Components.Length; c++)
        {
            finalCoding[c] = componentCoc.TryGetValue(c, out var s) ? s : mainCod!;
            finalQuant[c] = componentQcc.TryGetValue(c, out var q) ? q : mainQcd!;
        }

        var header = new JpxCodestreamHeader
        {
            Siz = siz,
            Coding = finalCoding,
            Quant = finalQuant,
            MainPoc = mainPoc,
            TileParts = tileParts,
        };
        foreach (var (tileIndex, ov) in tileOverrides)
        {
            header.TileOverrides[tileIndex] = ov;
        }

        return header;
    }

    /// <summary>
    /// Styles 0 and 2 transmit one <c>SPqcd</c> entry per subband — <c>3·N_L + 1</c> of them
    /// (Table A.28). A segment carrying fewer is recoverable (<see cref="JpxGeometry.SubbandQuantisation"/>
    /// reuses the last transmitted entry for the missing finest subbands, the same recovery a
    /// reference decoder applies), but it is a header defect the caller must hear about, not a
    /// silent clamp: reported once per component under <c>PLUME3707</c>'s deviation arm.
    /// </summary>
    private static void ReportShortQuantisation(JpxCodingStyle[] coding, JpxQuantization[] quant, string where, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        for (var c = 0; c < coding.Length; c++)
        {
            if (quant[c].Style == 1)
            {
                continue;
            }

            var required = (3 * coding[c].DecompositionLevels) + 1;
            if (quant[c].Steps.Length < required)
            {
                FilterDiagnostics.ReportDeviation(JpxDiagnosticCodes.HeaderInvalid, $"QCD/QCC in {where} carries {quant[c].Steps.Length} SPqcd entr{(quant[c].Steps.Length == 1 ? "y" : "ies")} for component {c}, which has {required} subbands; the missing subbands reuse the last transmitted step.", options, diagnostics, null);
            }
        }
    }

    private static ushort PeekMarkerOrThrow(ReadOnlySpan<byte> data, int position)
    {
        RequireBytes(data, position, 2, "the next main-header marker");
        return ReadUInt16(data, position);
    }

    // ---- SIZ (A.5.1) ----

    private static JpxSiz ParseSiz(ReadOnlySpan<byte> data, ref int position)
    {
        position += 2; // consume the SIZ marker code
        RequireBytes(data, position, 2, "SIZ segment length");
        var length = ReadUInt16(data, position);
        var segStart = position;
        if (length < 38)
        {
            throw HeaderInvalid($"SIZ segment length ({length}) is too short for even a zero-component image.");
        }

        RequireSegment(data, segStart, length, "SIZ");
        var payload = segStart + 2;

        var csiz = ReadUInt16(data, payload + 34);
        if (csiz == 0)
        {
            // A zero-component image is not a recoverable deviation to fall back from: every
            // downstream stage (tile/component/resolution/subband build, MCT/palette lookups)
            // indexes component 0 unconditionally, so silently accepting it just moves the
            // crash further downstream (bare IndexOutOfRangeException) instead of naming it here.
            throw new PlumePdfException(JpxDiagnosticCodes.GeometryInvalid, "SIZ declares zero components (Csiz = 0).");
        }

        var expectedLength = 38 + (3 * csiz);
        if (length != expectedLength)
        {
            throw HeaderInvalid($"SIZ segment length ({length}) does not match its declared component count ({csiz}); expected {expectedLength}.");
        }

        var rsiz = ReadUInt16(data, payload);
        if ((rsiz & 0x8000) != 0)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.Part2, "SIZ Rsiz bit 15 is set — this is a Part 2 (extended) codestream, not supported.");
        }

        var xsiz = ToInt32OrGeometryInvalid(ReadUInt32(data, payload + 2), "Xsiz");
        var ysiz = ToInt32OrGeometryInvalid(ReadUInt32(data, payload + 6), "Ysiz");
        var xOsiz = ToInt32OrGeometryInvalid(ReadUInt32(data, payload + 10), "XOsiz");
        var yOsiz = ToInt32OrGeometryInvalid(ReadUInt32(data, payload + 14), "YOsiz");
        var xTsiz = ToInt32OrGeometryInvalid(ReadUInt32(data, payload + 18), "XTsiz");
        var yTsiz = ToInt32OrGeometryInvalid(ReadUInt32(data, payload + 22), "YTsiz");
        var xTOsiz = ToInt32OrGeometryInvalid(ReadUInt32(data, payload + 26), "XTOsiz");
        var yTOsiz = ToInt32OrGeometryInvalid(ReadUInt32(data, payload + 30), "YTOsiz");

        if (xOsiz >= xsiz || yOsiz >= ysiz || xTsiz <= 0 || yTsiz <= 0 ||
            xTOsiz > xOsiz || xTOsiz + (long)xTsiz <= xOsiz ||
            yTOsiz > yOsiz || yTOsiz + (long)yTsiz <= yOsiz)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.GeometryInvalid, "SIZ image/tile geometry is inconsistent (origin at or past the image size, or the tile grid does not cover the image origin).");
        }

        // B-5's tile counts, ⌈(Xsiz − XTOsiz) / XTsiz⌉, are evaluated by JpxSiz in int; a grid
        // whose Xsiz − XTOsiz + XTsiz − 1 passes int.MaxValue would wrap them negative (and the
        // tile count, their product, is taken in long by the decoder). Such a grid is refused as
        // geometry this decoder cannot represent — no conformant image needs a 2^31-pixel tile.
        if ((long)xsiz - xTOsiz + xTsiz - 1 > int.MaxValue || (long)ysiz - yTOsiz + yTsiz - 1 > int.MaxValue)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.GeometryInvalid, "SIZ tile grid extends past the representable coordinate range.");
        }

        var components = new JpxComponentInfo[csiz];
        for (var c = 0; c < csiz; c++)
        {
            var compOffset = payload + 36 + (c * 3);
            var ssiz = data[compOffset];
            var xr = data[compOffset + 1];
            var yr = data[compOffset + 2];
            var precision = (ssiz & 0x7F) + 1;
            if (precision > 16)
            {
                throw new PlumePdfException(JpxDiagnosticCodes.PrecisionUnsupported, $"Component {c} declares {precision}-bit precision — precision above 16 bits is not supported.");
            }

            if (xr == 0 || yr == 0)
            {
                throw new PlumePdfException(JpxDiagnosticCodes.GeometryInvalid, $"Component {c} declares a zero sub-sampling factor.");
            }

            // B-2: the component's own grid is [⌈XOsiz/XRsiz⌉, ⌈Xsiz/XRsiz⌉) × the Y analogue. A
            // sub-sampling factor that leaves it with no samples at all (XOsiz = 99, Xsiz = 100,
            // XRsiz = 255) is degenerate geometry — every later stage (tile-component bounds,
            // upsampling to the reference grid) assumes at least one sample per component.
            var componentWidth = JpxGeometry.CeilDiv(xsiz, xr) - JpxGeometry.CeilDiv(xOsiz, xr);
            var componentHeight = JpxGeometry.CeilDiv(ysiz, yr) - JpxGeometry.CeilDiv(yOsiz, yr);
            if (componentWidth <= 0 || componentHeight <= 0)
            {
                throw new PlumePdfException(JpxDiagnosticCodes.GeometryInvalid, $"Component {c}'s sub-sampling ({xr}x{yr}) leaves it with no samples on the image area.");
            }

            components[c] = new JpxComponentInfo(precision, (ssiz & 0x80) != 0, xr, yr);
        }

        position = segStart + length;
        return new JpxSiz
        {
            Xsiz = xsiz,
            Ysiz = ysiz,
            XOsiz = xOsiz,
            YOsiz = yOsiz,
            XTsiz = xTsiz,
            YTsiz = yTsiz,
            XTOsiz = xTOsiz,
            YTOsiz = yTOsiz,
            Rsiz = rsiz,
            Components = components,
        };
    }

    // ---- COD (A.6.1) / COC (A.6.2) ----

    private static JpxCodingStyle ParseCod(ReadOnlySpan<byte> data, ref int position)
    {
        position += 2;
        RequireBytes(data, position, 2, "COD segment length");
        var length = ReadUInt16(data, position);
        var segStart = position;
        RequireSegment(data, segStart, length, "COD");
        var payload = segStart + 2;

        RequireBytes(data, payload, 5, "COD Scod/SGcod");
        var scod = data[payload];
        var progressionByte = data[payload + 1];
        if (progressionByte > 4)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.CodingStyleUnsupported, $"COD declares an unsupported progression order ({progressionByte}).");
        }

        var layers = ReadUInt16(data, payload + 2);
        if (layers == 0)
        {
            // Table A.14: 1..65535 layers. Zero layers would enumerate no packets at all and
            // hand back a silently blank image for a stream whose data is right there.
            throw new PlumePdfException(JpxDiagnosticCodes.CodingStyleUnsupported, "COD declares zero quality layers; Part 1 requires 1..65535.");
        }

        var mctByte = data[payload + 4];
        if (mctByte > 1)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.CodingStyleUnsupported, $"COD declares an unsupported multiple-component-transform value ({mctByte}).");
        }

        var spcod = ParseSPcod(data, payload + 5, scod, segStart + length);

        position = segStart + length;
        return new JpxCodingStyle
        {
            DecompositionLevels = spcod.DecompositionLevels,
            Xcb = spcod.Xcb,
            Ycb = spcod.Ycb,
            CodeBlockStyle = spcod.Style,
            Reversible53 = spcod.Reversible,
            PrecinctExponentsX = spcod.PpxArr,
            PrecinctExponentsY = spcod.PpyArr,
            Progression = (JpxProgression)progressionByte,
            Layers = layers,
            Mct = mctByte == 1,
            Sop = (scod & 0x02) != 0,
            Eph = (scod & 0x04) != 0,
        };
    }

    private static (int Component, JpxCodingStyle Style) ParseCoc(ReadOnlySpan<byte> data, ref int position, int componentCount, JpxCodingStyle sharedFieldsSource)
    {
        position += 2;
        RequireBytes(data, position, 2, "COC segment length");
        var length = ReadUInt16(data, position);
        var segStart = position;
        RequireSegment(data, segStart, length, "COC");
        var payload = segStart + 2;

        var cfs = ComponentFieldSize(componentCount);
        RequireBytes(data, payload, cfs + 1, "COC Ccoc/Scoc");
        var component = cfs == 1 ? data[payload] : ReadUInt16(data, payload);
        var scocOffset = payload + cfs;
        var scoc = data[scocOffset];

        var spcod = ParseSPcod(data, scocOffset + 1, scoc, segStart + length);

        position = segStart + length;
        var style = new JpxCodingStyle
        {
            DecompositionLevels = spcod.DecompositionLevels,
            Xcb = spcod.Xcb,
            Ycb = spcod.Ycb,
            CodeBlockStyle = spcod.Style,
            Reversible53 = spcod.Reversible,
            PrecinctExponentsX = spcod.PpxArr,
            PrecinctExponentsY = spcod.PpyArr,
            Progression = sharedFieldsSource.Progression,
            Layers = sharedFieldsSource.Layers,
            Mct = sharedFieldsSource.Mct,
            Sop = sharedFieldsSource.Sop,
            Eph = sharedFieldsSource.Eph,
        };
        return (component, style);
    }

    private readonly record struct SPcod(int DecompositionLevels, int Xcb, int Ycb, byte Style, bool Reversible, int[] PpxArr, int[] PpyArr);

    /// <summary>Shared <c>SPcod</c>/<c>SPcoc</c> layout (A.6.1/A.6.2, identical for both — only their preceding <c>Scod</c>/<c>Scoc</c> + component-index prefix differ).</summary>
    private static SPcod ParseSPcod(ReadOnlySpan<byte> data, int offset, byte scodOrScoc, int segmentEnd)
    {
        RequireBytes(data, offset, 5, "COD/COC SPcod");
        if (offset + 5 > segmentEnd)
        {
            throw HeaderInvalid("COD/COC SPcod field runs past the marker segment's declared length.");
        }

        var nl = data[offset];
        if (nl > 32)
        {
            // A.6.1 Table A.15: 0..32 decomposition levels; anything above is not a Part 1 codestream.
            throw new PlumePdfException(JpxDiagnosticCodes.CodingStyleUnsupported, $"COD/COC declares {nl} decomposition levels; Part 1 permits 0..32.");
        }

        var xcbVal = data[offset + 1];
        var ycbVal = data[offset + 2];
        var style = data[offset + 3];
        var waveletByte = data[offset + 4];

        if (waveletByte > 1)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.CodingStyleUnsupported, $"COD/COC declares an unsupported wavelet transform value ({waveletByte}).");
        }

        if (xcbVal > 8 || ycbVal > 8 || xcbVal + ycbVal > 8)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.CodeBlockSizeInvalid, $"COD/COC declares an illegal code-block size (xcb={xcbVal + 2}, ycb={ycbVal + 2}).");
        }

        var xcb = xcbVal + 2;
        var ycb = ycbVal + 2;
        var ppxArr = new int[nl + 1];
        var ppyArr = new int[nl + 1];

        if ((scodOrScoc & 0x01) != 0)
        {
            var precinctsOffset = offset + 5;
            if (precinctsOffset + nl + 1 > segmentEnd)
            {
                throw HeaderInvalid("COD/COC explicit precinct-size list runs past the marker segment's declared length.");
            }

            RequireBytes(data, precinctsOffset, nl + 1, "COD/COC precinct sizes");
            for (var r = 0; r <= nl; r++)
            {
                var b = data[precinctsOffset + r];
                ppxArr[r] = b & 0x0F;
                ppyArr[r] = (b >> 4) & 0x0F;
                if (r > 0 && (ppxArr[r] == 0 || ppyArr[r] == 0))
                {
                    // Table A.21 / B.7: a precinct exponent of 0 is permitted only at r = 0. Above
                    // it, a detail subband's code-block exponent would be min(xcb, PPx − 1) = −1
                    // — no partition exists; and a 1×1-precinct grid over a full-resolution
                    // band is exactly the per-precinct object explosion the decoder must not
                    // attempt.
                    throw new PlumePdfException(JpxDiagnosticCodes.CodingStyleUnsupported, $"COD/COC declares a zero precinct-size exponent at resolution {r}; PPx = PPy = 0 is only permitted at r = 0 (Table A.21).");
                }
            }
        }
        else
        {
            for (var r = 0; r <= nl; r++)
            {
                ppxArr[r] = 15;
                ppyArr[r] = 15;
            }
        }

        return new SPcod(nl, xcb, ycb, style, waveletByte == 1, ppxArr, ppyArr);
    }

    // ---- QCD (A.6.4) / QCC (A.6.5) ----

    private static JpxQuantization ParseQcd(ReadOnlySpan<byte> data, ref int position)
    {
        position += 2;
        RequireBytes(data, position, 2, "QCD segment length");
        var length = ReadUInt16(data, position);
        var segStart = position;
        RequireSegment(data, segStart, length, "QCD");
        var payload = segStart + 2;

        RequireBytes(data, payload, 1, "QCD Sqcd");
        var (style, guardBits, steps) = ParseQuantization(data, payload, segStart + length);

        position = segStart + length;
        return new JpxQuantization { Style = style, GuardBits = guardBits, Steps = steps };
    }

    private static (int Component, JpxQuantization Quant) ParseQcc(ReadOnlySpan<byte> data, ref int position, int componentCount)
    {
        position += 2;
        RequireBytes(data, position, 2, "QCC segment length");
        var length = ReadUInt16(data, position);
        var segStart = position;
        RequireSegment(data, segStart, length, "QCC");
        var payload = segStart + 2;

        var cfs = ComponentFieldSize(componentCount);
        RequireBytes(data, payload, cfs + 1, "QCC Cqcc/Sqcc");
        var component = cfs == 1 ? data[payload] : ReadUInt16(data, payload);
        var (style, guardBits, steps) = ParseQuantization(data, payload + cfs, segStart + length);

        position = segStart + length;
        return (component, new JpxQuantization { Style = style, GuardBits = guardBits, Steps = steps });
    }

    private static (int Style, int GuardBits, (int Exponent, int Mantissa)[] Steps) ParseQuantization(ReadOnlySpan<byte> data, int sOffset, int segmentEnd)
    {
        var sqcd = data[sOffset];
        var style = sqcd & 0x1F;
        var guardBits = (sqcd >> 5) & 0x07;
        if (style > 2)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.CodingStyleUnsupported, $"QCD/QCC declares an unsupported quantisation style ({style}).");
        }

        var stepsOffset = sOffset + 1;
        var stepsBytes = Math.Max(0, segmentEnd - stepsOffset);
        (int, int)[] steps;
        if (style == 0)
        {
            steps = new (int, int)[stepsBytes];
            for (var i = 0; i < stepsBytes; i++)
            {
                var b = data[stepsOffset + i];
                steps[i] = ((b >> 3) & 0x1F, 0);
            }
        }
        else
        {
            var count = stepsBytes / 2;
            steps = new (int, int)[count];
            for (var i = 0; i < count; i++)
            {
                var packed = ReadUInt16(data, stepsOffset + (i * 2));
                steps[i] = ((packed >> 11) & 0x1F, packed & 0x7FF);
            }
        }

        if (steps.Length == 0)
        {
            // A QCD/QCC with no SPqcd entries at all (segment length exactly consumed by Sqcd,
            // or an odd number of step bytes under style 1/2) is not recoverable the way a
            // too-few-detail-subbands QCD is (SubbandQuantisation clamps THAT case to the last
            // transmitted entry, and Parse reports it): with zero entries there is no "last" to
            // clamp to, and every reader of quant.Steps[0] (style 1's reference exponent) or
            // quant.Steps[index] (styles 0/2) would otherwise index an empty array.
            throw HeaderInvalid($"QCD/QCC (style {style}) carries no SPqcd step data.");
        }

        foreach (var (exponent, _) in steps)
        {
            // E-2: M_b = G + ε_b − 1 is the number of bit-planes tier-1 decodes into a 32-bit
            // sign-magnitude coefficient. T.800's field widths allow up to 37, but this decoder
            // represents at most 31 (the sign takes the 32nd) — a larger M_b is refused up
            // front rather than masking the bit-plane shifts downstream. No practical stream
            // approaches it: 16-bit data with two guard bits sits near M_b ≈ 19.
            var mb = guardBits + exponent - 1;
            if (mb > MaxBitPlanes)
            {
                throw new PlumePdfException(JpxDiagnosticCodes.CodingStyleUnsupported, $"QCD/QCC declares guard bits {guardBits} and exponent {exponent} (M_b = {mb}); this decoder supports at most {MaxBitPlanes} coded bit-planes.");
            }
        }

        return (style, guardBits, steps);
    }

    // ---- POC (A.6.6) ----

    private static bool TryParsePoc(ReadOnlySpan<byte> data, ref int position, int componentCount, out JpxProgressionVolume[] entries)
    {
        entries = Array.Empty<JpxProgressionVolume>();
        position += 2;
        RequireBytes(data, position, 2, "POC segment length");
        var length = ReadUInt16(data, position);
        var segStart = position;
        RequireSegment(data, segStart, length, "POC");
        var payload = segStart + 2;
        position = segStart + length;

        var cfs = ComponentFieldSize(componentCount);
        var entrySize = 5 + (2 * cfs);
        var available = (segStart + length) - payload;
        if (available <= 0 || available % entrySize != 0)
        {
            return false;
        }

        var count = available / entrySize;
        var list = new JpxProgressionVolume[count];
        var offset = payload;
        for (var i = 0; i < count; i++)
        {
            var rspoc = data[offset];
            int cspoc = cfs == 1 ? data[offset + 1] : ReadUInt16(data, offset + 1);
            var lyOffset = offset + 1 + cfs;
            var lyepoc = ReadUInt16(data, lyOffset);
            int repoc = data[lyOffset + 2];
            var ceOffset = lyOffset + 3;
            int cepoc = cfs == 1 ? data[ceOffset] : ReadUInt16(data, ceOffset);
            var ppocOffset = ceOffset + cfs;
            var ppocByte = data[ppocOffset];
            if (ppocByte > 4)
            {
                return false;
            }

            // Table A.32 ranges. The END indices are "up to" bounds an encoder may legitimately
            // set past what this image has (255/256 components, 33 resolutions mean "all"), so
            // they are clamped to the image; the START indices and the layer count must be
            // in range outright, and every volume must be non-empty — an out-of-range start
            // would index tile.Components/Resolutions past their ends in the progression
            // iterators.
            if (cfs == 1 && cepoc == 0)
            {
                cepoc = 256; // A.6.6: a one-byte CEpoc of 0 means 256.
            }

            cepoc = Math.Min(cepoc, componentCount);
            repoc = Math.Min(repoc, MaxResolutionLevels);
            if (lyepoc == 0 || rspoc >= repoc || cspoc >= cepoc)
            {
                return false;
            }

            list[i] = new JpxProgressionVolume(rspoc, cspoc, lyepoc, repoc, cepoc, (JpxProgression)ppocByte);
            offset += entrySize;
        }

        entries = list;
        return true;
    }

    // ---- SOT (A.4.2) ----

    private static (int TileIndex, uint Psot, int TPsot, int TNsot) ParseSot(ReadOnlySpan<byte> data, ref int position)
    {
        position += 2;
        RequireBytes(data, position, 2, "SOT segment length");
        var length = ReadUInt16(data, position);
        var segStart = position;
        if (length != 10)
        {
            throw HeaderInvalid($"SOT segment length ({length}) must be exactly 10.");
        }

        RequireSegment(data, segStart, length, "SOT");
        var payload = segStart + 2;

        var tileIndex = ReadUInt16(data, payload);
        var psot = ReadUInt32(data, payload + 2);
        var tpsot = data[payload + 6];
        var tnsot = data[payload + 7];

        position = segStart + length;
        return (tileIndex, psot, tpsot, tnsot);
    }

    // ---- shared segment machinery ----

    /// <summary>Skips a marker segment (<c>TLM</c>/<c>PLM</c>/<c>PLT</c>/<c>CRG</c>/<c>COM</c>, or any other segment this parser has no specific handling for) purely by its declared length.</summary>
    private static void SkipSegment(ReadOnlySpan<byte> data, ref int position)
    {
        position += 2;
        RequireBytes(data, position, 2, "a skipped marker segment's length");
        var length = ReadUInt16(data, position);
        RequireSegment(data, position, length, "a skipped marker segment");
        position += length;
    }

    private static int ComponentFieldSize(int componentCount) => componentCount < 257 ? 1 : 2;

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];

    private static void RequireBytes(ReadOnlySpan<byte> data, int offset, int count, string what)
    {
        if (offset < 0 || count < 0 || offset + count > data.Length)
        {
            throw new TruncatedSignal($"{what} runs past the end of the codestream data");
        }
    }

    private static void RequireSegment(ReadOnlySpan<byte> data, int segStart, int length, string what)
    {
        if (length < 2 || segStart + length > data.Length)
        {
            throw new TruncatedSignal($"{what} segment declares a length ({length}) that runs past the end of the codestream data");
        }
    }

    private static PlumePdfException HeaderInvalid(string message) => new(JpxDiagnosticCodes.HeaderInvalid, message);

    /// <summary>SIZ's <c>Xsiz</c>/<c>Ysiz</c>/etc. fields are 32-bit unsigned per A.5.1, but every downstream geometry computation is signed <c>int</c>; a value above <see cref="int.MaxValue"/> is refused as inconsistent geometry rather than silently overflowing.</summary>
    private static int ToInt32OrGeometryInvalid(uint value, string fieldName)
    {
        if (value > int.MaxValue)
        {
            throw new PlumePdfException(JpxDiagnosticCodes.GeometryInvalid, $"SIZ {fieldName} ({value}) is too large to represent.");
        }

        return (int)value;
    }
}
