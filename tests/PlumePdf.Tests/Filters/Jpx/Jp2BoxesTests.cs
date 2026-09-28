using System.Text;
using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// The full <see cref="Jp2Boxes"/> box-family rule set —
/// <c>ftyp</c>, <c>ihdr</c>/<c>bpcc</c> consistency, every <c>colr</c> METH shape (including
/// multiple <c>colr</c> boxes), <c>pclr</c>/<c>cmap</c>/<c>cdef</c>, <c>res </c>'s
/// <c>resd</c>-over-<c>resc</c> precedence and its <c>N/D·10^E</c> formula, malformed
/// <c>LBox</c>/<c>XLBox</c>, and the fragment-table/second-codestream case — on hand-built box
/// sequences (per code) and the committed hand-built fixtures the matrix was built for exactly
/// this purpose (<c>two-colr</c>, <c>res-metadata</c>, <c>pclr-cmap</c>, <c>premultiplied-alpha</c>,
/// <c>cmyk-enumcs12</c>). Earlier regression tests for <c>pclr</c> bounds
/// validation (the two tests below the class summary line) predate this pass and are kept as-is.
/// </summary>
public class Jp2BoxesTests
{
    [Fact]
    public void Parse_PclrDeclaringMoreEntriesThanTheBodyCanHold_ReportsDeviationAndIgnoresThePalette()
    {
        // A regression test for the bug that ParsePclr read NE (entries) and NPC
        // (columns) and then walked `entries * columns` source bytes with no check that the box
        // body actually carried that many — a pclr box declaring 65,535 entries and 4 columns in
        // a 15-byte body (this exact shape) made JpxImageDecoder.Decode throw a bare
        // IndexOutOfRangeException (after already allocating entries*columns*bytesPerChannel).
        var pclrBody = new byte[15];
        pclrBody[0] = 0xFF; // NE (uint16) = 65535
        pclrBody[1] = 0xFF;
        pclrBody[2] = 0x04; // NPC (columns) = 4
        pclrBody[3] = 0x07; // Bi for column 0: depth 8
        pclrBody[4] = 0x07; // column 1
        pclrBody[5] = 0x07; // column 2
        pclrBody[6] = 0x07; // column 3
        // bytes 7..14 are filler -- nowhere near the ~262,140 bytes 65,535 entries x 4 columns
        // x 1 byte/channel would actually need.

        var pclrBox = WriteBox("pclr", pclrBody);
        var jp2hBox = WriteBox("jp2h", pclrBox);
        var jp2cBox = WriteBox("jp2c", [0xAA, 0xBB, 0xCC, 0xDD]);
        var file = new List<byte>();
        file.AddRange(jp2hBox);
        file.AddRange(jp2cBox);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(file.ToArray(), PdfOptions.Default, diagnostics);

        Assert.Null(container.Colour.Palette);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid);
    }

    [Fact]
    public void Parse_PclrTooShortForItsOwnColumnCount_ReportsDeviationAndIgnoresThePalette()
    {
        // The narrower case: the body is too short even for the NPC column bit-depth bytes
        // (Bi), which ParsePclr indexes before it ever reaches entry data.
        byte[] pclrBody = [0x00, 0x02, 0x05]; // NE=2, NPC=5, but no Bi bytes at all follow.

        var pclrBox = WriteBox("pclr", pclrBody);
        var jp2hBox = WriteBox("jp2h", pclrBox);
        var jp2cBox = WriteBox("jp2c", [0xAA, 0xBB, 0xCC, 0xDD]);
        var file = new List<byte>();
        file.AddRange(jp2hBox);
        file.AddRange(jp2cBox);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(file.ToArray(), PdfOptions.Default, diagnostics);

        Assert.Null(container.Colour.Palette);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid);
    }

    // ---- ftyp ----

    [Fact]
    public void Parse_FtypDeclaresJp2Brand_NoDeviationReported()
    {
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7));

        var diagnostics = new DiagnosticCollection();
        Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        // J2kBuilder.Jp2 writes a conforming ftyp (Brand 'jp2 ') — no complaint about it.
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("ftyp"));
    }

    [Fact]
    public void Parse_FtypDeclaresUnrelatedBrand_ReportsDeviationButStillDecodesCodestream()
    {
        var badFtyp = WriteBox("ftyp", Encoding.ASCII.GetBytes("jpx ") // Brand
            .Concat(new byte[4]) // MinV
            .Concat(Encoding.ASCII.GetBytes("jpx ")) // CL[0] -- still not 'jp2 '
            .ToArray());
        var jp2hBox = WriteBox("jp2h", J2kBuilder.Ihdr(16, 16, 1, 7));
        var jp2cBox = WriteBox("jp2c", [0xAA, 0xBB, 0xCC, 0xDD]);
        var file = new List<byte>();
        file.AddRange(badFtyp);
        file.AddRange(jp2hBox);
        file.AddRange(jp2cBox);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(file.ToArray(), PdfOptions.Default, diagnostics);

        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }, container.Codestream.ToArray());
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.Jp2BoxInvalid && d.Message.Contains("ftyp"));
    }

    [Fact]
    public void Parse_FtypTooShortForBrandAndMinV_ReportsDeviation()
    {
        var shortFtyp = WriteBox("ftyp", [0x6A, 0x70, 0x32, 0x20]); // Brand only, no MinV.
        var jp2hBox = WriteBox("jp2h", J2kBuilder.Ihdr(16, 16, 1, 7));
        var jp2cBox = WriteBox("jp2c", [0xAA, 0xBB, 0xCC, 0xDD]);
        var file = new List<byte>();
        file.AddRange(shortFtyp);
        file.AddRange(jp2hBox);
        file.AddRange(jp2cBox);

        var diagnostics = new DiagnosticCollection();
        Jp2Boxes.Parse(file.ToArray(), PdfOptions.Default, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.Jp2BoxInvalid && d.Message.Contains("ftyp"));
    }

    // ---- bpcc ----

    [Fact]
    public void Parse_BpccPresentButIhdrDoesNotDeclareVariableBpc_ReportsDeviationAndIgnored()
    {
        // ihdr's BPC is 7 (8-bit unsigned, uniform) -- a bpcc box has no business existing here.
        var jp2 = J2kBuilder.Jp2(new J2kBuilder { Csiz = 2 }.BuildCodestream(), J2kBuilder.Ihdr(16, 16, 2, 7), WriteBox("bpcc", [7, 7]));

        var diagnostics = new DiagnosticCollection();
        Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.Jp2BoxInvalid && d.Message.Contains("bpcc") && d.Message.Contains("0xFF"));
    }

    [Fact]
    public void Parse_BpccComponentCountDisagreesWithIhdrNc_ReportsDeviation()
    {
        // ihdr declares BPC=0xFF (varies per component) and NC=3, but bpcc supplies only 2 bytes.
        var jp2 = J2kBuilder.Jp2(new J2kBuilder { Csiz = 3 }.BuildCodestream(), J2kBuilder.Ihdr(16, 16, 3, 0xFF), WriteBox("bpcc", [7, 7]));

        var diagnostics = new DiagnosticCollection();
        Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.Jp2BoxInvalid && d.Message.Contains("bpcc") && d.Message.Contains("NC"));
    }

    [Fact]
    public void Parse_BpccConsistentWithVariableBpcIhdr_NoDeviationReported()
    {
        var jp2 = J2kBuilder.Jp2(new J2kBuilder { Csiz = 2 }.BuildCodestream(), J2kBuilder.Ihdr(16, 16, 2, 0xFF), WriteBox("bpcc", [7, 0x8B]));

        var diagnostics = new DiagnosticCollection();
        Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("bpcc"));
    }

    [Fact]
    public void Parse_BpccEmptyBody_ReportsDeviation()
    {
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 0xFF), WriteBox("bpcc", []));

        var diagnostics = new DiagnosticCollection();
        Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.Jp2BoxInvalid && d.Message.Contains("bpcc") && d.Message.Contains("empty"));
    }

    // ---- colr, METH 1 ----

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(12)]
    public void Parse_ColrMeth1RecognisedEnumCs_SetsEnumeratedColourSpace(int enumCs)
    {
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), J2kBuilder.ColrEnum(enumCs));

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Equal(enumCs, container.Colour.EnumeratedColourSpace);
        Assert.DoesNotContain(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(99)]
    public void Parse_ColrMeth1UnrecognisedEnumCs_ReportsDeviationAndLeavesColourUnset(int enumCs)
    {
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), J2kBuilder.ColrEnum(enumCs));

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Null(container.Colour.EnumeratedColourSpace);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid && d.Message.Contains("EnumCS"));
    }

    // ---- colr, METH 2/3 (ICC by component count) ----

    [Theory]
    [InlineData(2, "GRAY", 1)]
    [InlineData(2, "RGB ", 3)]
    [InlineData(2, "CMYK", 4)]
    [InlineData(3, "RGB ", 3)]
    public void Parse_ColrMeth2Or3IccProfile_MapsByComponentCount(int meth, string dataColourSpace, int expectedComponentCount)
    {
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), WriteBox("colr", IccColr(meth, dataColourSpace)));

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Equal(expectedComponentCount, container.Colour.IccComponentCount);
        Assert.Null(container.Colour.EnumeratedColourSpace);
        Assert.DoesNotContain(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid);
    }

    [Fact]
    public void Parse_ColrMeth2IccProfileUnrecognisedDataColourSpace_ReportsDeviation()
    {
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), WriteBox("colr", IccColr(2, "XYZ ")));

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Null(container.Colour.IccComponentCount);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(200)]
    public void Parse_ColrMethAtOrAbove4_ReportsDeviationAndLeavesColourUnset(int meth)
    {
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), WriteBox("colr", [(byte)meth, 0, 0]));

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Null(container.Colour.EnumeratedColourSpace);
        Assert.Null(container.Colour.IccComponentCount);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid && d.Message.Contains("METH"));
    }

    [Fact]
    public void Parse_ColrMeth1VsMeth2SideBySide_SameBoxTypeDifferentFieldSet()
    {
        // METH 1 carries a 4-byte EnumCS at body offset 3; METH 2/3 carry ICC profile bytes at
        // that same offset with no EnumCS field at all -- one box type, two field layouts that
        // must not be cross-read as each other.
        var meth1 = J2kBuilder.ColrEnum(17); // greyscale
        var meth2 = WriteBox("colr", IccColr(2, "RGB "));

        var diagnosticsForMeth1 = new DiagnosticCollection();
        var meth1Result = Jp2Boxes.Parse(J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), meth1), PdfOptions.Default, diagnosticsForMeth1);

        var diagnosticsForMeth2 = new DiagnosticCollection();
        var meth2Result = Jp2Boxes.Parse(J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), meth2), PdfOptions.Default, diagnosticsForMeth2);

        Assert.Equal(17, meth1Result.Colour.EnumeratedColourSpace);
        Assert.Null(meth1Result.Colour.IccComponentCount);
        Assert.Empty(diagnosticsForMeth1);

        Assert.Null(meth2Result.Colour.EnumeratedColourSpace);
        Assert.Equal(3, meth2Result.Colour.IccComponentCount);
        Assert.Empty(diagnosticsForMeth2);
    }

    /// <summary>An I.5.3.3 METH 2/3 <c>colr</c> box body: <c>METH</c>, <c>PREC</c>, <c>APPROX</c>, then a (fake, minimal) ICC profile whose data-colour-space signature — the real ICC header's offset 16, so body offset 3+16=19 — is <paramref name="dataColourSpace"/>.</summary>
    private static byte[] IccColr(int meth, string dataColourSpace)
    {
        var body = new byte[23];
        body[0] = (byte)meth;
        body[1] = 0;
        body[2] = 0;
        Encoding.ASCII.GetBytes(dataColourSpace).CopyTo(body, 19);
        return body;
    }

    // ---- multiple colr boxes -- first recognised wins, others Info ----

    [Fact]
    public void Parse_TwoColrFixture_FirstRecognisedWins_SecondReportsWarningDeviation()
    {
        var bytes = ReadFixture("two-colr.jp2");

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(bytes, PdfOptions.Default, diagnostics);

        // MANIFEST.json: "colorEnumCS": [16, 18] -- EnumCS 16 first, 18 second; 16 wins. The
        // disagreeing second box is a recoverable deviation like every other JP2 deviation:
        // Warning, through FilterDiagnostics.ReportDeviation -- a hand-added Info severity here
        // would bypass PdfOptions.Strict.
        Assert.Equal(16, container.Colour.EnumeratedColourSpace);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid && d.Severity == DiagnosticSeverity.Warning);
        Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Info);
    }

    /// <summary>The disagreeing-duplicate <c>colr</c> deviation must honour <see cref="PdfOptions.Strict"/> exactly as every other <c>Jp2Boxes</c> deviation does — a direct <c>Info</c> add never threw.</summary>
    [Fact]
    public void Parse_TwoColrFixture_UnderStrict_ThrowsPlume3715()
    {
        var bytes = ReadFixture("two-colr.jp2");
        var strict = PdfOptions.Default with { Strict = true };

        var ex = Assert.Throws<PlumePdfException>(() => Jp2Boxes.Parse(bytes, strict, new DiagnosticCollection()));

        Assert.Equal(JpxDiagnosticCodes.ColourBoxInvalid, ex.Code);
    }

    [Fact]
    public void Parse_MultipleColrBoxes_HandBuilt_SecondRecognisedBoxIgnoredWithWarningDeviation()
    {
        var first = J2kBuilder.ColrEnum(16);
        var second = J2kBuilder.ColrEnum(18);
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), first, second);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Equal(16, container.Colour.EnumeratedColourSpace);
        var deviation = Assert.Single(diagnostics);
        Assert.Equal(JpxDiagnosticCodes.ColourBoxInvalid, deviation.Code);
        Assert.Equal(DiagnosticSeverity.Warning, deviation.Severity);
    }

    [Fact]
    public void Parse_MultipleColrBoxes_FirstUnrecognisedSecondRecognised_SecondWins()
    {
        var unrecognised = J2kBuilder.ColrEnum(99);
        var recognised = J2kBuilder.ColrEnum(16);
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), unrecognised, recognised);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Equal(16, container.Colour.EnumeratedColourSpace);
        // The failed first attempt is a Warning-class deviation reported by
        // TryParseColr itself; the second, recognised box resolves the colour and adds nothing.
        var deviation = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, deviation.Severity);
    }

    // ---- A JP2 cut short inside its jp2c ----

    /// <summary>
    /// A <c>jp2c</c> whose declared length runs past EOF is a file truncated INSIDE its codestream,
    /// not "not JPEG 2000": the box walk hands the available bytes on (with the box-length
    /// disagreement recorded as <c>PLUME3714</c>) instead of refusing with <c>PLUME3700</c>.
    /// </summary>
    [Theory]
    [InlineData(0.3)]
    [InlineData(0.6)]
    [InlineData(0.95)]
    public void Parse_Jp2CutInsideJp2c_HandsTheAvailableBytesOn_Reports3714NotThrow3700(double keepFraction)
    {
        var codestream = ReadFixture("baseline-53.j2k");
        var whole = J2kBuilder.Jp2(codestream, J2kBuilder.Ihdr(48, 64, 1, 7), J2kBuilder.ColrEnum(17));
        var jp2cBodyStart = whole.Length - codestream.Length;
        var cut = whole[..(jp2cBodyStart + (int)(codestream.Length * keepFraction))];

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(cut, PdfOptions.Default, diagnostics);

        Assert.Equal(cut.Length - jp2cBodyStart, container.Codestream.Length);
        Assert.True(container.Codestream.Span.SequenceEqual(codestream.AsSpan(0, cut.Length - jp2cBodyStart)));
        Assert.Equal(17, container.Colour.EnumeratedColourSpace); // the jp2h before the cut still parsed.
        var deviation = Assert.Single(diagnostics);
        Assert.Equal(JpxDiagnosticCodes.Jp2BoxInvalid, deviation.Code);
        Assert.Contains("runs past the end", deviation.Message, StringComparison.Ordinal);
    }

    /// <summary>End to end through <see cref="JpxImageDecoder"/>: the cut JP2 decodes what it can and reports the truncation as <c>PLUME3701</c> (the same code a raw <c>.j2k</c> cut at the same point reports), never <c>PLUME3700</c>.</summary>
    [Theory]
    [InlineData(0.3)]
    [InlineData(0.6)]
    public void Decode_Jp2CutInsideJp2c_Reports3701Truncation_AndDecodes(double keepFraction)
    {
        var codestream = ReadFixture("baseline-53.j2k");
        var whole = J2kBuilder.Jp2(codestream, J2kBuilder.Ihdr(48, 64, 1, 7), J2kBuilder.ColrEnum(17));
        var jp2cBodyStart = whole.Length - codestream.Length;
        var cut = whole[..(jp2cBodyStart + (int)(codestream.Length * keepFraction))];

        var diagnostics = new DiagnosticCollection();
        var image = JpxImageDecoder.Decode(cut, PdfOptions.Default, diagnostics);

        Assert.Equal(64, image.Width);
        Assert.Equal(48, image.Height);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.Truncated);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.Jp2BoxInvalid);
        Assert.DoesNotContain(diagnostics, d => d.Code == JpxDiagnosticCodes.NotJpeg2000);
    }

    // ---- pclr/cmap/cdef, and the committed hand-built fixtures ----

    [Fact]
    public void Parse_PclrCmapFixture_PaletteAndChannelMapParsed()
    {
        var bytes = ReadFixture("pclr-cmap.jp2");

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(bytes, PdfOptions.Default, diagnostics);

        // MANIFEST.json: pclrEntryCount 256, ihdrComponentCount 1 (a single-component index
        // codestream mapped through the palette to 3 RGB columns via cmap).
        Assert.NotNull(container.Colour.Palette);
        Assert.Equal(256, container.Colour.Palette!.Value.Entries);
        Assert.NotNull(container.Colour.ChannelMap);
        Assert.Equal(3, container.Colour.ChannelMap!.Length);
    }

    [Fact]
    public void Parse_PremultipliedAlphaFixture_CdefMarksPremultipliedOpacity()
    {
        var bytes = ReadFixture("premultiplied-alpha.jp2");

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(bytes, PdfOptions.Default, diagnostics);

        // MANIFEST.json: cdef entry {Cn:3, Typ:2, Asoc:0} -- channel 3, premultiplied opacity.
        Assert.Equal(3, container.Colour.AlphaChannelIndex);
        Assert.True(container.Colour.AlphaPremultiplied);
    }

    [Fact]
    public void Parse_Cmyk12Fixture_EnumCs12Recognised()
    {
        var bytes = ReadFixture("cmyk-enumcs12.jp2");

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(bytes, PdfOptions.Default, diagnostics);

        Assert.Equal(12, container.Colour.EnumeratedColourSpace);
        Assert.DoesNotContain(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid);
    }

    [Fact]
    public void Parse_CmapUnknownMtyp_ReportsDeviationAndIgnoresCmap()
    {
        var pclr = J2kBuilder.Pclr(2, 3, 7);
        var badCmap = WriteBox("cmap", [0, 0, 2, 0]); // MTYP=2 is neither 0 (direct) nor 1 (palette).
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), J2kBuilder.ColrEnum(16), pclr, badCmap);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Null(container.Colour.ChannelMap);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid && d.Message.Contains("MTYP"));
    }

    [Fact]
    public void Parse_CmapBodyNotWholeNumberOfEntries_ReportsDeviation()
    {
        var badCmap = WriteBox("cmap", [0, 0, 1]); // 3 bytes -- not a whole 4-byte entry.
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), badCmap);

        var diagnostics = new DiagnosticCollection();
        Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid && d.Message.Contains("cmap"));
    }

    [Fact]
    public void Parse_CdefTooShortForEntryCount_ReportsDeviationAndIgnored()
    {
        var badCdef = WriteBox("cdef", [0]); // one byte -- the U16 entry count doesn't even fit.
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), badCdef);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Null(container.Colour.AlphaChannelIndex);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid && d.Message.Contains("cdef"));
    }

    [Fact]
    public void Parse_CdefDeclaresMoreEntriesThanBodyHolds_ReportsDeviationAndStopsAtWhatItFound()
    {
        // One well-formed NON-opacity entry (Cn=0, Typ=0 -- "colour association", not opacity,
        // so the scan-for-opacity loop keeps going) followed by a declared-but-missing second
        // entry: the loop reports the shortfall and stops, never having found an opacity entry.
        var body = new byte[8];
        body[1] = 2; // count = 2
        body[2] = 0;
        body[3] = 0; // Cn = 0
        body[4] = 0;
        body[5] = 0; // Typ = 0 (not opacity)
        body[6] = 0;
        body[7] = 1; // Asoc = 1 -- body ends here, entry index 1 has no room at all.
        var jp2 = J2kBuilder.Jp2(new J2kBuilder { Csiz = 2 }.BuildCodestream(), J2kBuilder.Ihdr(16, 16, 2, 7), J2kBuilder.ColrEnum(17), WriteBox("cdef", body));

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Null(container.Colour.AlphaChannelIndex);
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.ColourBoxInvalid && d.Message.Contains("cdef"));
    }

    // ---- res -- resd wins over resc, N/D*10^E formula ----

    [Fact]
    public void Parse_ResFixture_PixelsPerMetreImplies300Dpi()
    {
        var bytes = ReadFixture("res-metadata.jp2");

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(bytes, PdfOptions.Default, diagnostics);

        Assert.NotNull(container.Colour.PixelsPerMetre);
        var (x, y) = container.Colour.PixelsPerMetre!.Value;
        // MANIFEST.json: "300 dpi (11811 px/m)"; DPI = pixels-per-metre * 0.0254.
        Assert.Equal(300.0, x * 0.0254, precision: 0);
        Assert.Equal(300.0, y * 0.0254, precision: 0);
    }

    [Fact]
    public void Parse_ResdAndRescBothPresent_ResdWins()
    {
        var res = WriteBox("res ", ResEntry("resc", vN: 100, vD: 1, hN: 100, hD: 1, vE: 0, hE: 0).Concat(ResEntry("resd", vN: 200, vD: 1, hN: 200, hD: 1, vE: 0, hE: 0)).ToArray());
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), res);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.Equal((200.0, 200.0), container.Colour.PixelsPerMetre);
    }

    [Fact]
    public void Parse_ResFormula_NumOverDenTimesTenToExponent()
    {
        // VRcN=300, VRcD=2, VRcE=1 -> (300/2) * 10^1 = 1500. HRcN=72, HRcD=1, HRcE=-1 -> 7.2.
        var res = WriteBox("res ", ResEntry("resc", vN: 300, vD: 2, hN: 72, hD: 1, vE: 1, hE: -1));
        var jp2 = J2kBuilder.Jp2(new J2kBuilder().BuildCodestream(), J2kBuilder.Ihdr(16, 16, 1, 7), res);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(jp2, PdfOptions.Default, diagnostics);

        Assert.NotNull(container.Colour.PixelsPerMetre);
        var (x, y) = container.Colour.PixelsPerMetre!.Value;
        Assert.Equal(7.2, x, precision: 6);
        Assert.Equal(1500.0, y, precision: 6);
    }

    /// <summary>One <c>resc</c>/<c>resd</c> sub-box body: <c>VRcN</c>/<c>VRcD</c>/<c>HRcN</c>/<c>HRcD</c> as big-endian <see cref="ushort"/>, then <c>VRcE</c>/<c>HRcE</c> as signed bytes — wrapped as a named box.</summary>
    private static byte[] ResEntry(string type, int vN, int vD, int hN, int hD, int vE, int hE)
    {
        var body = new byte[10];
        body[0] = (byte)(vN >> 8);
        body[1] = (byte)vN;
        body[2] = (byte)(vD >> 8);
        body[3] = (byte)vD;
        body[4] = (byte)(hN >> 8);
        body[5] = (byte)hN;
        body[6] = (byte)(hD >> 8);
        body[7] = (byte)hD;
        body[8] = unchecked((byte)(sbyte)vE);
        body[9] = unchecked((byte)(sbyte)hE);
        return WriteBox(type, body);
    }

    // ---- fragment table / second codestream -> 3716 ----

    [Fact]
    public void Parse_TwoJp2cBoxes_ReportsMultipleCodestreamsAndDecodesTheFirst()
    {
        var jp2hBox = WriteBox("jp2h", J2kBuilder.Ihdr(16, 16, 1, 7));
        var firstCodestream = WriteBox("jp2c", [0x01, 0x02, 0x03]);
        var secondCodestream = WriteBox("jp2c", [0x04, 0x05]);
        var file = new List<byte>();
        file.AddRange(jp2hBox);
        file.AddRange(firstCodestream);
        file.AddRange(secondCodestream);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(file.ToArray(), PdfOptions.Default, diagnostics);

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, container.Codestream.ToArray());
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.MultipleCodestreams);
    }

    // ---- malformed LBox/XLBox -> 3714 ----

    [Fact]
    public void Parse_LboxShorterThanTheHeaderItself_ReportsDeviationAndStopsTheWalk()
    {
        // LBox = 4: shorter than even the 8-byte ordinary box header, let alone a real body.
        var badBox = new byte[8];
        badBox[3] = 4;
        Encoding.ASCII.GetBytes("jp2h").CopyTo(badBox, 4);
        var jp2cBox = WriteBox("jp2c", [0xAA]);
        var file = new List<byte>();
        file.AddRange(jp2cBox); // found before the malformed box -- still usable.
        file.AddRange(badBox);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(file.ToArray(), PdfOptions.Default, diagnostics);

        Assert.Equal(new byte[] { 0xAA }, container.Codestream.ToArray());
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.Jp2BoxInvalid);
    }

    [Fact]
    public void Parse_XlboxDeclaredButBodyTooShortForIt_ReportsDeviationAndStopsTheWalk()
    {
        // LBox = 1 (use XLBox) but there aren't even 16 bytes of header for it to live in.
        byte[] badBox = [0, 0, 0, 1, (byte)'j', (byte)'p', (byte)'2', (byte)'h', 0, 0, 0, 0];
        var jp2cBox = WriteBox("jp2c", [0xBB]);
        var file = new List<byte>();
        file.AddRange(jp2cBox);
        file.AddRange(badBox);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(file.ToArray(), PdfOptions.Default, diagnostics);

        Assert.Equal(new byte[] { 0xBB }, container.Codestream.ToArray());
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.Jp2BoxInvalid);
    }

    [Fact]
    public void Parse_SubBoxInsideJp2hHasBadLength_StopsOnlyTheJp2hWalkNotTheTopLevelOne()
    {
        // The malformed sub-box is inside jp2h; a jp2c box placed AFTER jp2h in the top-level
        // walk must still be found (only the child superbox's own walk stops early).
        var badSubBox = new byte[8];
        badSubBox[3] = 4; // LBox shorter than 8 -- invalid even as an empty header.
        Encoding.ASCII.GetBytes("colr").CopyTo(badSubBox, 4);
        var jp2hBox = WriteBox("jp2h", J2kBuilder.Ihdr(16, 16, 1, 7).Concat(badSubBox).ToArray());
        var jp2cBox = WriteBox("jp2c", [0xCC, 0xDD]);
        var file = new List<byte>();
        file.AddRange(jp2hBox);
        file.AddRange(jp2cBox);

        var diagnostics = new DiagnosticCollection();
        var container = Jp2Boxes.Parse(file.ToArray(), PdfOptions.Default, diagnostics);

        Assert.Equal(new byte[] { 0xCC, 0xDD }, container.Codestream.ToArray());
        Assert.Contains(diagnostics, d => d.Code == JpxDiagnosticCodes.Jp2BoxInvalid && d.Message.Contains("jp2h"));
    }

    private static byte[] WriteBox(string type, byte[] body)
    {
        var box = new byte[8 + body.Length];
        var length = box.Length;
        box[0] = (byte)(length >> 24);
        box[1] = (byte)(length >> 16);
        box[2] = (byte)(length >> 8);
        box[3] = (byte)length;
        box[4] = (byte)type[0];
        box[5] = (byte)type[1];
        box[6] = (byte)type[2];
        box[7] = (byte)type[3];
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] ReadFixture(string name) =>
        File.ReadAllBytes(Path.Combine(FindRepoRoot(), "tests", "PlumePdf.CorpusTests", "Fixtures", "jpx", name));

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlumePdf.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"Could not locate the repository root above {AppContext.BaseDirectory}.");
    }
}
