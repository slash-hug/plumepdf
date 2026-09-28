using System.IO.Compression;
using PlumePdf.Filters;
using PlumePdf.Filters.Jbig2;
using Xunit;

namespace PlumePdf.Tests.Filters.Jbig2;

/// <summary>
/// <c>/JBIG2Globals</c> streams are routinely Flate-wrapped by
/// real-world producers, but the adapter passed <see cref="PdfStream.RawBytes"/> — the
/// still-compressed zlib bytes — straight to the segment parser. The parser silently yielded
/// no symbol dictionaries, the text region found zero symbols and composed an empty bitmap
/// with no diagnostic, and the page rendered as a solid sheet through the image's own
/// <c>/Decode</c> polarity. These tests pin the fix: the globals stream's OWN filter chain is
/// applied first (PLUME3504 when that fails or is malformed), and a text region whose
/// referred segments supplied no symbols degrades loudly (PLUME3559) instead of painting
/// nothing silently.
/// </summary>
public class Jbig2GlobalsResolutionTests
{
    // A two-part symbol-coded stream produced by jbig2enc 0.32 (-s -p) from our own generated
    // 200x100 glyph pattern: the globals part holds the symbol dictionary (segment 0), the
    // embedded part holds page info + a text region referring to it - the exact structure
    // real scanner producers emit (dictionary in /JBIG2Globals, text region in the image).
    private static readonly byte[] GlobalsSegmentBytes = Convert.FromHexString(
        "0000000000010000000028000003FFFDFF02FEFEFE00000001000000013224342A88DD182378105CC6CF4A5FA6FD5C435FFFAC");

    private static readonly byte[] EmbeddedSegmentBytes = Convert.FromHexString(
        "0000000130000100000013000000C8000000640000000000000000000000000000020622000100000024000000C8000000640000000000000000000000000000089E2E15067E6185EC9C7A8FFFAC");

    private const int Width = 200;
    private const int Height = 100;

    private static PdfDictionary ParmsFor(PdfStream globalsStream)
    {
        var parms = new PdfDictionary();
        parms.Set(PdfName.Get("JBIG2Globals"), globalsStream);
        return parms;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            z.Write(data);
        }

        return output.ToArray();
    }

    [Fact]
    public void Decode_FlateWrappedGlobals_DecodesIdenticallyToRawGlobals()
    {
        var adapter = new Jbig2FilterAdapter();

        var rawGlobals = new PdfStream(new PdfDictionary(), GlobalsSegmentBytes);
        var reference = adapter.Decode(EmbeddedSegmentBytes, ParmsFor(rawGlobals), PdfOptions.Default, null, null);

        var flateDict = new PdfDictionary();
        flateDict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        var flateGlobals = new PdfStream(flateDict, ZlibCompress(GlobalsSegmentBytes));
        var diagnostics = new DiagnosticCollection();
        var decoded = adapter.Decode(EmbeddedSegmentBytes, ParmsFor(flateGlobals), PdfOptions.Default, diagnostics, null);

        Assert.Equal(reference, decoded);
        Assert.Empty(diagnostics);

        // And the shared-dictionary content actually landed: the packed 1-bpp output (0 =
        // black) must contain ink, not the all-white sheet the pre-fix code produced.
        var rowBytes = (Width + 7) / 8;
        Assert.Equal(rowBytes * Height, decoded.Length);
        Assert.Contains(decoded, static b => b != 0xFF);
    }

    [Fact]
    public void Decode_CorruptFlateGlobals_RefusedAsPlume3504()
    {
        var adapter = new Jbig2FilterAdapter();
        var flateDict = new PdfDictionary();
        flateDict.Set(PdfName.Filter, PdfName.Get("FlateDecode"));
        // 0xFF everywhere is invalid as a zlib header AND as raw DEFLATE (BTYPE 11 is
        // reserved), so both of FlateFilter's framings hard-fail - the lenient truncation
        // path (which returns partial bytes instead of throwing) never engages.
        var corrupt = new PdfStream(flateDict, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

        var ex = Assert.Throws<PlumePdfException>(() =>
            adapter.Decode(EmbeddedSegmentBytes, ParmsFor(corrupt), PdfOptions.Default, null, null));

        Assert.Equal("PLUME3504", ex.Code);
    }

    [Fact]
    public void Decode_GlobalsDeclaringJbig2DecodeFilter_RefusedAsPlume3504()
    {
        var adapter = new Jbig2FilterAdapter();
        var selfCodedDict = new PdfDictionary();
        selfCodedDict.Set(PdfName.Filter, PdfName.Get("JBIG2Decode"));
        var selfCoded = new PdfStream(selfCodedDict, GlobalsSegmentBytes);

        var ex = Assert.Throws<PlumePdfException>(() =>
            adapter.Decode(EmbeddedSegmentBytes, ParmsFor(selfCoded), PdfOptions.Default, null, null));

        Assert.Equal("PLUME3504", ex.Code);
    }

    [Fact]
    public void Decode_TextRegionWithNoAvailableSymbols_RefusesLoudlyInsteadOfPaintingNothing()
    {
        // The embedded part alone: its text region refers to globals segment 0, which is
        // absent - exactly what every consumer saw pre-fix. The region must be skipped with
        // PLUME3559, and with no other content segment the decode must refuse via PLUME3501
        // (a fail-loud posture) rather than return the blank page background.
        var diagnostics = new DiagnosticCollection();

        var ex = Assert.Throws<PlumePdfException>(() =>
            Jbig2Decoder.Decode(EmbeddedSegmentBytes, globals: null, PdfOptions.Default, diagnostics, null));

        Assert.Equal("PLUME3501", ex.Code);
        Assert.Contains(diagnostics, static d => d.Code == "PLUME3559");
    }
}
