using PlumePdf.Filters.Jbig2;
using Xunit;

namespace PlumePdf.Tests.Filters.Jbig2;

/// <summary>
/// Hewlett-Packard MFP scans carry the whole page as one immediate generic region
/// whose segment header declares an UNKNOWN data length (0xFFFFFFFF, ITU-T T.88 §7.2.7). The
/// segment reader used to abandon the walk there — silently — so every such page rendered
/// blank with no diagnostic. §7.2.7: the coded data runs to a terminator the coder cannot
/// produce (0xFF 0xAC for MQ, 0x00 0x00 for MMR) followed by a four-byte row count.
/// </summary>
public class Jbig2UnknownLengthTests
{
    // The 96x64 jbig2enc generic-region stream Jbig2DecoderTests already pins (page info + one
    // immediate generic region, template 0, MQ-coded, data 199 bytes ending in the MQ flush
    // marker 0xFF 0xAC).
    private static readonly byte[] KnownLengthStream = Convert.FromHexString(
        "00000000300001000000130000006000000040000000000000000001000000000001260001000000C700000060000000" +
        "400000000000000000000003FFFDFF02FEFEFEA7CD3AF89ACB51243CDFCC812074A4D7D337586024B4F97707EFFE2511" +
        "EA5ED1AE0484F5FDC8CE067003A49F7A439ACE4802F7AFAB2BED3876B1865BD6B2134BB653C8482A9CEFA8FF7FF22F01" +
        "E347BBFD7C67D5B22C5F5FA247761B7D9D55E0DB615CAE702A17E1B59864A898F27630A6143C3BEF31D2D1EAF7F1903C" +
        "863AE6357A38A6A0AF4BF83F2A5C264DC84AB80835DDE18DF1029BFB7B3261DEB8FA2919B87F629FFF6CD8A75A7FFFAC");

    private const int RegionHeaderOffset = 30;   // second segment header
    private const int RegionLengthOffset = 37;   // its 4-byte data length field
    private const int RegionDataOffset = 41;
    private const int RegionInfoHeightOffset = RegionDataOffset + 4;

    /// <summary>Rewrites the generic region as §7.2.7 unknown-length: length 0xFFFFFFFF, coded data, terminator, row count.</summary>
    private static byte[] UnknownLengthStream(uint rowCount, uint? regionHeight = null, bool dropTerminator = false)
    {
        var s = (byte[])KnownLengthStream.Clone();
        s[RegionLengthOffset] = s[RegionLengthOffset + 1] = s[RegionLengthOffset + 2] = s[RegionLengthOffset + 3] = 0xFF;
        if (regionHeight is { } h)
        {
            s[RegionInfoHeightOffset] = (byte)(h >> 24); s[RegionInfoHeightOffset + 1] = (byte)(h >> 16); s[RegionInfoHeightOffset + 2] = (byte)(h >> 8); s[RegionInfoHeightOffset + 3] = (byte)h;
        }

        var body = s.AsSpan(RegionDataOffset).ToArray();
        if (dropTerminator)
        {
            body = body[..^2]; // strip the trailing 0xFF 0xAC so no terminator exists anywhere
        }

        var tail = dropTerminator ? [] : new byte[] { (byte)(rowCount >> 24), (byte)(rowCount >> 16), (byte)(rowCount >> 8), (byte)rowCount };
        return [.. s.AsSpan(0, RegionDataOffset), .. body, .. tail];
    }

    private static bool[,] Decode(byte[] stream, DiagnosticCollection? diagnostics = null)
    {
        var result = Jbig2Decoder.Decode(stream, globals: null, PdfOptions.Default, diagnostics, null);
        Assert.Equal(96, result.Width);
        Assert.Equal(64, result.Height);
        return result.Page;
    }

    private static void AssertSameBitmap(bool[,] expected, bool[,] actual)
    {
        Assert.Equal(expected.GetLength(0), actual.GetLength(0));
        Assert.Equal(expected.GetLength(1), actual.GetLength(1));
        var set = 0;
        for (var y = 0; y < expected.GetLength(0); y++)
        {
            for (var x = 0; x < expected.GetLength(1); x++)
            {
                Assert.Equal(expected[y, x], actual[y, x]);
                if (actual[y, x]) set++;
            }
        }

        Assert.True(set > 0, "the reference bitmap is not blank");
    }

    [Fact]
    public void UnknownLength_ArithmeticRegion_DecodesIdenticallyToKnownLength()
    {
        var diagnostics = new DiagnosticCollection();
        var expected = Decode(KnownLengthStream);

        var actual = Decode(UnknownLengthStream(rowCount: 64), diagnostics);

        AssertSameBitmap(expected, actual);
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void UnknownLength_UnknownRegionHeight_UsesTheRowCount()
    {
        var expected = Decode(KnownLengthStream);

        var actual = Decode(UnknownLengthStream(rowCount: 64, regionHeight: 0xFFFFFFFF));

        AssertSameBitmap(expected, actual);
    }

    [Fact]
    public void UnknownLength_MissingTerminator_IsLoudNotBlank()
    {
        var diagnostics = new DiagnosticCollection();

        var ex = Assert.Throws<PlumePdfException>(() => Jbig2Decoder.Decode(UnknownLengthStream(rowCount: 64, dropTerminator: true), globals: null, PdfOptions.Default, diagnostics, null));

        Assert.Equal("PLUME3501", ex.Code);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3552" && d.Message.Contains("could not be parsed", StringComparison.Ordinal));
    }

    [Fact]
    public void AbandonedSegmentWalk_RecordsDiagnostic()
    {
        // A page-info segment followed by a header that declares a non-generic type with an
        // unknown length: §7.2.7 forbids that, so the walk must stop AND say so.
        var s = (byte[])KnownLengthStream.Clone();
        s[RegionHeaderOffset + 4] = 0x06; // ImmediateTextRegion
        s[RegionLengthOffset] = s[RegionLengthOffset + 1] = s[RegionLengthOffset + 2] = s[RegionLengthOffset + 3] = 0xFF;
        var diagnostics = new DiagnosticCollection();

        var ex = Assert.Throws<PlumePdfException>(() => Jbig2Decoder.Decode(s, globals: null, PdfOptions.Default, diagnostics, null));

        Assert.Equal("PLUME3501", ex.Code);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3552" && d.Message.Contains($"offset {RegionHeaderOffset}", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownLength_TerminatorBytesInsideAtPixels_AreSkipped()
    {
        // AT1 = (x -1, y -84) encodes as 0xFF 0xAC - the MQ terminator bytes - inside the region's
        // own AT field. The search must start after the AT bytes (jbig2dec and pdf.js scan from
        // byte 18 and would stop here).
        var s = UnknownLengthStream(rowCount: 64);
        var atOffset = RegionDataOffset + Jbig2RegionInfo.ByteSize + 1;
        s[atOffset] = 0xFF; s[atOffset + 1] = 0xAC;

        var segments = Jbig2SegmentReader.ParseSegments(s, maxSegments: 16, out var stoppedAt);

        Assert.Equal(2, segments.Count);
        Assert.Equal(197, segments[1].DataLength); // coded data up to the real terminator
        Assert.Equal(64, segments[1].UnknownLengthRowCount);
        Assert.Equal(s.Length, stoppedAt);
    }

    [Fact]
    public void UnknownLength_RowCountAboveDeclaredHeight_IsNotTheTerminator()
    {
        // A bogus FF AC + 1000 planted right after the AT bytes must be skipped (S7.4.6.4: the row
        // count is never more than the declared height); the real terminator (row count 64) wins.
        var s = UnknownLengthStream(rowCount: 64);
        var insertAt = RegionDataOffset + Jbig2RegionInfo.ByteSize + 1 + 8;
        byte[] bogus = [0xFF, 0xAC, 0x00, 0x00, 0x03, 0xE8];
        var planted = new byte[s.Length + bogus.Length];
        s.AsSpan(0, insertAt).CopyTo(planted);
        bogus.CopyTo(planted, insertAt);
        s.AsSpan(insertAt).CopyTo(planted.AsSpan(insertAt + bogus.Length));

        var segments = Jbig2SegmentReader.ParseSegments(planted, maxSegments: 16, out _);

        Assert.Equal(2, segments.Count);
        Assert.Equal(64, segments[1].UnknownLengthRowCount);
        Assert.Equal(197 + bogus.Length, segments[1].DataLength); // coded data up to the real terminator, plus the planted bytes
    }

    [Fact]
    public void UnknownLength_WalkResumesAfterTheRowCount()
    {
        // An end-of-page segment (type 49, no data) after the row count must be found: the walk
        // resumes at terminator + 6.
        byte[] endOfPage = [0, 0, 0, 2, 49, 0, 1, 0, 0, 0, 0];
        var s = (byte[])[.. UnknownLengthStream(rowCount: 64), .. endOfPage];

        var segments = Jbig2SegmentReader.ParseSegments(s, maxSegments: 16, out var stoppedAt);

        Assert.Equal(3, segments.Count);
        Assert.Equal(49, segments[2].Type);
        Assert.Equal(s.Length, stoppedAt);
    }

    [Fact]
    public void UnknownLength_MmrRegion_TerminatedByTwoZeroBytes_Decodes()
    {
        var known = Jbig2DecoderTests.BuildMmrGenericRegionStream(); // 64x40, page info + one MMR immediate generic region
        var expected = Jbig2Decoder.Decode(known, globals: null, PdfOptions.Default, null, null).Page;
        // Rewrite the region length as unknown and append the MMR terminator + row count.
        var s = (byte[])known.Clone();
        var lengthOffset = 11 + 19 + 7; // second header: number(4) flags(1) rts(1) page(1) then length
        s[lengthOffset] = s[lengthOffset + 1] = s[lengthOffset + 2] = s[lengthOffset + 3] = 0xFF;
        var unknown = (byte[])[.. s, 0x00, 0x00, 0, 0, 0, 40];
        var diagnostics = new DiagnosticCollection();

        var actual = Jbig2Decoder.Decode(unknown, globals: null, PdfOptions.Default, diagnostics, null).Page;

        AssertSameBitmap(expected, actual);
        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void TrailingRemainder_ReportedOnlyWhenHeaderSized(int trailing, bool expectDiagnostic)
    {
        // 0xFF fill: an unparseable header (long-form referred-to count of 0x1FFFFFFF); zero fill would parse as a valid empty symbol dictionary.
        var s = (byte[])[.. KnownLengthStream, .. Enumerable.Repeat((byte)0xFF, trailing)];
        var diagnostics = new DiagnosticCollection();

        var result = Jbig2Decoder.Decode(s, globals: null, PdfOptions.Default, diagnostics, null);

        Assert.Equal(96, result.Width); // the region still decodes either way
        Assert.Equal(expectDiagnostic, diagnostics.Any(d => d.Code == "PLUME3552"));
    }

    [Fact]
    public void UnknownLength_OnLosslessGenericRegionType_IsRejectedLoudly()
    {
        var s = UnknownLengthStream(rowCount: 64);
        s[RegionHeaderOffset + 4] = 39; // ImmediateLosslessGenericRegion - not permitted by S7.2.7
        var diagnostics = new DiagnosticCollection();

        var ex = Assert.Throws<PlumePdfException>(() => Jbig2Decoder.Decode(s, globals: null, PdfOptions.Default, diagnostics, null));

        Assert.Equal("PLUME3501", ex.Code);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3552");
    }

    [Fact]
    public void AbandonedGlobalsWalk_RecordsDiagnostic()
    {
        // A globals stream whose only header is a symbol dictionary with an unknown length.
        byte[] globals = [0, 0, 0, 0, 0, 0, 1, 0xFF, 0xFF, 0xFF, 0xFF, 0xAA, 0xBB];
        var diagnostics = new DiagnosticCollection();

        var result = Jbig2Decoder.Decode(KnownLengthStream, globals, PdfOptions.Default, diagnostics, null);

        Assert.Equal(96, result.Width);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3552" && d.Message.Contains("/JBIG2Globals", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownLength_ZeroRowsWithUnknownHeight_IsLoudNotBlank()
    {
        var diagnostics = new DiagnosticCollection();

        var ex = Assert.Throws<PlumePdfException>(() => Jbig2Decoder.Decode(UnknownLengthStream(rowCount: 0, regionHeight: 0xFFFFFFFF), globals: null, PdfOptions.Default, diagnostics, null));

        Assert.Equal("PLUME3501", ex.Code);
        Assert.Contains(diagnostics, d => d.Code == "PLUME3552" && d.Message.Contains("geometry", StringComparison.Ordinal));
    }
}
