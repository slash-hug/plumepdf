using PlumePdf.Filters.Jpx;
using Xunit;

namespace PlumePdf.Tests.Filters.Jpx;

/// <summary>
/// <see cref="JpxFilterAdapter"/>'s byte contract —
/// <c>Decode</c> delegates to <see cref="JpxImageDecoder.Decode"/> and packs the result via
/// <c>JpxImage.ToInterleaved8Bit</c> at 8-bit-or-narrower precision or
/// <c>ToInterleaved16BitBigEndian</c> above 8 bits. Cross-checked against
/// <see cref="JpxImageDecoder"/> directly rather than against a hardcoded expected byte array, so
/// this test tracks the adapter's contract (which call, which flag) rather than duplicating the
/// codec's own pixel values.
/// </summary>
public class JpxFilterAdapterTests
{
    private readonly JpxFilterAdapter _adapter = new();

    [Fact]
    public void Decode_EightBitFixture_MatchesToInterleaved8BitWithAlphaDropped()
    {
        var bytes = ReadFixture("baseline-53.j2k"); // 8-bit, single component (MANIFEST.json).

        var actual = _adapter.Decode(bytes, PdfOptions.Default, diagnostics: null, subject: null);

        var image = JpxImageDecoder.Decode(bytes, PdfOptions.Default, diagnostics: null);
        var expected = image.ToInterleaved8Bit(dropAlpha: true);

        Assert.Equal(expected, actual);
        // A 64x48 single-component 8-bit source packs one byte per pixel, no widening.
        Assert.Equal(64 * 48, actual.Length);
    }

    [Fact]
    public void Decode_TwelveBitFixture_MatchesToInterleaved16BitBigEndianWithAlphaDropped()
    {
        var bytes = ReadFixture("depth-12u.j2k"); // 12-bit, single component (MANIFEST.json).

        var actual = _adapter.Decode(bytes, PdfOptions.Default, diagnostics: null, subject: null);

        var image = JpxImageDecoder.Decode(bytes, PdfOptions.Default, diagnostics: null);
        var expected = image.ToInterleaved16BitBigEndian(dropAlpha: true);

        Assert.Equal(expected, actual);
        // 64x48 single-component, above 8 bits -- two bytes per pixel, big-endian.
        Assert.Equal(64 * 48 * 2, actual.Length);
    }

    [Fact]
    public void Decode_Jp2WrappedEightBitFixture_DecodesTheSameBytesAsTheRawCodestream()
    {
        // "jpx-small"-shaped byte contract check: a JP2-wrapped source produces the exact same
        // pixel bytes as its raw codestream twin (the wrapper carries no pixel data of its own).
        var jp2 = ReadFixture("baseline-53-jp2.jp2");
        var j2k = ReadFixture("baseline-53.j2k");

        var fromJp2 = _adapter.Decode(jp2, PdfOptions.Default, diagnostics: null, subject: null);
        var fromJ2k = _adapter.Decode(j2k, PdfOptions.Default, diagnostics: null, subject: null);

        Assert.Equal(fromJ2k, fromJp2);
    }

    /// <summary>
    /// Regression test for the bug that this adapter's <c>maxPrecision</c> scan
    /// (unlike <c>ImageExtractor</c>'s equivalent loop) included the <c>cdef</c>-declared alpha
    /// plane: an 8-bit greyscale colour channel next to a 12-bit opacity channel must still pack
    /// 8-bit (alpha is dropped here — <c>dropAlpha: true</c> — so its own precision is
    /// irrelevant to how the retained colour samples are packed), matching what
    /// <c>ImageExtractor</c>'s call site would do for the identical dictionary, per this
    /// method's own doc comment promising the two never disagree.
    /// </summary>
    [Fact]
    public void Decode_EightBitColourWithWiderAlphaPlane_StillPacksEightBit()
    {
        // Component 0 (Ssiz=7 -> 8-bit unsigned) is the sole grey colour channel; component 1
        // (Ssiz=11 -> 12-bit unsigned) is declared opacity (Typ=1) via cdef -- greyscale (colr
        // enumCS 17) + one opacity channel is a consistent 2-component JP2, the same shape
        // Jp2BoxesTests/JpxMalformedInputTests already use for cdef coverage.
        var codestream = new J2kBuilder { Csiz = 2, Components = [(7, 1, 1), (11, 1, 1)] }.BuildCodestream();
        var jp2 = J2kBuilder.Jp2(codestream, J2kBuilder.Ihdr(16, 16, 2, 7), J2kBuilder.ColrEnum(17), J2kBuilder.Cdef((1, 1, 0)));

        var image = JpxImageDecoder.Decode(jp2, PdfOptions.Default, diagnostics: null);
        Assert.Equal(1, image.Colour.AlphaChannelIndex); // sanity: the cdef box was honoured.
        Assert.Equal(8, image.Planes[0].Precision);
        Assert.Equal(12, image.Planes[1].Precision); // sanity: the alpha plane really is wider.

        var actual = _adapter.Decode(jp2, PdfOptions.Default, diagnostics: null, subject: null);

        var expected = image.ToInterleaved8Bit(dropAlpha: true);
        Assert.Equal(expected, actual);
        Assert.Equal(16 * 16, actual.Length); // one 8-bit colour byte per pixel -- 16-bit packing would double this.
    }

    [Fact]
    public void Decode_MalformedInput_ThrowsCodedPlumePdfException()
    {
        byte[] garbage = [0x00, 0x01, 0x02, 0x03];

        var ex = Assert.Throws<PlumePdfException>(() => _adapter.Decode(garbage, PdfOptions.Default, diagnostics: null, subject: null));

        Assert.Equal(JpxDiagnosticCodes.NotJpeg2000, ex.Code);
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
