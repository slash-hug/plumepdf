using PlumePdf.Filters.Jbig2;
using Xunit;

namespace PlumePdf.Tests.Filters.Jbig2;

/// <summary>
/// <see cref="Jbig2Decoder"/> — segment parsing, page assembly, and the MMR
/// generic-region path (which delegates to the already libtiff-cross-validated
/// <see cref="PlumePdf.Filters.CcittFaxEngine"/>, giving this test genuine cross-implementation
/// grounding rather than only self-consistency: the MMR fixture below is the exact same
/// libtiff-generated G4 strip <c>CcittFaxEngineTests</c> validates, now wrapped in a
/// hand-built JBIG2 segment stream). The arithmetic-coded generic-region/symbol-dictionary/
/// text-region paths are covered by <see cref="Jbig2ArithmeticDecoderTests"/> (the shared MQ
/// core, cross-checked against an independent transcription) and structural tests below; no
/// real-world arithmetic-coded JBIG2 corpus was available in this environment to decode-parity
/// test against (no JBIG2 encoder tool was available either) - <c>Jbig2BitmapMatrixTests</c>
/// (corpus tests) picks this up for real once the pdf.js <c>bitmap-*</c> set is fetched.
/// </summary>
public class Jbig2DecoderTests
{
    private const int Width = 64;
    private const int Height = 40;

    // Same provenance as CcittFaxEngineTests' G4Strip: `tiffcp -c g4` (libtiff 4.7.2) over the
    // documented 64x40 synthetic bilevel raster.
    private static readonly byte[] G4Strip = Convert.FromHexString(
        "23a3197c8e64733688e8be70c8e65cc128888b1c589c0a3383286c825a8820d8319b048919e224986e460cc66610c0b371b811a02081097d9dd4d814206c450891c430318a23a8310a2219c14a7020d0848e19c861e701835408bce55978ce41143866208d6d44444444459ac8d6c9c20323c9e0704508217523b90933cc8673513503725c3723997cd9cd84ec11ccc330e22e2138871060c8a304c399e6308d5916831110e46067414e8336ce81e6d91d022e022fd9bc11e0b7046cc25046983110845088c444f885c88688621d4c4f01026cdcfe46011215d44f830182042c8f91cc8fcac3e323991dd06a20c45882621040e5111c3018cdc2503444ca2290cf0618008008");

    // Same source raster as CcittFaxEngineTests' ExpectedRaster (WhiteIsZero packing: bit 1 =
    // black), reinterpreted here as a bool[,] (true = black) - JBIG2's own fixed convention.
    private static readonly byte[] ExpectedRasterPacked = Convert.FromHexString(
        "466fbe14c1f7cf8600001ff7e0007ff007f001e3ffe1ffe000000ff9fffc00000fff8ffffc00ffff000000000000000000001fc601ff8ff00e1ce31060cf0e0c3fcffe000ff8fe00001fc0003ffe0003000000000000000007f80001ffff803f0003ffff8003ffc007fffc00039e0fc0043c1e0e0be3b861000000000000000000007e0000f003ff00000fe0fff03fff0000400003ff800078007fe01807ffff000007ffffe000007cc3e707c3063c780001e01fe3ffbfff00003fc00007fb9f1800e0000ff007ff00000000000001ff00001ffffc0003fc0003f878001ffe000f5e267c30c790fa700fc000ffff000000000000000001ff3f0004001ff0001f0003ffff0000f80f0180000ffffcffe60000ffff80003f3f6fb08307df783e0e01ffc03fe000f801001dfffc00003fff0e00007f00007fc11fe001ff81fffc00");

    private static bool[,] ExpectedBooleanRaster()
    {
        var rowBytes = (Width + 7) / 8;
        var grid = new bool[Height, Width];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var b = ExpectedRasterPacked[(y * rowBytes) + (x >> 3)];
                grid[y, x] = (b & (0x80 >> (x & 7))) != 0;
            }
        }

        return grid;
    }

    private static void WriteUInt32(List<byte> buffer, uint value)
    {
        buffer.Add((byte)(value >> 24));
        buffer.Add((byte)(value >> 16));
        buffer.Add((byte)(value >> 8));
        buffer.Add((byte)value);
    }

    private static void WriteSegmentHeader(List<byte> buffer, uint segmentNumber, byte type, uint pageAssociation, uint dataLength)
    {
        WriteUInt32(buffer, segmentNumber);
        buffer.Add(type); // flags byte: type in low 6 bits, page-association-size bit (0x40) clear = 1-byte page association.
        buffer.Add(0x00); // referred-to segment count/retention flags: short form, count = 0.
        buffer.Add((byte)pageAssociation);
        WriteUInt32(buffer, dataLength);
    }

    internal static byte[] BuildMmrGenericRegionStream()
    {
        var buffer = new List<byte>();

        // Page info segment (type 48): 19-byte body (width, height, xres, yres, flags, striping).
        var pageInfoBody = new List<byte>();
        WriteUInt32(pageInfoBody, Width);
        WriteUInt32(pageInfoBody, Height);
        WriteUInt32(pageInfoBody, 0);
        WriteUInt32(pageInfoBody, 0);
        pageInfoBody.Add(0x00); // flags: default pixel value 0 (white).
        pageInfoBody.Add(0x00);
        pageInfoBody.Add(0x00); // striping (2 bytes).
        WriteSegmentHeader(buffer, 0, Jbig2SegmentType.PageInfo, pageAssociation: 1, (uint)pageInfoBody.Count);
        buffer.AddRange(pageInfoBody);

        // Immediate generic region segment (type 38): 17-byte region info + 1 flags byte
        // (MMR=1, template=0 - irrelevant when MMR, TPGDON=0) + the MMR (G4) data itself. No
        // AT pixels: T.88 6.2.5.3 only reads AT pixels when MMR=0.
        var regionBody = new List<byte>();
        WriteUInt32(regionBody, Width);
        WriteUInt32(regionBody, Height);
        WriteUInt32(regionBody, 0);
        WriteUInt32(regionBody, 0);
        regionBody.Add(0x00); // combination operator: OR (0).
        regionBody.Add(0x01); // generic region flags: MMR=1.
        regionBody.AddRange(G4Strip);
        WriteSegmentHeader(buffer, 1, Jbig2SegmentType.ImmediateGenericRegion, pageAssociation: 1, (uint)regionBody.Count);
        buffer.AddRange(regionBody);

        return [.. buffer];
    }

    [Fact]
    public void Decode_MmrGenericRegion_MatchesLibtiffCrossValidatedRaster()
    {
        var stream = BuildMmrGenericRegionStream();

        var result = Jbig2Decoder.Decode(stream, globals: null, PdfOptions.Default, null, null);

        Assert.Equal(Width, result.Width);
        Assert.Equal(Height, result.Height);
        var expected = ExpectedBooleanRaster();
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                Assert.True(expected[y, x] == result.Page[y, x], $"pixel ({x},{y}) mismatch.");
            }
        }
    }

    [Fact]
    public void Decode_ThroughFilterAdapter_ProducesDeviceGrayPackedBytes()
    {
        var stream = BuildMmrGenericRegionStream();
        var adapter = new Jbig2FilterAdapter();

        var decoded = adapter.Decode(stream, PdfOptions.Default, null, null);

        var rowBytes = (Width + 7) / 8;
        Assert.Equal(rowBytes * Height, decoded.Length);
        // JBIG2Decode's fixed polarity (ISO 32000-1 §7.4.7): 0 = black, 1 = white - the
        // inverse of ExpectedRasterPacked's own WhiteIsZero-TIFF-sourced convention (1 =
        // black) - so the adapter's output must be the bitwise complement of that fixture.
        for (var i = 0; i < decoded.Length; i++)
        {
            Assert.Equal((byte)~ExpectedRasterPacked[i], decoded[i]);
        }
    }

    [Fact]
    public void Decode_EmptyInput_Throws()
    {
        var ex = Assert.Throws<PlumePdfException>(() => Jbig2Decoder.Decode(Array.Empty<byte>(), globals: null, PdfOptions.Default, null, null));
        Assert.Equal("PLUME3501", ex.Code);
    }

    [Fact]
    public void Decode_UnknownSegmentType_SkippedWithDiagnostic_RegionStillDecodes()
    {
        var stream = BuildMmrGenericRegionStream();
        var withExtra = new List<byte>(stream);

        // Append a well-formed but unrecognized segment type (61 is unassigned) with a tiny
        // body - proves per-segment fallback doesn't derail the rest of the stream.
        WriteSegmentHeader(withExtra, 2, 61, pageAssociation: 1, dataLength: 2);
        withExtra.Add(0xAA);
        withExtra.Add(0xBB);

        var diagnostics = new DiagnosticCollection();
        var result = Jbig2Decoder.Decode(withExtra.ToArray(), globals: null, PdfOptions.Default, diagnostics, null);

        Assert.Equal(Width, result.Width);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3551");
    }

    [Fact]
    public void Decode_SegmentCountExceedsCap_StopsAtCapWithoutHanging()
    {
        var stream = BuildMmrGenericRegionStream();

        // maxSegments=1 - the page-info segment consumes the budget, so the region segment
        // (and therefore the page bitmap it would have produced) never gets processed. The
        // call must still return cleanly (parsing stops, it does not throw or hang) since a
        // resource cap on segment COUNT is a leniency knob, not the pixel-budget guard.
        var segments = Jbig2SegmentReader.ParseSegments(stream, maxSegments: 1);
        Assert.Single(segments);
    }

    // A 96x64 arithmetic-coded generic-region stream (template 0, nominal ATs) produced by
    // jbig2enc 0.32 from a synthetic speckled-text pattern - our own generated content, committed
    // as the realistic-encoder fixture for the fast-path differential below (the suite's other
    // streams are MMR-coded and never enter the arithmetic generic-region path).
    private static readonly byte[] Jbig2EncGenericStream = Convert.FromHexString(
        "00000000300001000000130000006000000040000000000000000001000000000001260001000000C700000060000000" +
        "400000000000000000000003FFFDFF02FEFEFEA7CD3AF89ACB51243CDFCC812074A4D7D337586024B4F97707EFFE2511" +
        "EA5ED1AE0484F5FDC8CE067003A49F7A439ACE4802F7AFAB2BED3876B1865BD6B2134BB653C8482A9CEFA8FF7FF22F01" +
        "E347BBFD7C67D5B22C5F5FA247761B7D9D55E0DB615CAE702A17E1B59864A898F27630A6143C3BEF31D2D1EAF7F1903C" +
        "863AE6357A38A6A0AF4BF83F2A5C264DC84AB80835DDE18DF1029BFB7B3261DEB8FA2919B87F629FFF6CD8A75A7FFFAC");

    [Fact]
    public void Decode_GenericRegion_FastPathMatchesGeneralTemplateWalk()
    {
        // The sliding-window template-0 fast path must be byte-for-byte the general template
        // walk - same context labels in, same arithmetic bit stream out. Decoding the same
        // real-encoder stream both ways pins that.
        var fast = Jbig2Decoder.Decode(Jbig2EncGenericStream, globals: null, Jbig2Decoder.DefaultMaxSegments, Jbig2Decoder.DefaultMaxSymbols, Jbig2Decoder.DefaultMaxDecodedPixels, PdfOptions.Default, null, null, disableGenericFastPath: false);
        var slow = Jbig2Decoder.Decode(Jbig2EncGenericStream, globals: null, Jbig2Decoder.DefaultMaxSegments, Jbig2Decoder.DefaultMaxSymbols, Jbig2Decoder.DefaultMaxDecodedPixels, PdfOptions.Default, null, null, disableGenericFastPath: true);

        Assert.Equal(96, fast.Width);
        Assert.Equal(64, fast.Height);
        Assert.Equal(slow.Width, fast.Width);
        Assert.Equal(slow.Height, fast.Height);
        var painted = 0;
        for (var y = 0; y < fast.Height; y++)
        {
            for (var x = 0; x < fast.Width; x++)
            {
                Assert.Equal(slow.Page[y, x], fast.Page[y, x]);
                if (fast.Page[y, x])
                {
                    painted++;
                }
            }
        }

        Assert.InRange(painted, 1, (96 * 64) - 1); // Anti-vacuity: the stream decodes real ink, not a blank page.
    }

    [Theory]
    [InlineData(3, 5, false)]
    [InlineData(3, 5, true)]
    [InlineData(47, 31, false)]
    [InlineData(61, 33, true)]
    [InlineData(200, 40, false)]
    public void DecodeGenericBitmapCore_ArbitraryPayload_FastPathMatchesGeneralWalk(int width, int height, bool tpgdon)
    {
        // Any byte payload is a decodable arithmetic-coder input (the MQ decoder never
        // rejects), so a deterministic pseudorandom payload exercises both paths across
        // narrow/odd widths where the sliding windows straddle the bitmap edges.
        var payload = new byte[4096];
        var seed = 12345u;
        for (var i = 0; i < payload.Length; i++)
        {
            seed = (seed * 1103515245u) + 12345u;
            payload[i] = (byte)(seed >> 16);
        }

        (int X, int Y)[] nominalAt = [(3, -1), (-3, -1), (2, -2), (-2, -2)];

        var fast = Jbig2Decoder.DecodeGenericBitmapCore(width, height, 0, nominalAt, tpgdon, new Jbig2ArithmeticDecoder(payload, 0, payload.Length), new byte[1 << 16], 1L << 27, disableFastPath: false);
        var slow = Jbig2Decoder.DecodeGenericBitmapCore(width, height, 0, nominalAt, tpgdon, new Jbig2ArithmeticDecoder(payload, 0, payload.Length), new byte[1 << 16], 1L << 27, disableFastPath: true);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Assert.Equal(slow[y, x], fast[y, x]);
            }
        }
    }

    // A page-info segment (default pixel 1) followed by one Huffman-coded immediate text region
    // (flags bit 0 set - the coding jbig2enc cannot produce, which is why no earlier fixture
    // covered it; LuraTech/ABBYY-class compressors emit it). Hand-built so every content
    // segment is skipped as unsupported, leaving only the default-pixel background.
    private static readonly byte[] HuffmanTextRegionDefaultPixel1Stream = Convert.FromHexString(
        "0000000030000100000013000004FB0000067200000000000000000400000000000106000100000053000004FB0000" +
        "06720000000000000000000001000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F2021" +
        "22232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F");

    [Fact]
    public void Decode_AllContentSegmentsSkippedAsUnsupported_RefusesInsteadOfReturningBackground()
    {
        // Before this guard, a stream whose every content segment was skipped
        // (Huffman text regions here) SUCCEEDED with the bare page-default-pixel background -
        // a solid black sheet for default-pixel-1 pages (or black via /Decode [1 0] on
        // default-0 pages) painted over content every conformant viewer renders. Refusing
        // routes the resolver to its paint-nothing PLUME7744 posture instead.
        var diagnostics = new DiagnosticCollection();
        var ex = Assert.Throws<PlumePdfException>(() => Jbig2Decoder.Decode(HuffmanTextRegionDefaultPixel1Stream, globals: null, PdfOptions.Default, diagnostics, null));

        Assert.Equal("PLUME3501", ex.Code);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3557"); // The per-segment Huffman-text-region skip was still recorded first.
    }
}
