using PlumePdf.Filters;
using Xunit;

namespace PlumePdf.Tests.Filters;

/// <summary>
/// <see cref="CcittFaxEngine"/> (ISO 32000-1 §7.4.6, ITU-T T.4/T.6) and its
/// <see cref="CcittFaxFilterAdapter"/> <c>/DecodeParms</c> wiring. Hand-built bit vectors
/// cover the mode-code and run-length tables directly; the G3-1D/G3-2D/G4 fixtures below are
/// real libtiff output (<c>tiffcp -c g3:1d|g3:2d|g4</c>, libtiff 4.7.2, generated once against
/// a 64x40 synthetic bilevel raster with the varying-run-length pattern documented in
/// <see cref="ExpectedRaster"/>'s comment) — a genuine cross-implementation interop check
/// baked into the hermetic lane (generated once and byte-committed with its provenance
/// recorded), not merely a self-consistency round-trip.
/// </summary>
public class CcittFaxEngineTests
{
    private const int Width = 64;
    private const int Height = 40;

    [Fact]
    public void Decode_G4Fixture_MatchesLibtiffSourceRaster()
    {
        var decoded = CcittFaxEngine.Decode(G4Strip, new CcittFaxParameters(K: -1, Columns: Width, Rows: Height, BlackIs1: true), PdfOptions.Default, null, null);
        Assert.Equal(ExpectedRaster, decoded);
    }

    [Fact]
    public void Decode_G3OneDimensionalFixture_MatchesLibtiffSourceRaster()
    {
        var decoded = CcittFaxEngine.Decode(G3OneDStrip, new CcittFaxParameters(K: 0, Columns: Width, Rows: Height, BlackIs1: true), PdfOptions.Default, null, null);
        Assert.Equal(ExpectedRaster, decoded);
    }

    [Fact]
    public void Decode_G3TwoDimensionalFixture_MatchesLibtiffSourceRaster()
    {
        var decoded = CcittFaxEngine.Decode(G3TwoDStrip, new CcittFaxParameters(K: 1, Columns: Width, Rows: Height, BlackIs1: true), PdfOptions.Default, null, null);
        Assert.Equal(ExpectedRaster, decoded);
    }

    [Fact]
    public void Decode_BlackIs1True_InvertsPackedBits()
    {
        var normal = CcittFaxEngine.Decode(G4Strip, new CcittFaxParameters(K: -1, Columns: Width, Rows: Height, BlackIs1: false), PdfOptions.Default, null, null);
        var inverted = CcittFaxEngine.Decode(G4Strip, new CcittFaxParameters(K: -1, Columns: Width, Rows: Height, BlackIs1: true), PdfOptions.Default, null, null);

        Assert.Equal(normal.Length, inverted.Length);
        for (var i = 0; i < normal.Length; i++)
        {
            Assert.Equal((byte)~normal[i], inverted[i]);
        }
    }

    [Fact]
    public void Decode_UnknownRowCount_G4StopsAtDeclaredHeightWithoutEofb()
    {
        // No EOFB in this fixture (a bare TIFF strip) - Rows<=0 with EndOfBlock should still
        // decode every row the data actually contains, then stop at input exhaustion.
        var decoded = CcittFaxEngine.Decode(G4Strip, new CcittFaxParameters(K: -1, Columns: Width, Rows: 0, BlackIs1: true), PdfOptions.Default, null, null);
        Assert.Equal(ExpectedRaster, decoded);
    }

    [Fact]
    public void Decode_TruncatedInput_ReturnsRowsDecodedSoFarWithDiagnostic()
    {
        // Truncation (rather than a bit/byte flip) is the reliable way to exercise
        // decode-as-far-as-possible for 2D coding: G4's rows are referential (each row's
        // decode depends on the previous row's changing elements), so a corrupted bit does
        // not reliably produce an invalid code word soon after - it can just decode to a
        // different-but-still-valid code and silently desync the image from that point on,
        // which would make a byte-exact prefix assertion flaky. Chopping the tail off,
        // instead, guarantees every row that DID decode used only genuine, uncorrupted bits.
        var truncated = G4Strip[..(G4Strip.Length / 2)];

        var diagnostics = new DiagnosticCollection();
        var decoded = CcittFaxEngine.Decode(truncated, new CcittFaxParameters(K: -1, Columns: Width, Rows: Height, BlackIs1: true), PdfOptions.Default, diagnostics, null);

        const int rowBytes = Width / 8;
        Assert.True(decoded.Length >= rowBytes, "at least one full row should have decoded before the input ran out.");
        Assert.True(decoded.Length < ExpectedRaster.Length, "truncation should have stopped decoding before the full image was recovered.");

        // Every row before the last must be byte-identical to the source raster: unlike a
        // corrupted bit (which can silently decode to a different-but-valid code and desync
        // rows that still return "ok"), running out of input can only ever affect the one
        // row where it happened - everything decoded before that row used real, untouched
        // bits. The final row may itself be a best-effort partial (whatever transitions were
        // decoded before EOF, padded with the last run's color out to Columns), so it's
        // deliberately excluded from the byte-exact comparison.
        var fullRowBytes = decoded.Length - rowBytes;
        Assert.Equal(ExpectedRaster.AsSpan(0, fullRowBytes).ToArray(), decoded.AsSpan(0, fullRowBytes).ToArray());
        Assert.Contains(diagnostics, d => d.Code == "PLUME3402");
    }

    [Fact]
    public void Decode_HandCraftedBadModeCode_ReturnsPriorRowWithDiagnostic()
    {
        // Row 0: a single V0 ("1") codes one all-white run the full 8-column width -
        // decodes cleanly. Row 1: "0000000" (seven zero bits) matches no mode code at any
        // length 1-7 (ITU-T T.4 Table 1's shortest is "1", longest 7-bit entries are
        // "0000011"/"0000010") - a deterministic, hand-verified bad code word.
        var bits = PackBits("1" + "0000000");
        var diagnostics = new DiagnosticCollection();

        var decoded = CcittFaxEngine.Decode(bits, new CcittFaxParameters(K: -1, Columns: 8, Rows: 2), PdfOptions.Default, diagnostics, null);

        Assert.Equal([0xFF], decoded); // row 0 only: all-white, BlackIs1=false default -> 1 bits.
        Assert.Contains(diagnostics, d => d.Code == "PLUME3401");
    }

    [Fact]
    public void Decode_Strict_ThrowsInsteadOfDiagnosticOnBadCodeWord()
    {
        var corrupt = (byte[])G4Strip.Clone();
        corrupt[corrupt.Length / 3] ^= 0xFF;

        var options = PdfOptions.Default with { Strict = true };
        var ex = Assert.Throws<PlumePdfException>(() =>
            CcittFaxEngine.Decode(corrupt, new CcittFaxParameters(K: -1, Columns: Width, Rows: Height, BlackIs1: true), options, null, null));
        Assert.StartsWith("PLUME34", ex.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_EmptyInput_ThrowsRatherThanReturningNothing()
    {
        Assert.Throws<PlumePdfException>(() => CcittFaxEngine.Decode([], new CcittFaxParameters(K: -1, Columns: Width, Rows: Height), PdfOptions.Default, null, null));
    }

    [Fact]
    public void Decode_ExceedsPixelCap_Throws()
    {
        var ex = Assert.Throws<PlumePdfException>(() =>
            CcittFaxEngine.Decode(G4Strip, new CcittFaxParameters(K: -1, Columns: Width, Rows: Height), maxDecodedPixels: 10, PdfOptions.Default, null, null));
        Assert.Equal("PLUME3405", ex.Code);
    }

    [Fact]
    public void Adapter_ReadsDecodeParms_AndDecodesCorrectly()
    {
        var adapter = new CcittFaxFilterAdapter();
        var decodeParms = new PdfDictionary
        {
            [PdfName.Get("K")] = PdfNumber.Get(-1),
            [PdfName.Columns] = PdfNumber.Get(Width),
            [PdfName.Get("Rows")] = PdfNumber.Get(Height),
            [PdfName.Get("BlackIs1")] = PdfBoolean.Get(true),
        };

        var decoded = adapter.Decode(G4Strip, decodeParms, PdfOptions.Default, null, null);

        Assert.Equal(ExpectedRaster, decoded);
    }

    [Fact]
    public void Adapter_NoDecodeParms_UsesIsoDefaults()
    {
        var adapter = new CcittFaxFilterAdapter();
        // Default K=0 (MH/1D), Columns=1728: just prove it doesn't throw and produces some
        // bytes for a 1D-appropriate fixture at the right row width.
        var decoded = adapter.Decode(G3OneDStrip, PdfOptions.Default, null, null);
        Assert.NotEmpty(decoded);
    }

    // --- Hand-built vectors against the mode/run-length tables directly --------------------

    [Fact]
    public void Decode_G4_AllWhiteRow_DecodesToAllOneBits()
    {
        // A single all-white row of width 8: reference line is empty (imaginary all-white),
        // so a0=-1, color=white; b1/b2 both equal columns (no reference changes) - V0 mode
        // ("1") moves a1 to b1=8=columns, ending the row with one white run the full width.
        var bits = PackBits("1"); // V0
        var decoded = CcittFaxEngine.Decode(bits, new CcittFaxParameters(K: -1, Columns: 8, Rows: 1), PdfOptions.Default, null, null);
        Assert.Equal([0xFF], decoded); // BlackIs1=false: white = 1 bits.
    }

    [Fact]
    public void Decode_G4_HorizontalMode_SplitsRowIntoTwoRuns()
    {
        // Row width 8: Horizontal mode ("001") + white run 3 ("1000") + black run 5 ("0011").
        var bits = PackBits("001" + "1000" + "0011");
        var decoded = CcittFaxEngine.Decode(bits, new CcittFaxParameters(K: -1, Columns: 8, Rows: 1), PdfOptions.Default, null, null);
        // WWWBBBBB -> bits (1=white): 1110 0000
        Assert.Equal([0b1110_0000], decoded);
    }

    [Fact]
    public void Decode_MH_OneDimensional_TerminatingCodesOnly()
    {
        // Columns=8: white run 3 ("1000"), black run 5 ("0011"). Same pixel pattern as the
        // horizontal-mode test above, but via the pure-1D (MH) path (K=0), which reads the
        // run-length tables directly with no mode codes at all.
        var bits = PackBits("1000" + "0011");
        var decoded = CcittFaxEngine.Decode(bits, new CcittFaxParameters(K: 0, Columns: 8, Rows: 1), PdfOptions.Default, null, null);
        Assert.Equal([0b1110_0000], decoded);
    }

    private static byte[] PackBits(string bits)
    {
        var byteCount = (bits.Length + 7) / 8;
        var result = new byte[byteCount];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
            {
                result[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
        }

        return result;
    }

    // --- libtiff-generated fixtures (provenance below) --------------------------------------

    // Provenance: 64x40 synthetic bilevel raster (deterministic PRNG, seed 42, run lengths
    // 1-5/1-130/1-20 varying by row to exercise terminating, makeup, and mixed-mode codes)
    // packed 1-bpp MSB-first, PhotometricInterpretation=WhiteIsZero (bit 0 = white). Wrapped
    // in a minimal uncompressed classic TIFF and run through `tiffcp -c g4|g3:1d|g3:2d`
    // (libtiff 4.7.2, LIBTIFF Version 4.7.2 - `tiffcp -c g4 baseline.tif g4.tif`, etc.); the
    // strip bytes below are each output's sole StripOffsets/StripByteCounts region, extracted
    // and byte-committed exactly as produced: generated once locally, frozen here - never
    // regenerated by CI or hashed against a rolling tool version.
    private static readonly byte[] ExpectedRaster = Convert.FromHexString(
        "466fbe14c1f7cf8600001ff7e0007ff007f001e3ffe1ffe000000ff9fffc00000fff8ffffc00ffff000000000000000000001fc601ff8ff00e1ce31060cf0e0c3fcffe000ff8fe00001fc0003ffe0003000000000000000007f80001ffff803f0003ffff8003ffc007fffc00039e0fc0043c1e0e0be3b861000000000000000000007e0000f003ff00000fe0fff03fff0000400003ff800078007fe01807ffff000007ffffe000007cc3e707c3063c780001e01fe3ffbfff00003fc00007fb9f1800e0000ff007ff00000000000001ff00001ffffc0003fc0003f878001ffe000f5e267c30c790fa700fc000ffff000000000000000001ff3f0004001ff0001f0003ffff0000f80f0180000ffffcffe60000ffff80003f3f6fb08307df783e0e01ffc03fe000f801001dfffc00003fff0e00007f00007fc11fe001ff81fffc00");

    /// <summary>
    /// Real fax hardware emits FILL (zero bits) + EOL prefixes even ahead of G4
    /// (T.6) data, where the spec defines no EOL — PDFium renders such streams; a strict
    /// decoder fails row 0 and the whole page degrades. Prefix = 4 fill bits + a 12-bit EOL
    /// (bytes 00 01); the decode must be byte-identical to the unprefixed one, with no
    /// diagnostics.
    /// </summary>
    [Fact]
    public void Decode_LeadingFillAndEolBeforeG4Data_MatchesUnprefixedDecode()
    {
        var parms = new CcittFaxParameters(K: -1, Columns: Width, Rows: Height, BlackIs1: true);
        var plain = CcittFaxEngine.Decode(G4Strip, parms, PdfOptions.Default, null, null);

        var prefixed = new byte[G4Strip.Length + 2];
        prefixed[0] = 0x00;
        prefixed[1] = 0x01; // 0000000000000001 = 4 fill zeros + EOL (000000000001).
        G4Strip.CopyTo(prefixed, 2);

        var diagnostics = new DiagnosticCollection();
        var decoded = CcittFaxEngine.Decode(prefixed, parms, PdfOptions.Default, diagnostics, null);

        Assert.Equal(plain, decoded);
        Assert.Empty(diagnostics);
    }

    /// <summary>
    /// A stream whose <c>/EncodedByteAlign</c> flag LIES (set, but rows are not
    /// byte-aligned) desyncs under the flag's per-row alignment skips, yet PDFium renders the
    /// page. The engine decodes spec-compliantly first, probes once without alignment on
    /// failure, keeps the better result, and records <c>PLUME3404</c>. Payload provenance:
    /// a 64x32 G4 strip written once by Pillow 11.3.0/libtiff (white background, black
    /// rectangle x16..48 y8..24 under the fax convention), oracle-verified — alignment
    /// genuinely breaks its row decode, which is what makes this regression non-vacuous.
    /// </summary>
    [Fact]
    public void Decode_LyingEncodedByteAlignFlag_RecoversViaUnalignedRetryWithPlume3404()
    {
        var withoutFlag = CcittFaxEngine.Decode(
            UnalignedG4Strip64x32,
            new CcittFaxParameters(K: -1, Columns: 64, Rows: 32),
            PdfOptions.Default, null, null);

        var diagnostics = new DiagnosticCollection();
        var withLyingFlag = CcittFaxEngine.Decode(
            UnalignedG4Strip64x32,
            new CcittFaxParameters(K: -1, Columns: 64, Rows: 32, EncodedByteAlign: true),
            PdfOptions.Default, diagnostics, null);

        Assert.Equal(withoutFlag, withLyingFlag);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3404");
    }

    private static readonly byte[] UnalignedG4Strip64x32 = Convert.FromHexString("FF350357FFFFFFFFFFE3FE002002");

    /// <summary>
    /// Root-cause regression: white terminating codes 61-63 were transcribed wrong
    /// (61/62 carried codes not in T.4 Table 2; 63 carried 61's code), so a white run of 61
    /// silently decoded as 63 (+2 desync → downstream garbage/overrun) and runs of 62/63 were
    /// BadCodeWord truncations — the exact real-world corpus signature (scanned text hits
    /// 61-63-pixel white gaps constantly). Payload provenance: 128x3 G4 written once by
    /// Pillow 11.3.0/libtiff — row r carries a single fax-black pixel after a white run of
    /// exactly 61, 62, and 63 respectively. Fails wholesale before the table fix.
    /// </summary>
    [Fact]
    public void Decode_WhiteRuns61To63_DecodeExactly()
    {
        var diagnostics = new DiagnosticCollection();
        var decoded = CcittFaxEngine.Decode(
            White61To63Strip128x3,
            new CcittFaxParameters(K: -1, Columns: 128, Rows: 3, BlackIs1: true),
            PdfOptions.Default, diagnostics, null);

        Assert.Empty(diagnostics);
        Assert.Equal(3 * 16, decoded.Length); // 128 columns = 16 bytes/row, 3 full rows.
        for (var row = 0; row < 3; row++)
        {
            var blackAt = 61 + row;
            for (var x = 0; x < 128; x++)
            {
                var bit = (decoded[(row * 16) + (x / 8)] >> (7 - (x % 8))) & 1;
                Assert.True(bit == (x == blackAt ? 1 : 0), $"row {row} x {x}: expected {(x == blackAt ? 1 : 0)} (single black pixel at {blackAt}), got {bit}");
            }
        }
    }

    private static readonly byte[] White61To63Strip128x3 = Convert.FromHexString("264ACAD8ECAD9A80080080");

    private static readonly byte[] G4Strip = Convert.FromHexString(
        "23a3197c8e64733688e8be70c8e65cc128888b1c589c0a3383286c825a8820d8319b048919e224986e460cc66610c0b371b811a02081097d9dd4d814206c450891c430318a23a8310a2219c14a7020d0848e19c861e701835408bce55978ce41143866208d6d44444444459ac8d6c9c20323c9e0704508217523b90933cc8673513503725c3723997cd9cd84ec11ccc330e22e2138871060c8a304c399e6308d5916831110e46067414e8336ce81e6d91d022e022fd9bc11e0b7046cc25046983110845088c444f885c88688621d4c4f01026cdcfe46011215d44f830182042c8f91cc8fcac3e323991dd06a20c45882621040e5111c3018cdc2503444ca2290cf0618008008");

    private static readonly byte[] G3OneDStrip = Convert.FromHexString(
        "0011d46f8e639da1d3f8639b9de38008c1072d02d800e0d0e012c3e0008811c309c006c12008382e003b350011838e61205b001bae7a3859e6eeecdc005c570a18903a000a075026b800ecd4007054e0450800740ce187e001c062a9de17000e2b78f2c439c43d7da001d9a800d655b384001103c0fc0e0035a101750011d90127cc338009706e5c004737ecdec3bf3870e000757316028e0e0029c51828f39800c68ab168140056040011819ea15c00741dbd03d0006d8e87709f73bef86eacc7438008f4cb405ea0015810005c834189a9800e804547ec007f18043857c7001a818ac9c80047c731fb59e18e63bc3c87001f0b310333a001443860403800dc9c758988006052027060e0");

    private static readonly byte[] G3TwoDStrip = Convert.FromHexString(
        "0018ea37c731ced0e9fc31cdcef1c0042222c71627028c0078343804b0f80021041b063001d824010705c004224986e0031838e61205b0011b8dc08d0104084beceea6c0a003715c286240e80021447506214007b350011c14a7020d08007a0670c3f0008e0306a81179800f15bc79621ce21ebed0008444444445800eb2ad9c20008191e4f03800eb4202ea00223b90933cc867350019706e5c0044732f9b39b09d823998661c007ab98b01470700103228c130e67800e34558b40a0021110e0031819ea15c0047419b6740f001db1d0ee13ee77df0dd598e870010234c1888422844600358100045c88688621d4c007a01151fb0011fc8c02242ba800ea062b27200111f23991f9587c647323ba0d4007e1662066740022888e180c6003b938eb1310009114867830c");
}
