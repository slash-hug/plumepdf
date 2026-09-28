using PlumePdf.Fonts.Reading;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Fonts.Reading;

/// <summary>
/// Pins <see cref="FontEncoder"/>'s encode direction as unaffected by the Standard-14
/// Symbol/ZapfDingbats sticky built-in-encoding rule.
/// <see cref="FontEncoder.Create"/> is a third <see cref="EncodingResolver.Resolve"/> caller
/// — the AcroForm <c>/DA</c>/<c>/DR</c> encode direction, via
/// <see cref="DocumentFontResolver"/> — that would otherwise
/// silently inherit a resolver-inferred sticky rule; the design keeps it an explicit,
/// caller-supplied flag instead, so <see cref="FontEncoder"/> stays bit-identical
/// for every Standard-14 symbol font name. These tests pin that today's behavior, so any
/// future change (in <see cref="EncodingResolver"/> or in <see cref="FontEncoder"/> itself)
/// that starts special-casing <c>/BaseFont /Symbol</c> or <c>/ZapfDingbats</c> shows up here.
/// </summary>
public class FontEncoderTests
{
    private static readonly IObjectSource EmptySource = new InMemoryObjectSource(new PdfDictionary(), new Dictionary<int, PdfObject>());

    [Fact]
    public void Create_DRStyleZapfDingbatsWithWinAnsiEncoding_EncodesAsciiUnchanged()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("ZapfDingbats"));
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));

        var encoder = FontEncoder.Create(fontDict, EmptySource, PdfOptions.Default, null);

        Assert.NotNull(encoder);
        Assert.True(encoder!.TryEncode("A", out var encoded, out _));
        Assert.Equal([0x41], encoded);
    }

    [Fact]
    public void Create_SymbolWithNoEncodingAndNoDescriptor_EncodesViaStandardEncoding()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Symbol"));

        var encoder = FontEncoder.Create(fontDict, EmptySource, PdfOptions.Default, null);

        Assert.NotNull(encoder);
        Assert.True(encoder!.TryEncode("A", out var encoded, out _));
        Assert.Equal([0x41], encoded); // StandardEncoding's "A", not Symbol's own built-in "Alpha"
    }

    [Fact]
    public void Create_SymbolWithDifferencesEncoding_UnaffectedCodesStillEncodeViaStandardEncoding()
    {
        // A third pin, distinct from the two above, that a resolver which started
        // *inferring* the sticky rule from /BaseFont itself (rather than from FontEncoder's
        // explicit non-authoritative call) would still pass by coincidence. A /Differences
        // dictionary routes EncodingResolver.Resolve through its PdfDictionary /Encoding
        // branch; for any code the /Differences array does not name, the base table must still
        // be StandardEncoding, not Symbol's own built-in encoding (whose code 0x41 is "Alpha",
        // an unrelated PUA-mapped codepoint — see Standard14Encodings.g.cs — that would make
        // this assertion fail if the sticky rule leaked in here).
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Symbol"));
        var encodingDict = new PdfDictionary();
        encodingDict.Set(PdfName.Get("Differences"), new PdfArray([PdfNumber.Get(200), PdfName.Get("degree")]));
        fontDict.Set(PdfName.Get("Encoding"), encodingDict);

        var encoder = FontEncoder.Create(fontDict, EmptySource, PdfOptions.Default, null);

        Assert.NotNull(encoder);
        Assert.True(encoder!.TryEncode("A", out var encoded, out _));
        Assert.Equal([0x41], encoded);
    }

    [Fact]
    public void DocumentFontResolver_ResolvesADRStyleZapfDingbatsFontForEncoding()
    {
        // No cheap PdfDictionary-level builder for a /DR /Font entry exists in
        // Forms/FormsTestDocuments.cs (its helpers all build whole encoded PDF byte streams),
        // so this constructs the /DR-shaped resources dictionary DocumentFontResolver.Resolve
        // actually consumes directly, exercising the real call path the FontEncoder.Create
        // pin above stands in for (RenderFontFactory/ExtractionFontFactory are the two callers
        // that DO pass the sticky-rule flag; DocumentFontResolver's FontEncoder is the one that
        // must not).
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("ZapfDingbats"));
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));

        var fontDir = new PdfDictionary();
        fontDir.Set(PdfName.Get("ZaDb"), fontDict);
        var resources = new PdfDictionary();
        resources.Set(PdfName.Get("Font"), fontDir);

        var resolver = new DocumentFontResolver(EmptySource, PdfOptions.Default, null);
        var resolved = resolver.Resolve("ZaDb", resources);

        Assert.NotNull(resolved);
        Assert.True(resolved!.Encoder.TryEncode("A", out var encoded, out _));
        Assert.Equal([0x41], encoded); // today's DocumentFontResolver behavior, unaffected by the sticky rule
    }
}
