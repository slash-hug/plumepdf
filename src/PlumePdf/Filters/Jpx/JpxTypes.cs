namespace PlumePdf.Filters.Jpx;

// The in-house JPEG 2000 decoder's shared data model.
//
// This file is the contract every codec stage reads and writes, plus the result type the
// resolver and the RasterImage facade consume. Field names follow ITU-T T.800 (08/2002) so a
// reader can find each one in the standard by name (Xsiz/YOsiz, Rsiz, Lblock, Sqcd …); every
// clause reference below is to T.800. Transcribed from the standard's published structure only
// (the clean-room policy in AGENTS.md).

/// <summary>Every diagnostic code the JPX decoder mints. Minted here so <c>scripts/check-error-docs.sh</c> sees the literal from one place, so nothing else has to touch <c>docs/errors/</c> separately.</summary>
internal static class JpxDiagnosticCodes
{
    /// <summary>Not a JPEG 2000 stream: neither a JP2 signature box nor <c>SOC</c>+<c>SIZ</c>.</summary>
    public const string NotJpeg2000 = "PLUME3700";

    /// <summary>Codestream truncated / a marker segment's length exceeds the data — every complete packet decoded, image partial.</summary>
    public const string Truncated = "PLUME3701";

    /// <summary>Part 2 codestream (<c>Rsiz</c> bit 15) or a Part 2 marker — refused.</summary>
    public const string Part2 = "PLUME3702";

    /// <summary><c>RGN</c> (region of interest) present — refused.</summary>
    public const string RoiUnsupported = "PLUME3703";

    /// <summary><c>PPM</c>/<c>PPT</c> packed packet headers — refused.</summary>
    public const string PackedHeadersUnsupported = "PLUME3704";

    /// <summary>Component precision above 16 bits — refused.</summary>
    public const string PrecisionUnsupported = "PLUME3705";

    /// <summary>Image/tile geometry inconsistent (<c>SIZ</c> origin ≥ size, tile grid not covering the image, zero-size tile).</summary>
    public const string GeometryInvalid = "PLUME3706";

    /// <summary>Missing, duplicate or misplaced main-header marker (<c>COD</c>/<c>QCD</c> absent, <c>SIZ</c> not first, <c>COD</c> in a non-first tile-part).</summary>
    public const string HeaderInvalid = "PLUME3707";

    /// <summary>Tile-part sequencing broken (<c>TPsot</c> out of order, <c>TNsot</c> exceeded, <c>Psot</c> inconsistent) — consistent parts decoded.</summary>
    public const string TilePartSequence = "PLUME3708";

    /// <summary>Packet header malformed — remaining packets of the tile skipped.</summary>
    public const string PacketHeaderInvalid = "PLUME3709";

    /// <summary>Code-block segments disagree with tier-2 signalling (fewer codeword segments than the signalled passes promised, or a segment's declared length runs past its tile-part) — block truncated at the last good pass. Never an MQ/raw over-read (T.800 C.3.4), and never passes signalled beyond <c>M_b</c>'s schedule (skipped silently, as OpenJPEG does).</summary>
    public const string CodeBlockTruncated = "PLUME3710";

    /// <summary>Segmentation symbol mismatch after a cleanup pass — block kept, flagged.</summary>
    public const string SegmentationSymbolMismatch = "PLUME3711";

    /// <summary>Unsupported <c>SPcod</c> wavelet, multiple-component-transform value, or quantisation style.</summary>
    public const string CodingStyleUnsupported = "PLUME3712";

    /// <summary><c>POC</c> progression volume malformed — ignored, <c>COD</c> progression used.</summary>
    public const string PocInvalid = "PLUME3713";

    /// <summary>JP2 box structure malformed (bad <c>LBox</c>/<c>XLBox</c>, missing <c>jp2c</c>, <c>ihdr</c> disagrees with <c>SIZ</c>) — codestream decoded when found.</summary>
    public const string Jp2BoxInvalid = "PLUME3714";

    /// <summary><c>colr</c> METH/EnumCS unrecognised, ICC component count not 1/3/4, or <c>pclr</c>/<c>cmap</c>/<c>cdef</c> inconsistent — box ignored, colour by component count.</summary>
    public const string ColourBoxInvalid = "PLUME3715";

    /// <summary>Fragment table / multiple codestreams — the first contiguous codestream decoded.</summary>
    public const string MultipleCodestreams = "PLUME3716";

    /// <summary>Illegal code-block size (<c>xcb</c>/<c>ycb</c> above 10 or their sum above 12).</summary>
    public const string CodeBlockSizeInvalid = "PLUME3717";

    /// <summary>Reference grid exceeds <see cref="PdfOptions.MaxImagePixels"/> (checked on <c>SIZ</c> before any allocation).</summary>
    public const string ImageTooLarge = "PLUME3718";
}

/// <summary>Per-component <c>SIZ</c> facts (A.5.1): precision in bits, signedness, and the horizontal/vertical sub-sampling factors.</summary>
internal readonly record struct JpxComponentInfo(int Precision, bool Signed, int XRsiz, int YRsiz);

/// <summary>Progression order (Table A.16).</summary>
internal enum JpxProgression : byte
{
    /// <summary>Layer–resolution–component–position.</summary>
    Lrcp = 0,

    /// <summary>Resolution–layer–component–position.</summary>
    Rlcp = 1,

    /// <summary>Resolution–position–component–layer.</summary>
    Rpcl = 2,

    /// <summary>Position–component–resolution–layer.</summary>
    Pcrl = 3,

    /// <summary>Component–position–resolution–layer.</summary>
    Cprl = 4,
}

/// <summary>The <c>SIZ</c> marker segment (A.5.1): reference grid, tile grid, capabilities, components.</summary>
internal sealed class JpxSiz
{
    public required int Xsiz { get; init; }

    public required int Ysiz { get; init; }

    public required int XOsiz { get; init; }

    public required int YOsiz { get; init; }

    public required int XTsiz { get; init; }

    public required int YTsiz { get; init; }

    public required int XTOsiz { get; init; }

    public required int YTOsiz { get; init; }

    public required ushort Rsiz { get; init; }

    public required JpxComponentInfo[] Components { get; init; }

    /// <summary>Number of tiles across (B-5).</summary>
    public int NumXTiles => (Xsiz - XTOsiz + XTsiz - 1) / XTsiz;

    /// <summary>Number of tiles down (B-5).</summary>
    public int NumYTiles => (Ysiz - YTOsiz + YTsiz - 1) / YTsiz;
}

/// <summary>Coding style for one component after <c>COD</c>/<c>COC</c> precedence (A.6.1, A.6.2).</summary>
internal sealed class JpxCodingStyle
{
    /// <summary><c>N_L</c>.</summary>
    public required int DecompositionLevels { get; init; }

    /// <summary>Code-block width exponent <c>xcb</c> (before the B.7 precinct clamp).</summary>
    public required int Xcb { get; init; }

    /// <summary>Code-block height exponent <c>ycb</c> (before the B.7 precinct clamp).</summary>
    public required int Ycb { get; init; }

    /// <summary>The six <c>SPcod</c> code-block style flags (Table A.19): bypass 0x01, reset 0x02, termall 0x04, vertically causal 0x08, predictable termination 0x10, segmentation symbols 0x20.</summary>
    public required byte CodeBlockStyle { get; init; }

    /// <summary><see langword="true"/> for the 5/3 reversible wavelet (transformation byte 1), <see langword="false"/> for 9/7 irreversible (0).</summary>
    public required bool Reversible53 { get; init; }

    /// <summary>Precinct width exponents <c>PPx</c> per resolution level, length <c>N_L + 1</c>; every entry 15 when <c>Scod</c> bit 0 is clear (default precincts).</summary>
    public required int[] PrecinctExponentsX { get; init; }

    /// <summary>Precinct height exponents <c>PPy</c> per resolution level, length <c>N_L + 1</c>; every entry 15 when <c>Scod</c> bit 0 is clear.</summary>
    public required int[] PrecinctExponentsY { get; init; }

    public required JpxProgression Progression { get; init; }

    /// <summary>Number of quality layers.</summary>
    public required int Layers { get; init; }

    /// <summary>Multiple component transform flag (<c>SGcod</c>): RCT with 5/3, ICT with 9/7, on components 0–2.</summary>
    public required bool Mct { get; init; }

    /// <summary><c>SOP</c> marker segments may precede packets (<c>Scod</c> bit 1).</summary>
    public required bool Sop { get; init; }

    /// <summary><c>EPH</c> markers terminate packet headers (<c>Scod</c> bit 2).</summary>
    public required bool Eph { get; init; }
}

/// <summary>Quantisation for one component after <c>QCD</c>/<c>QCC</c> precedence (A.6.4, A.6.5).</summary>
internal sealed class JpxQuantization
{
    /// <summary><c>Sqcd</c> low five bits: 0 none (reversible), 1 scalar derived, 2 scalar expounded.</summary>
    public required int Style { get; init; }

    /// <summary>Guard bits <c>G</c> (<c>Sqcd</c> bits 5–7).</summary>
    public required int GuardBits { get; init; }

    /// <summary>Per-subband (<c>ε_b</c>, <c>μ_b</c>) in subband order; exactly one entry for style 1 (scalar derived), where <c>ε_b = ε_0 − N_L + n_b</c> is derived per subband (E-5).</summary>
    public required (int Exponent, int Mantissa)[] Steps { get; init; }
}

/// <summary>One <c>POC</c> progression change (A.6.6).</summary>
internal readonly record struct JpxProgressionVolume(int RSpoc, int CSpoc, int LYEpoc, int REpoc, int CEpoc, JpxProgression Progression);

/// <summary>One codeword segment of a code-block: a byte range into the tile-part data at <see cref="TilePartIndex"/> carrying <see cref="Passes"/> coding passes (B.10.7).</summary>
internal readonly record struct JpxSegmentRef(int TilePartIndex, int Offset, int Length, int Passes);

/// <summary>A code-block (B.7): its bounds within the subband, the tier-2 state tier-2 accumulates across layers, and the tier-1 output.</summary>
internal sealed class JpxCodeBlock
{
    public required int X0 { get; init; }

    public required int Y0 { get; init; }

    public required int X1 { get; init; }

    public required int Y1 { get; init; }

    /// <summary>B.10.7.1 length-indicator state; starts at 3.</summary>
    public int Lblock { get; set; } = 3;

    /// <summary>Number of missing most-significant bit-planes <c>P</c> signalled at first inclusion (B.10.5).</summary>
    public int ZeroBitPlanes { get; set; }

    /// <summary>Whether the block has been included in any packet yet.</summary>
    public bool Included { get; set; }

    /// <summary>Total coding passes signalled so far across all layers.</summary>
    public int PassesSignalled { get; set; }

    /// <summary>Codeword segments in decode order — appended by the packet decoder, consumed by the block decoder.</summary>
    public List<JpxSegmentRef> Segments { get; } = new();

    /// <summary>Sign-magnitude coefficients, <c>(X1−X0)·(Y1−Y0)</c> row-major, written by the block decoder.</summary>
    public int[]? Coefficients { get; set; }

    /// <summary>How many bit-planes the block decoder actually decoded (drives the E.1.1.2 reconstruction rounding).</summary>
    public int DecodedPlanes { get; set; }

    public int Width => X1 - X0;

    public int Height => Y1 - Y0;
}

/// <summary>A precinct partition cell within one subband (B.6), holding its code-blocks and the two tag trees (B.10.2) tier-2 resumes across layers.</summary>
internal sealed class JpxPrecinct
{
    public required int X0 { get; init; }

    public required int Y0 { get; init; }

    public required int X1 { get; init; }

    public required int Y1 { get; init; }

    /// <summary>Code-blocks in raster order, <see cref="CodeBlocksWide"/> per row.</summary>
    public required JpxCodeBlock[] CodeBlocks { get; init; }

    public required int CodeBlocksWide { get; init; }

    /// <summary>Inclusion tag tree; created lazily by the packet decoder at the first packet touching this precinct.</summary>
    public JpxTagTree? Inclusion { get; set; }

    /// <summary>Zero-bit-planes tag tree; created lazily by the packet decoder.</summary>
    public JpxTagTree? ZeroBitPlanes { get; set; }
}

/// <summary>One subband (B.5): orientation, coordinates in its own subband coordinate system, quantisation facts and the coefficient plane the block decoder fills.</summary>
internal sealed class JpxSubband
{
    /// <summary>0 = LL, 1 = HL, 2 = LH, 3 = HH.</summary>
    public required int Orientation { get; init; }

    public required int X0 { get; init; }

    public required int Y0 { get; init; }

    public required int X1 { get; init; }

    public required int Y1 { get; init; }

    /// <summary><c>ε_b</c> after style-1 derivation.</summary>
    public required int Epsilon { get; init; }

    /// <summary><c>μ_b</c>.</summary>
    public required int Mu { get; init; }

    /// <summary><c>M_b = G + ε_b − 1</c> (E-2).</summary>
    public required int Mb { get; init; }

    /// <summary>log2 subband gain: 0 (LL), 1 (HL, LH), 2 (HH) — the <c>gain_b</c> term of <c>R_b</c> (E-4).</summary>
    public required int Gain { get; init; }

    public required JpxPrecinct[] Precincts { get; init; }

    /// <summary>Dequantised coefficients for the whole subband, <c>(X1−X0)·(Y1−Y0)</c> row-major; filled before the inverse wavelet runs.</summary>
    public int[]? Coefficients { get; set; }
}

/// <summary>One resolution level of a tile-component (B.5): bounds, precinct exponents after the B.7 clamp source, precinct grid, and its subbands (one LL for <c>r = 0</c>, HL/LH/HH otherwise).</summary>
internal sealed class JpxResolution
{
    /// <summary><c>r</c>, 0 = lowest.</summary>
    public required int Level { get; init; }

    public required int X0 { get; init; }

    public required int Y0 { get; init; }

    public required int X1 { get; init; }

    public required int Y1 { get; init; }

    /// <summary>Precinct width exponent at this level (from <see cref="JpxCodingStyle.PrecinctExponentsX"/>).</summary>
    public required int PPx { get; init; }

    /// <summary>Precinct height exponent at this level.</summary>
    public required int PPy { get; init; }

    public required int PrecinctsWide { get; init; }

    public required int PrecinctsHigh { get; init; }

    public required JpxSubband[] Subbands { get; init; }
}

/// <summary>One component of one tile (B.3): bounds on the component's own grid, its coding/quantisation after precedence, and its resolution levels.</summary>
internal sealed class JpxTileComponent
{
    public required int Component { get; init; }

    public required int X0 { get; init; }

    public required int Y0 { get; init; }

    public required int X1 { get; init; }

    public required int Y1 { get; init; }

    public required JpxCodingStyle Coding { get; init; }

    public required JpxQuantization Quant { get; init; }

    public required JpxResolution[] Resolutions { get; init; }
}

/// <summary>One tile (B.3): bounds on the reference grid, its components, and the progression volumes in force.</summary>
internal sealed class JpxTile
{
    public required int Index { get; init; }

    public required int X0 { get; init; }

    public required int Y0 { get; init; }

    public required int X1 { get; init; }

    public required int Y1 { get; init; }

    public required JpxTileComponent[] Components { get; init; }

    /// <summary>Tile-part <c>POC</c> volumes if any, else the main-header ones, else empty (the <c>COD</c> progression applies).</summary>
    public required JpxProgressionVolume[] ProgressionVolumes { get; init; }
}

/// <summary>One tile-part (A.4.2): which tile, its index and declared count, and the bitstream slice from just after <c>SOD</c> to the next <c>SOT</c>/<c>EOC</c>.</summary>
internal sealed class JpxTilePart
{
    public required int TileIndex { get; init; }

    /// <summary><c>TPsot</c>.</summary>
    public required int TPsot { get; init; }

    /// <summary><c>TNsot</c>; 0 = number of tile-parts unknown (legal).</summary>
    public required int TNsot { get; init; }

    public required ReadOnlyMemory<byte> Data { get; init; }

    /// <summary><c>POC</c> found in this tile-part's header, if any.</summary>
    public JpxProgressionVolume[]? Poc { get; set; }
}

/// <summary>Everything the main header and tile-part headers say (Annex A), after precedence: the input to geometry and tier-2.</summary>
internal sealed class JpxCodestreamHeader
{
    public required JpxSiz Siz { get; init; }

    /// <summary>Main-header coding style per component (<c>COC</c> over <c>COD</c>).</summary>
    public required JpxCodingStyle[] Coding { get; init; }

    /// <summary>Main-header quantisation per component (<c>QCC</c> over <c>QCD</c>).</summary>
    public required JpxQuantization[] Quant { get; init; }

    public required JpxProgressionVolume[] MainPoc { get; init; }

    /// <summary>Tile-parts in <c>TPsot</c> order per tile, in codestream order overall.</summary>
    public required List<JpxTilePart> TileParts { get; init; }

    /// <summary>Per-tile overrides from a first tile-part header's <c>COD</c>/<c>COC</c>/<c>QCD</c>/<c>QCC</c>, keyed by tile index.</summary>
    public Dictionary<int, (JpxCodingStyle[] Coding, JpxQuantization[] Quant)> TileOverrides { get; } = new();
}

/// <summary>What the JP2 wrapper said about colour (Annex I): enumerated space, ICC by component count, palette, channel map, alpha, and display resolution. All optional; a raw codestream has none of it.</summary>
internal sealed class JpxColourInfo
{
    /// <summary><c>colr</c> METH 1 EnumCS (16 sRGB, 17 greyscale, 18 sYCC, 12 CMYK) when present and recognised.</summary>
    public int? EnumeratedColourSpace { get; set; }

    /// <summary>For <c>colr</c> METH 2/3: the number of colour channels the ICC profile describes (1, 3 or 4) — mapped by count, never applied.</summary>
    public int? IccComponentCount { get; set; }

    /// <summary><c>pclr</c> as read (informational — planes are already expanded): entry bit depth, entry count, and the raw table bytes.</summary>
    public (int Depth, int Entries, byte[] Table)? Palette { get; set; }

    /// <summary><c>cmap</c> channel mapping as read (informational).</summary>
    public int[]? ChannelMap { get; set; }

    /// <summary>Index into <see cref="JpxImage.Planes"/> of the <c>cdef</c> opacity channel, if any.</summary>
    public int? AlphaChannelIndex { get; set; }

    /// <summary><c>cdef</c> Typ 2: colour is premultiplied by the opacity channel.</summary>
    public bool AlphaPremultiplied { get; set; }

    /// <summary>Display (<c>resd</c>) else capture (<c>resc</c>) resolution in pixels per metre.</summary>
    public (double X, double Y)? PixelsPerMetre { get; set; }
}

/// <summary>
/// One decoded component plane at native byte width (the parity-or-better memory posture):
/// <see cref="Samples8"/> for precision ≤ 8, <see cref="Samples16"/> otherwise; signed components are
/// stored two's-complement in that width and left signed (T.800 G.1.2 shifts only unsigned components).
/// </summary>
internal sealed class JpxPlane
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Precision { get; init; }

    public required bool Signed { get; init; }

    public byte[]? Samples8 { get; init; }

    public ushort[]? Samples16 { get; init; }

    /// <summary>Full-scale maximum for an unsigned sample of this precision, <c>2^p − 1</c>.</summary>
    public int MaxValue => (1 << Precision) - 1;

    /// <summary>Raw sample at <paramref name="index"/> (row-major), sign-extended when <see cref="Signed"/>.</summary>
    public int Sample(int index)
    {
        var raw = Samples8 is not null ? Samples8[index] : Samples16![index];
        if (Signed && (raw & (1 << (Precision - 1))) != 0)
        {
            return raw - (1 << Precision);
        }

        return raw;
    }
}

/// <summary>
/// The decoder's result: colour-space-ready planes at reference-grid size (palette expanded,
/// subsampled components upsampled), the colour facts from the wrapper, and the sample conversions every
/// consumer shares. Signed planes are left signed here; <see cref="SampleUnsigned"/> shifts.
/// </summary>
internal sealed class JpxImage
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required JpxPlane[] Planes { get; init; }

    public required JpxColourInfo Colour { get; init; }

    /// <summary>Planes minus the <c>cdef</c> opacity channel — the count every ISO 32000-1 §7.4.9 colour rule uses.</summary>
    public int ColourChannelCount => Planes.Length - (Colour.AlphaChannelIndex is null ? 0 : 1);

    /// <summary>Sample at <paramref name="index"/> of <paramref name="plane"/> as an unsigned value in <c>[0, 2^p − 1]</c>: signed components are shifted by <c>+2^(p−1)</c>.</summary>
    public int SampleUnsigned(int plane, int index)
    {
        var p = Planes[plane];
        var v = p.Sample(index);
        return p.Signed ? v + (1 << (p.Precision - 1)) : v;
    }

    /// <summary>
    /// Interleaved 8-bit samples, row-major, colour channels in plane order (alpha dropped when
    /// <paramref name="dropAlpha"/>). Every precision is reduced by the full-scale rescale
    /// <c>(v·255 + max/2) / max</c> with <c>max = 2^p − 1</c> — at 16-bit this differs from PNG/TIFF's
    /// truncating <c>raw &gt;&gt; 8</c> at the rounding boundaries (raw 255 → 1 here, 0 there; 511 → 2 vs 1;
    /// 32768 → 128 in both), a recorded, deliberate divergence.
    /// </summary>
    public byte[] ToInterleaved8Bit(bool dropAlpha)
    {
        var planes = SelectPlanes(dropAlpha);
        var pixels = Width * Height;
        var result = new byte[pixels * planes.Length];
        for (var c = 0; c < planes.Length; c++)
        {
            var plane = Planes[planes[c]];
            var max = plane.MaxValue;
            var half = plane.Signed ? 1 << (plane.Precision - 1) : 0;
            for (var i = 0; i < pixels; i++)
            {
                var v = plane.Sample(i) + half;
                result[(i * planes.Length) + c] = (byte)Rescale(v, max, 255);
            }
        }

        return result;
    }

    /// <summary>Interleaved 16-bit big-endian samples, row-major, full-scale rescaled to <c>[0, 65535]</c> (the same rounding rule as <see cref="ToInterleaved8Bit"/>).</summary>
    public byte[] ToInterleaved16BitBigEndian(bool dropAlpha)
    {
        var planes = SelectPlanes(dropAlpha);
        var pixels = Width * Height;
        var result = new byte[pixels * planes.Length * 2];
        for (var c = 0; c < planes.Length; c++)
        {
            var plane = Planes[planes[c]];
            var max = plane.MaxValue;
            var half = plane.Signed ? 1 << (plane.Precision - 1) : 0;
            for (var i = 0; i < pixels; i++)
            {
                var v = Rescale(plane.Sample(i) + half, max, 65535);
                var o = ((i * planes.Length) + c) * 2;
                result[o] = (byte)(v >> 8);
                result[o + 1] = (byte)v;
            }
        }

        return result;
    }

    /// <summary>Full-scale rescale of <paramref name="value"/> in <c>[0, max]</c> to <c>[0, target]</c>, rounding half up.</summary>
    internal static int Rescale(int value, int max, int target) =>
        Math.Clamp((int)(((long)value * target + (max / 2)) / max), 0, target);

    private int[] SelectPlanes(bool dropAlpha)
    {
        if (!dropAlpha || Colour.AlphaChannelIndex is not { } alpha)
        {
            var all = new int[Planes.Length];
            for (var i = 0; i < all.Length; i++)
            {
                all[i] = i;
            }

            return all;
        }

        var selected = new int[Planes.Length - 1];
        var n = 0;
        for (var i = 0; i < Planes.Length; i++)
        {
            if (i != alpha)
            {
                selected[n++] = i;
            }
        }

        return selected;
    }
}

/// <summary>What the JP2 wrapper resolved to (Annex I): the contiguous codestream, the colour facts, and <c>ihdr</c>'s component count / bit depth for the cross-check against <c>SIZ</c>.</summary>
internal sealed class Jp2Container
{
    public required ReadOnlyMemory<byte> Codestream { get; init; }

    public required JpxColourInfo Colour { get; init; }

    /// <summary><c>ihdr</c> NC and BPC as read, for the <c>PLUME3714</c> disagreement check.</summary>
    public (int NC, int BPC)? Ihdr { get; init; }
}
