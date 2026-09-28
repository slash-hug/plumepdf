using System.Text;
using PlumePdf.Fonts.Reading;
using PlumePdf.Fonts.Standard14;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Fonts.Reading;

/// <summary>
/// Unit tests for the <see cref="ExtractionFont"/> family: width lookup precedence
/// (<c>/Widths</c> over the Standard-14 metrics fallback over <c>/MissingWidth</c>),
/// multi-byte (Type0/Identity-H) decode, the <c>/ToUnicode</c>-overlay-first fallback chain,
/// and <see cref="ExtractionFontFactory"/> building both font shapes end to end from a
/// resolved font dictionary.
/// </summary>
public class ExtractionFontTests
{
    private static readonly IObjectSource EmptySource = new InMemoryObjectSource(new PdfDictionary(), new Dictionary<int, PdfObject>());

    // ---- SimpleExtractionFont: width lookup precedence ----

    [Fact]
    public void SimpleFont_DeclaredWidths_TakePriorityOverEverything()
    {
        var font = new SimpleExtractionFont(
            "Helvetica",
            SimpleFontEncodings.WinAnsiEncoding,
            new Dictionary<int, double> { [65] = 999 },
            missingWidth: 42,
            toUnicode: null,
            PdfOptions.Default,
            null);

        font.DecodeNext([65], out var codeLength, out var unicode, out var width);

        Assert.Equal(1, codeLength);
        Assert.Equal("A", unicode);
        Assert.Equal(999, width);
    }

    [Fact]
    public void SimpleFont_NoWidthsArray_FallsBackToStandard14MetricsForARecognizedBaseFont()
    {
        var font = new SimpleExtractionFont(
            "Helvetica",
            SimpleFontEncodings.WinAnsiEncoding,
            new Dictionary<int, double>(),
            missingWidth: 0,
            toUnicode: null,
            PdfOptions.Default,
            null);

        font.DecodeNext([65], out _, out _, out var width);

        var expected = Standard14Metrics.ByFontName["Helvetica"].GlyphWidths["A"];
        Assert.Equal(expected, width);
        Assert.NotEqual(0, width);
    }

    [Fact]
    public void SimpleFont_NoWidthsAndUnrecognizedBaseFont_FallsBackToMissingWidth()
    {
        var font = new SimpleExtractionFont(
            "SomeEmbeddedFont+ABCDEF",
            SimpleFontEncodings.WinAnsiEncoding,
            new Dictionary<int, double>(),
            missingWidth: 250,
            toUnicode: null,
            PdfOptions.Default,
            null);

        font.DecodeNext([65], out _, out _, out var width);

        Assert.Equal(250, width);
    }

    // ---- SimpleExtractionFont: missing-ToUnicode fallback chain ----

    [Fact]
    public void SimpleFont_ToUnicodeOverlay_TakesPriorityOverTheEncodingTable()
    {
        var toUnicode = CMapParser.Parse(Encoding.ASCII.GetBytes("1 beginbfchar\n<41> <005A>\nendbfchar\n"), PdfOptions.Default, null);
        var font = new SimpleExtractionFont(
            "Helvetica", SimpleFontEncodings.WinAnsiEncoding, new Dictionary<int, double>(), 0, toUnicode, PdfOptions.Default, null);

        font.DecodeNext([65], out _, out var unicode, out _);

        Assert.Equal("Z", unicode); // overlay wins over WinAnsi's "A" for code 65
    }

    [Fact]
    public void SimpleFont_NoToUnicode_FallsBackToTheEncodingTable()
    {
        var font = new SimpleExtractionFont(
            "Helvetica", SimpleFontEncodings.WinAnsiEncoding, new Dictionary<int, double>(), 0, toUnicode: null, PdfOptions.Default, null);

        font.DecodeNext([65], out _, out var unicode, out _);

        Assert.Equal("A", unicode);
    }

    [Fact]
    public void SimpleFont_UnmappedCode_SubstitutesReplacementCharacterAndReportsADiagnostic()
    {
        var opaque = new (string, int)[256];
        Array.Fill(opaque, ("", -1));
        var diagnostics = new DiagnosticCollection();

        var font = new SimpleExtractionFont("Symbolic+ABCDEF", opaque, new Dictionary<int, double>(), 0, toUnicode: null, PdfOptions.Default, diagnostics);

        font.DecodeNext([65], out _, out var unicode, out _);

        Assert.Equal("\uFFFD", unicode);
        Assert.Contains(diagnostics, d => d.Code == "PLUME8020");
    }

    // ---- SimpleExtractionFont/ExtractionFontFactory: Standard-14 Symbol/ZapfDingbats sticky
    // built-in encoding for non-embedded fonts ----

    [Fact]
    public void SimpleFont_NonEmbeddedSymbol_NoWidths_ResolvesAfmAdvanceForAlpha()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Symbol"));

        var factory = new ExtractionFontFactory(EmptySource, PdfFilterRegistry.Default, PdfOptions.Default, null);
        var font = factory.Build(fontDict);

        font.DecodeNext([(byte)'a'], out var codeLength, out var unicode, out var width);

        // Symbol's built-in encoding maps code 'a' to the glyph "alpha", not the Latin letter.
        // This codebase's Standard-14 Symbol/ZapfDingbats tables record each glyph's Unicode
        // column using the Private Use Area convention (U+F000 + code — see
        // Standard14Encodings.g.cs's own header comment) rather than the glyph's semantic
        // Unicode value, so the expectation is read from that same source table rather than a
        // hardcoded literal, matching the width assertion's own "not a literal" convention below.
        var (glyphName, glyphUnicode) = SimpleFontEncodings.SymbolEncoding[(byte)'a'];
        Assert.Equal("alpha", glyphName);
        Assert.Equal(1, codeLength);
        Assert.Equal(char.ConvertFromUtf32(glyphUnicode), unicode);

        var expected = Standard14Metrics.ByFontName["Symbol"].GlyphWidths["alpha"];
        Assert.Equal(expected, width);
        Assert.NotEqual(0, width); // the pre-existing advance-0 defect this rule fixes
    }

    [Fact]
    public void SimpleFont_NonEmbeddedSymbol_WithWinAnsiEncoding_StillGreek()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Symbol"));
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));

        var factory = new ExtractionFontFactory(EmptySource, PdfFilterRegistry.Default, PdfOptions.Default, null);
        var font = factory.Build(fontDict);

        font.DecodeNext([(byte)'a'], out _, out var unicode, out var width);

        // The named /Encoding is ignored outright for a non-embedded Standard-14 symbol font
        // (the sticky rule): still "alpha", not WinAnsiEncoding's "a".
        var (glyphName, glyphUnicode) = SimpleFontEncodings.SymbolEncoding[(byte)'a'];
        Assert.Equal("alpha", glyphName);
        Assert.Equal(char.ConvertFromUtf32(glyphUnicode), unicode);
        Assert.Equal(Standard14Metrics.ByFontName["Symbol"].GlyphWidths["alpha"], width);
    }

    [Fact]
    public void SimpleFont_NonEmbeddedSymbol_SubsetPrefixedSymbol_ResolvesAfmAdvance()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("ABCDEF+Symbol"));

        var factory = new ExtractionFontFactory(EmptySource, PdfFilterRegistry.Default, PdfOptions.Default, null);
        var font = factory.Build(fontDict);

        font.DecodeNext([(byte)'a'], out _, out var unicode, out var width);

        // The AFM/built-in-encoding alias lookup strips the subset prefix, so
        // "ABCDEF+Symbol" resolves exactly like "Symbol".
        var (glyphName, glyphUnicode) = SimpleFontEncodings.SymbolEncoding[(byte)'a'];
        Assert.Equal("alpha", glyphName);
        Assert.Equal(char.ConvertFromUtf32(glyphUnicode), unicode);
        Assert.Equal(Standard14Metrics.ByFontName["Symbol"].GlyphWidths["alpha"], width);
        Assert.NotEqual(0, width);
    }

    [Fact]
    public void SimpleFont_TrueTypeSubtypeSymbol_KeepsLatin()
    {
        // Spec non-goal: the sticky Standard-14 rule applies to /Type1 and /MMType1 only.
        // A /Subtype /TrueType font merely named "Symbol" is PDFium's kMsSymbol mapper path,
        // a different animal, and keeps today's Latin substitution heuristic.
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("TrueType"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Symbol"));

        var factory = new ExtractionFontFactory(EmptySource, PdfFilterRegistry.Default, PdfOptions.Default, null);
        var font = factory.Build(fontDict);

        font.DecodeNext([(byte)'A'], out _, out var unicode, out _);

        Assert.Equal("A", unicode);
    }

    [Fact]
    public void SimpleFont_ZapfDingbats_DecodesA20ForCode0x34()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("ZapfDingbats"));
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));

        var factory = new ExtractionFontFactory(EmptySource, PdfFilterRegistry.Default, PdfOptions.Default, null);
        var font = factory.Build(fontDict);

        font.DecodeNext([0x34], out _, out var unicode, out var width);

        // Code 0x34 is the heavy check mark glyph "a20" (the AcroForm /ZaDb checkbox-on glyph)
        // -- not the similarly-named "a28".
        var (glyphName, glyphUnicode) = SimpleFontEncodings.ZapfDingbatsEncoding[0x34];
        Assert.Equal("a20", glyphName);
        Assert.Equal(char.ConvertFromUtf32(glyphUnicode), unicode);
        Assert.Equal(Standard14Metrics.ByFontName["ZapfDingbats"].GlyphWidths["a20"], width);
    }

    [Fact]
    public void SimpleFont_EmbeddedSymbol_HonorsDeclaredEncodingNotTheStickyRule()
    {
        // An embedded font merely *named* Symbol keeps its own declared
        // /Encoding non-authoritatively, so extraction agrees with render — RenderFontFactory's
        // BuildNonEmbedded reaches the same non-authoritative behaviour for an embedded-but-
        // unusable Symbol font via its hadEmbeddedProgram flag, not by the sticky rule being
        // unreachable (BuildNonEmbedded IS still reachable once a program is embedded, via its
        // two other call sites for an unsupported/unparseable embedded program).
        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Get("FontFile"), new PdfStream(new PdfDictionary(), Array.Empty<byte>()));

        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Symbol"));
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));
        fontDict.Set(PdfName.Get("FontDescriptor"), descriptor);

        var factory = new ExtractionFontFactory(EmptySource, PdfFilterRegistry.Default, PdfOptions.Default, null);
        var font = factory.Build(fontDict);

        font.DecodeNext([(byte)'a'], out _, out var unicode, out _);

        Assert.Equal("a", unicode); // WinAnsiEncoding's code 'a' -> the Latin letter, not "alpha"
    }

    [Fact]
    public void SimpleFont_EmbeddedSymbol_NoDeclaredEncoding_FallsBackToSymbolBuiltInTable()
    {
        // An embedded-but-unusable Symbol with no declared /Encoding
        // used to resolve through the opaque symbolic default (U+FFFD + PLUME8020). The built-in
        // base is now always Symbol's own table; only its AUTHORITY depends on embedding.
        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Get("Flags"), PdfNumber.Get(4));
        descriptor.Set(PdfName.Get("FontFile"), new PdfStream(new PdfDictionary(), Array.Empty<byte>()));

        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Symbol"));
        fontDict.Set(PdfName.Get("FontDescriptor"), descriptor);

        var factory = new ExtractionFontFactory(EmptySource, PdfFilterRegistry.Default, PdfOptions.Default, null);
        var font = factory.Build(fontDict);

        font.DecodeNext([(byte)'a'], out _, out var unicode, out _);

        var expected = char.ConvertFromUtf32(SimpleFontEncodings.SymbolEncoding[0x61].Unicode); // "alpha" in the repo's Symbol table
        Assert.Equal(expected, unicode);
    }

    // ---- Type0ExtractionFont: multi-byte (Identity-H) decode ----

    [Fact]
    public void Type0Font_IdentityH_DecodesTwoByteCodesAndLooksUpWidthsByCid()
    {
        var font = new Type0ExtractionFont(
            "EmbeddedCid+ABCDEF",
            isIdentity: true,
            encodingCMap: null,
            widthsByCid: new Dictionary<int, double> { [0x0041] = 500 },
            defaultWidth: 1000,
            toUnicode: null,
            PdfOptions.Default,
            null);

        font.DecodeNext([0x00, 0x41, 0x00, 0x42], out var codeLength, out _, out var width);

        Assert.Equal(2, codeLength);
        Assert.Equal(500, width); // CID 0x41 has a declared width
    }

    [Fact]
    public void Type0Font_IdentityH_UndeclaredCidFallsBackToDefaultWidth()
    {
        var font = new Type0ExtractionFont(
            "EmbeddedCid+ABCDEF", true, null, new Dictionary<int, double>(), defaultWidth: 750, toUnicode: null, PdfOptions.Default, null);

        font.DecodeNext([0x00, 0x99], out _, out _, out var width);

        Assert.Equal(750, width);
    }

    [Fact]
    public void Type0Font_NonIdentity_UsesTheEmbeddedCMapForCodeLengthAndCid()
    {
        var encodingCMap = CMapParser.Parse(Encoding.ASCII.GetBytes("""
            1 begincodespacerange
            <00> <80>
            <8100> <FFFF>
            endcodespacerange
            1 begincidrange
            <8100> <81FF> 1
            endcidrange
            """), PdfOptions.Default, null);

        var font = new Type0ExtractionFont(
            "MixedWidthCid+ABCDEF", isIdentity: false, encodingCMap, new Dictionary<int, double> { [1] = 600 }, defaultWidth: 1000, toUnicode: null, PdfOptions.Default, null);

        font.DecodeNext([0x81, 0x00], out var codeLength, out _, out var width);

        Assert.Equal(2, codeLength);
        Assert.Equal(600, width); // code 0x8100 -> CID 1 via the embedded CMap
    }

    // ---- ExtractionFontFactory: end-to-end from a resolved font dictionary ----

    [Fact]
    public void Factory_BuildsASimpleFontFromADictionary()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Helvetica"));
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));
        fontDict.Set(PdfName.Get("FirstChar"), PdfNumber.Get(65));
        fontDict.Set(PdfName.Get("Widths"), new PdfArray([PdfNumber.Get(600)]));

        var factory = new ExtractionFontFactory(EmptySource, PdfFilterRegistry.Default, PdfOptions.Default, null);
        var font = factory.Build(fontDict);

        font.DecodeNext([65], out var codeLength, out var unicode, out var width);

        Assert.Equal(1, codeLength);
        Assert.Equal("A", unicode);
        Assert.Equal(600, width);
    }

    [Fact]
    public void Factory_BuildsAnIdentityHType0FontWithDescendantWidthsAndToUnicode()
    {
        var toUnicodeStream = new PdfStream(new PdfDictionary(), Encoding.ASCII.GetBytes("1 beginbfrange\n<0000> <00FF> <0041>\nendbfrange\n"));

        var descendant = new PdfDictionary();
        descendant.Set(PdfName.Subtype, PdfName.Get("CIDFontType2"));
        descendant.Set(PdfName.Get("DW"), PdfNumber.Get(1000));
        descendant.Set(PdfName.Get("W"), new PdfArray([PdfNumber.Get(0), new PdfArray([PdfNumber.Get(500)])]));

        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type0"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("EmbeddedCid+ABCDEF"));
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("Identity-H"));
        fontDict.Set(PdfName.Get("DescendantFonts"), new PdfArray([descendant]));
        fontDict.Set(PdfName.Get("ToUnicode"), toUnicodeStream);

        var factory = new ExtractionFontFactory(EmptySource, PdfFilterRegistry.Default, PdfOptions.Default, null);
        var font = factory.Build(fontDict);

        font.DecodeNext([0x00, 0x00], out var codeLength, out var unicode, out var width);

        Assert.Equal(2, codeLength);
        Assert.Equal("A", unicode); // <0041> is 'A', via the ToUnicode bfrange starting at code 0
        Assert.Equal(500, width); // CID 0 has a declared width via the individual-widths /W form
    }

    [Fact]
    public void Factory_CachesOneInstancePerIndirectReference()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Helvetica"));

        var factory = new ExtractionFontFactory(EmptySource, PdfFilterRegistry.Default, PdfOptions.Default, null);
        var reference = new IndirectReference(7, 0);

        var first = factory.GetOrBuild(reference, fontDict);
        var second = factory.GetOrBuild(reference, fontDict);

        Assert.Same(first, second);
    }

    [Fact]
    public void Factory_NonIdentityPredefinedCMap_FallsBackToIdentityWithDiagnostic()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Subtype, PdfName.Get("Type0"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("CjkFont"));
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("90ms-RKSJ-H"));
        fontDict.Set(PdfName.Get("DescendantFonts"), new PdfArray([new PdfDictionary()]));

        var diagnostics = new DiagnosticCollection();
        var factory = new ExtractionFontFactory(EmptySource, PdfFilterRegistry.Default, PdfOptions.Default, diagnostics);
        var font = factory.Build(fontDict);

        font.DecodeNext([0x00, 0x41], out var codeLength, out _, out _);

        Assert.Equal(2, codeLength); // fell back to Identity-H's 2-byte codes
        Assert.Contains(diagnostics, d => d.Code == "PLUME8021");
    }
}
