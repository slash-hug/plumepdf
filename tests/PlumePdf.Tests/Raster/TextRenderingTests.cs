using System.Collections.Generic;
using System.Text;
using PlumePdf.Fonts;
using PlumePdf.Fonts.Substitute;
using PlumePdf.Objects;
using PlumePdf.Raster;
using PlumePdf.Tests.Fonts.Outlines;
using PlumePdf.Tests.Fonts.Substitute;
using Xunit;

namespace PlumePdf.Tests.Raster;

/// <summary>
/// Phase 8 text rasterization (the KEYSTONE gap): the interpreter now resolves each shown
/// character to a glyph outline and the paint pass rasterizes it — op -&gt; font resolve -&gt; glyph
/// outline -&gt; device transform -&gt; TextPageObject -&gt; GlyphRasterizer. These tests rasterize real
/// text and assert on actual pixels (ink where a glyph should be, blank where none), covering the
/// substitute-fallback path, the embedded-TrueType path, the all-whitespace no-ink case, the
/// embedded-bare-CFF paths — a simple Type1C font's name-keyed glyph selection
/// (both an explicit <c>/Encoding</c> and the font's own built-in encoding), a Type1C stream
/// reached through a Type0/CID wrapper (which must take the CID path, never a name lookup), and a
/// Type1C font reachable only through a Form XObject's own <c>/Resources</c> — and
/// the non-embedded Standard-14 Symbol/ZapfDingbats path, which now substitutes PDFium's
/// own bundled Foxit CFF face (<c>PLUME7510</c>) rather than skipping the run.
/// </summary>
public class TextRenderingTests
{
    private static byte[] Bytes(string content) => Encoding.ASCII.GetBytes(content);

    // A pixel is "inked" when its red channel is meaningfully darker than the white background —
    // black text over white, with a generous threshold so antialiased edge pixels still count.
    private static bool IsInked(ReadOnlySpan<byte> rgba, int width, int x, int y) => rgba[((y * width) + x) * 4] < 200;

    private static ObjectRegistry EmptyObjects() => new(new InMemoryObjectSource(new PdfDictionary(), new Dictionary<int, PdfObject>()));

    private readonly record struct InkAnalysis(int Total, int MinX, int MaxX, int MinY, int MaxY, int[] ColumnInkRows);

    private static InkAnalysis Analyze(RasterImageFrame frame)
    {
        var span = frame.Pixels.Span;
        var total = 0;
        int minX = frame.Width, maxX = -1, minY = frame.Height, maxY = -1;
        var columnInkRows = new int[frame.Width];

        for (var y = 0; y < frame.Height; y++)
        {
            for (var x = 0; x < frame.Width; x++)
            {
                if (!IsInked(span, frame.Width, x, y))
                {
                    continue;
                }

                total++;
                columnInkRows[x]++;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        return new InkAnalysis(total, minX, maxX, minY, maxY, columnInkRows);
    }

    // Asserts the rendered ink looks like a capital 'H': localized (not a page-filling block), with
    // two tall vertical strokes (left and right) and a much shorter middle column (only the
    // crossbar crosses it) — proof that a real glyph outline was filled, not an arbitrary rectangle.
    private static void AssertLooksLikeCapitalH(RasterImageFrame frame)
    {
        var ink = Analyze(frame);
        Assert.True(ink.Total > 40, $"expected the letter to paint a meaningful number of pixels, got {ink.Total}");

        var glyphWidth = ink.MaxX - ink.MinX;
        var glyphHeight = ink.MaxY - ink.MinY;
        Assert.InRange(glyphWidth, 4, frame.Width - 10);   // localized horizontally, not the whole page.
        Assert.InRange(glyphHeight, 4, frame.Height - 10); // localized vertically, not the whole page.

        var maxColumnRows = 0;
        foreach (var rows in ink.ColumnInkRows)
        {
            maxColumnRows = Math.Max(maxColumnRows, rows);
        }

        Assert.True(maxColumnRows >= glyphHeight / 2, $"expected a tall vertical stroke spanning much of the glyph height ({glyphHeight}), got a max column height of {maxColumnRows}");

        // A left leg (left third) and a right leg (right third), both tall.
        var leftThird = ink.MinX + (glyphWidth / 3);
        var rightThird = ink.MaxX - (glyphWidth / 3);
        var tallLeft = false;
        var tallRight = false;
        for (var x = ink.MinX; x <= leftThird; x++)
        {
            tallLeft |= ink.ColumnInkRows[x] >= maxColumnRows * 0.6;
        }

        for (var x = rightThird; x <= ink.MaxX; x++)
        {
            tallRight |= ink.ColumnInkRows[x] >= maxColumnRows * 0.6;
        }

        Assert.True(tallLeft, "expected a tall left vertical stroke");
        Assert.True(tallRight, "expected a tall right vertical stroke");

        // The middle column passes through the gap between the legs (only the crossbar), so it must
        // be far shorter than a full leg — this is what distinguishes an 'H' from a filled block.
        var middle = (ink.MinX + ink.MaxX) / 2;
        Assert.True(ink.ColumnInkRows[middle] <= maxColumnRows * 0.5, $"expected the gap between the H's legs to be far shorter ({ink.ColumnInkRows[middle]}) than a leg ({maxColumnRows})");
    }

    [Fact]
    public void BlackH_NonEmbeddedStandard14_FallsBackToLiberationSubstitute_ProducesVerticalBarCoverage()
    {
        // A non-embedded Standard-14 Helvetica: no /FontFile*, so the render path substitutes a
        // metric-compatible Liberation face (PLUME7510) and still draws a real 'H'.
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        fontDict.Set(PdfName.Get("Subtype"), PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Helvetica"));

        var resources = new PdfDictionary();
        var fontResources = new PdfDictionary();
        fontResources.Set(PdfName.Get("F1"), fontDict);
        resources.Set(PdfName.Get("Font"), fontResources);

        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td (H) Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            EmptyObjects());

        AssertLooksLikeCapitalH(frame);
        Assert.Contains(diagnostics, d => d.Code == "PLUME7510"); // recorded the substitution.
    }

    [Fact]
    public void WhitespaceOnlyPage_ProducesNoInk()
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        fontDict.Set(PdfName.Get("Subtype"), PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Helvetica"));

        var resources = new PdfDictionary();
        var fontResources = new PdfDictionary();
        fontResources.Set(PdfName.Get("F1"), fontDict);
        resources.Set(PdfName.Get("Font"), fontResources);

        // Only the space glyph is shown — it has no outline, so nothing is painted.
        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td (   ) Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            objects: EmptyObjects());

        Assert.Equal(0, Analyze(frame).Total);
    }

    [Fact]
    public void BlackH_EmbeddedTrueTypeCidFont_ProducesVerticalBarCoverage()
    {
        // A fully hermetic embedded font: a real Liberation TrueType program embedded as /FontFile2
        // on a Type0/Identity-H CIDFontType2, shown by its own glyph id. Exercises the embedded
        // path (SfntReader.ParseForRender -> GetGlyphOutline, CIDToGIDMap Identity) end to end.
        Assert.True(SubstituteFontBlobs.TryGetBlob("LiberationSans-Regular", out var blob));
        var fontBytes = blob.ToArray();
        Assert.True(SubstituteFontStore.TryGetFont("LiberationSans-Regular", FontReadLimits.Default, out var lib));
        Assert.True(lib.TryGetGlyphId('H', out var hGid));

        var fontFile = new PdfStream(new PdfDictionary(), fontBytes);

        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Get("Type"), PdfName.Get("FontDescriptor"));
        descriptor.Set(PdfName.Get("FontName"), PdfName.Get("LiberationSans"));
        descriptor.Set(PdfName.Get("Flags"), PdfNumber.Get(4));
        descriptor.Set(PdfName.Get("FontFile2"), new PdfReference(new IndirectReference(1, 0)));

        var cidFont = new PdfDictionary();
        cidFont.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        cidFont.Set(PdfName.Get("Subtype"), PdfName.Get("CIDFontType2"));
        cidFont.Set(PdfName.Get("BaseFont"), PdfName.Get("LiberationSans"));
        cidFont.Set(PdfName.Get("CIDToGIDMap"), PdfName.Get("Identity"));
        cidFont.Set(PdfName.Get("DW"), PdfNumber.Get(1000));
        cidFont.Set(PdfName.Get("FontDescriptor"), new PdfReference(new IndirectReference(2, 0)));

        var descendants = new PdfArray();
        descendants.Add(new PdfReference(new IndirectReference(3, 0)));

        var type0 = new PdfDictionary();
        type0.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        type0.Set(PdfName.Get("Subtype"), PdfName.Get("Type0"));
        type0.Set(PdfName.Get("BaseFont"), PdfName.Get("LiberationSans"));
        type0.Set(PdfName.Get("Encoding"), PdfName.Get("Identity-H"));
        type0.Set(PdfName.Get("DescendantFonts"), descendants);

        var objectsDict = new Dictionary<int, PdfObject>
        {
            [1] = fontFile,
            [2] = descriptor,
            [3] = cidFont,
            [4] = type0,
        };
        var objects = new ObjectRegistry(new InMemoryObjectSource(new PdfDictionary(), objectsDict), nextObjectNumber: 5);

        var resources = new PdfDictionary();
        var fontResources = new PdfDictionary();
        fontResources.Set(PdfName.Get("F1"), new PdfReference(new IndirectReference(4, 0)));
        resources.Set(PdfName.Get("Font"), fontResources);

        // Identity-H: the two-byte hex code IS the CID, and Identity CIDToGIDMap makes it the GID.
        var content = $"BT /F1 48 Tf 20 30 Td <{hGid:X4}> Tj ET";
        var frame = Rasterizer.Rasterize(
            Bytes(content),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            objects: objects);

        AssertLooksLikeCapitalH(frame);
    }

    // =========================================================================================
    // Non-embedded Standard-14 Symbol/ZapfDingbats now substitute PDFium's own
    // bundled Foxit CFF faces (PLUME7510) instead of skipping the run — cases (a)/(a')/(b)/(b'),
    // plus the two "stays on the Liberation path" pins (f)/(g) that the exact-name/exact-subtype
    // rule deliberately does not touch.
    // =========================================================================================

    private static PdfDictionary NonEmbeddedType1SymbolFont(string baseFont, string? encoding = null)
    {
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        fontDict.Set(PdfName.Get("Subtype"), PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get(baseFont));
        if (encoding is not null)
        {
            fontDict.Set(PdfName.Get("Encoding"), PdfName.Get(encoding));
        }

        return fontDict;
    }

    private static PdfDictionary SingleFontResources(PdfDictionary fontDict)
    {
        var resources = new PdfDictionary();
        var fontResources = new PdfDictionary();
        fontResources.Set(PdfName.Get("F1"), fontDict);
        resources.Set(PdfName.Get("Font"), fontResources);
        return resources;
    }

    [Fact]
    public void NonEmbeddedZapfDingbats_SubstitutesFoxitDingbatsFace_ProducesInk()
    {
        // Code 0x34 ('4') is glyph "a20" (the heavy check mark) in the ZapfDingbats built-in
        // encoding — not the digit glyph "four" a Latin substitute would have drawn.
        var resources = SingleFontResources(NonEmbeddedType1SymbolFont("ZapfDingbats"));

        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td (4) Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            EmptyObjects());

        Assert.True(Analyze(frame).Total > 0);
        Assert.Contains(diagnostics, d => d.Code == "PLUME7510" && d.Message.Contains("FoxitDingbats"));
    }

    [Fact]
    public void NonEmbeddedZapfDingbats_WithNamedEncoding_IgnoresIt_StillProducesInk()
    {
        // A /Encoding on a Standard-14 symbol font is meaningless against the substitute's
        // built-in encoding — EncodingResolver.Resolve's builtInIsAuthoritative: true ignores the
        // named table entirely, and the render-side diagnostic records that it did.
        var resources = SingleFontResources(NonEmbeddedType1SymbolFont("ZapfDingbats", "WinAnsiEncoding"));

        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td (4) Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            EmptyObjects());

        Assert.True(Analyze(frame).Total > 0);
        Assert.Contains(diagnostics, d => d.Code == "PLUME7510" && d.Message.Contains("was ignored in favour of the face's built-in encoding"));
    }

    [Fact]
    public void NonEmbeddedZapfDingbats_WithBaseEncodingDictionary_IgnoresIt_RecordsDiagnostic()
    {
        // The "declared /Encoding was ignored" diagnostic sentence only fired
        // for a bare /Encoding name; a dictionary shape naming its base via /BaseEncoding
        // (<< /BaseEncoding /WinAnsiEncoding /Differences [...] >>) — at least as common a
        // producer shape as the bare name — is overridden by the exact same sticky rule
        // (EncodingResolver.Resolve's builtInIsAuthoritative branch ignores /BaseEncoding inside
        // a dictionary too) but previously recorded no diagnostic saying so.
        var fontDict = NonEmbeddedType1SymbolFont("ZapfDingbats");
        var encodingDict = new PdfDictionary();
        encodingDict.Set(PdfName.Get("BaseEncoding"), PdfName.Get("WinAnsiEncoding"));
        fontDict.Set(PdfName.Get("Encoding"), encodingDict);
        var resources = SingleFontResources(fontDict);

        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td (4) Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            EmptyObjects());

        Assert.True(Analyze(frame).Total > 0);
        Assert.Contains(diagnostics, d => d.Code == "PLUME7510" && d.Message.Contains("dictionary's /BaseEncoding /WinAnsiEncoding") && d.Message.Contains("was ignored in favour of the face's built-in encoding"));
    }

    [Fact]
    public void NonEmbeddedSymbol_SubstitutesFoxitSymbolFace_ProducesInk()
    {
        // Code 'a' (0x61) is glyph "alpha" in the Symbol built-in encoding.
        var resources = SingleFontResources(NonEmbeddedType1SymbolFont("Symbol"));

        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td (a) Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            objects: EmptyObjects());

        Assert.True(Analyze(frame).Total > 0);
    }

    [Fact]
    public void NonEmbeddedSymbolMT_SubstitutesFoxitSymbolFace_EmitsPlume7510NamingIt()
    {
        // "SymbolMT" is the other exact alias Standard14SymbolFonts recognizes for Symbol.
        var resources = SingleFontResources(NonEmbeddedType1SymbolFont("SymbolMT"));

        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td (a) Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            EmptyObjects());

        Assert.True(Analyze(frame).Total > 0);
        Assert.Contains(diagnostics, d => d.Code == "PLUME7510" && d.Message.Contains("FoxitSymbol"));
    }

    [Fact]
    public void EmbeddedButUnparseableSymbol_HonorsDeclaredEncoding_AgreesWithExtraction()
    {
        // A font descriptor that DOES carry an
        // embedded program (/FontFile present) but whose program fails to parse must NOT take
        // the Standard-14 sticky-encoding rule — that rule is for the genuinely non-embedded
        // case only. Before this fix, RenderFontFactory.BuildNonEmbedded applied the sticky rule
        // unconditionally, so this exact font dictionary rendered via the Foxit face with its
        // declared /WinAnsiEncoding IGNORED, while ExtractionFontFactory (whose isEmbedded gate
        // is presence-based) honoured /WinAnsiEncoding and decoded code 'a' as the Latin letter
        // "a" — the two paths disagreed on the very same document.
        //
        // /WinAnsiEncoding maps code 'a' (0x61) to the glyph name "a", which the Foxit Symbol
        // charset does not contain (its names are Greek/math, e.g. "alpha") — so honouring the
        // declared encoding correctly paints nothing for this code, unlike the sticky-rule tests
        // above (which use codes the Foxit charset resolves).
        var fontFile = new PdfStream(new PdfDictionary(), Bytes("not a Type 1 program, no eexec section here"));

        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Get("Type"), PdfName.Get("FontDescriptor"));
        descriptor.Set(PdfName.Get("FontName"), PdfName.Get("Symbol"));
        descriptor.Set(PdfName.Get("FontFile"), new PdfReference(new IndirectReference(1, 0)));

        var fontDict = NonEmbeddedType1SymbolFont("Symbol", "WinAnsiEncoding");
        fontDict.Set(PdfName.Get("FontDescriptor"), new PdfReference(new IndirectReference(2, 0)));

        var objects = new ObjectRegistry(new InMemoryObjectSource(new PdfDictionary(), new Dictionary<int, PdfObject> { [1] = fontFile, [2] = descriptor }), nextObjectNumber: 3);
        var resources = SingleFontResources(fontDict);

        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td (a) Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            objects);

        // A Foxit substitution is still recorded (the embedded program didn't parse), but the
        // declared /Encoding must NOT be reported as ignored — it wasn't.
        Assert.Contains(diagnostics, d => d.Code == "PLUME7510" && d.Message.Contains("FoxitSymbol"));
        Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME7510" && d.Message.Contains("was ignored in favour of the face's built-in encoding"));

        // An embedded-but-unparseable Standard-14 symbol font honors its declared
        // /Encoding non-authoritatively, so a substitution here is never guaranteed to paint
        // anything (the Foxit charset has no Latin glyph names at all) — unlike the ordinary
        // Info-level "rendered using..." substitution message, this case must say so explicitly
        // and at Warning, so a caller filtering on Warning still sees that this font's text may
        // have gone unrendered (the signal the retired PLUME7729 used to carry) rather than the
        // message actively claiming a render that didn't happen.
        Assert.Contains(diagnostics, d => d.Code == "PLUME7510" && d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("failed to parse") && d.Message.Contains("will not paint"));
        Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME7510" && d.Message.Contains("is not embedded"));

        // /WinAnsiEncoding's "a" has no glyph of that name in the Foxit Symbol charset, so nothing
        // paints — the correct (if visually blank) consequence of honouring the declared encoding,
        // matching ExtractionFontFactory's own SimpleFont_EmbeddedSymbol_HonorsDeclaredEncodingNotTheStickyRule.
        Assert.Equal(0, Analyze(frame).Total);
    }

    [Fact]
    public void EmbeddedButUnparseableSymbol_NoDeclaredEncoding_FallsBackToSymbolBuiltIn_PaintsInk()
    {
        // With an embedded-but-corrupt program and NO declared /Encoding,
        // the built-in base used to be the opaque all-unassigned table, so nothing painted and
        // extraction produced U+FFFD. ISO 32000-1 §9.6.6.1's "font's built-in encoding" for a
        // Standard-14 Symbol is Symbol's own table — supplied non-authoritatively, so a declared
        // /Encoding (the sibling test above) still wins when present.
        var fontFile = new PdfStream(new PdfDictionary(), Bytes("not a Type 1 program, no eexec section here"));

        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Get("Type"), PdfName.Get("FontDescriptor"));
        descriptor.Set(PdfName.Get("FontName"), PdfName.Get("Symbol"));
        descriptor.Set(PdfName.Get("Flags"), PdfNumber.Get(4));
        descriptor.Set(PdfName.Get("FontFile"), new PdfReference(new IndirectReference(1, 0)));

        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        fontDict.Set(PdfName.Get("Subtype"), PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Symbol"));
        fontDict.Set(PdfName.Get("FontDescriptor"), new PdfReference(new IndirectReference(2, 0)));

        var objects = new ObjectRegistry(new InMemoryObjectSource(new PdfDictionary(), new Dictionary<int, PdfObject> { [1] = fontFile, [2] = descriptor }), nextObjectNumber: 3);
        var resources = SingleFontResources(fontDict);

        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td (a) Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            objects);

        Assert.True(Analyze(frame).Total > 0, "alpha should paint through the Foxit Symbol face via Symbol's built-in encoding.");
        Assert.Contains(diagnostics, d => d.Code == "PLUME7510" && d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("failed to parse") && d.Message.Contains("declares no /Encoding"));
    }

    [Fact]
    public void NonEmbeddedType0Symbol_WithCidFontType2Descendant_KeepsLiberationPath_NoException()
    {
        // The Foxit exact-name rule is gated on a Type1/MMType1 /Subtype (isType1Subtype); a
        // Type0 font merely named "Symbol" over a CIDFontType2 descendant with no embedded
        // program is not a Standard-14 symbol font at all and must keep resolving through the
        // ordinary Latin substitution heuristic.
        var cidFont = new PdfDictionary();
        cidFont.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        cidFont.Set(PdfName.Get("Subtype"), PdfName.Get("CIDFontType2"));
        cidFont.Set(PdfName.Get("BaseFont"), PdfName.Get("Symbol"));
        cidFont.Set(PdfName.Get("DW"), PdfNumber.Get(1000));

        var descendants = new PdfArray();
        descendants.Add(cidFont);

        var type0 = new PdfDictionary();
        type0.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        type0.Set(PdfName.Get("Subtype"), PdfName.Get("Type0"));
        type0.Set(PdfName.Get("BaseFont"), PdfName.Get("Symbol"));
        type0.Set(PdfName.Get("Encoding"), PdfName.Get("Identity-H"));
        type0.Set(PdfName.Get("DescendantFonts"), descendants);

        var resources = SingleFontResources(type0);

        var diagnostics = new DiagnosticCollection();
        var exception = Record.Exception(() => Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td <0048> Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            EmptyObjects()));

        Assert.Null(exception);
        Assert.Contains(diagnostics, d => d.Code == "PLUME7510" && d.Message.Contains("Liberation"));
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Foxit"));
    }

    [Fact]
    public void NonEmbeddedTrueTypeSymbol_KeepsLiberationSubstitute_SpecNonGoalPinned()
    {
        // A /Subtype /TrueType font named "Symbol" is PDFium's kMsSymbol mapper case, a different
        // animal from the Type1/MMType1 Standard-14 rule — it's a declared non-goal, left on the
        // ordinary Latin-substitution heuristic rather than the Foxit face.
        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        fontDict.Set(PdfName.Get("Subtype"), PdfName.Get("TrueType"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("Symbol"));

        var resources = SingleFontResources(fontDict);

        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td (A) Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            EmptyObjects());

        Assert.True(Analyze(frame).Total > 0);
        Assert.Contains(diagnostics, d => d.Code == "PLUME7510" && d.Message.Contains("Liberation"));
        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Foxit"));
    }

    // =========================================================================================
    // Embedded bare-CFF (/FontFile3 /Type1C) paths — name-keyed simple
    // rendering (via an explicit /Encoding and via the font's own built-in encoding), the
    // Type0/CID reach of the same stream shape, and a Form-XObject-only reach. All four glyphs
    // are drawn from the same hand-built one-glyph CFF program (CffTestFontBuilder)
    // rather than a duplicate builder.
    // =========================================================================================

    // SID 41 in the CFF standard-strings table (Adobe TN #5176 Appendix A) is the glyph name "H".
    private const int GlyphNameSidH = 41;

    // GID 1 draws a capital 'H' as a single closed 12-point contour (two legs + a crossbar) — the
    // same "looks like an H" shape AssertLooksLikeCapitalH checks for elsewhere in this file, but
    // sourced from a bare CFF program's own Type 2 charstring rather than a TrueType glyf outline.
    private static readonly byte[] CapitalHCharstring = BuildCapitalHCharstring();

    private static byte[] BuildCapitalHCharstring()
    {
        List<byte> cs = [];
        void MoveTo(int dx, int dy)
        {
            cs.AddRange(CffTestFontBuilder.Number(dx));
            cs.AddRange(CffTestFontBuilder.Number(dy));
            cs.Add(21); // rmoveto
        }

        void LineTo(int dx, int dy)
        {
            cs.AddRange(CffTestFontBuilder.Number(dx));
            cs.AddRange(CffTestFontBuilder.Number(dy));
            cs.Add(5); // rlineto
        }

        MoveTo(100, 100);
        LineTo(200, 0);
        LineTo(0, 300);
        LineTo(400, 0);
        LineTo(0, -300);
        LineTo(200, 0);
        LineTo(0, 800);
        LineTo(-200, 0);
        LineTo(0, -300);
        LineTo(-400, 0);
        LineTo(0, 300);
        LineTo(-200, 0);
        cs.Add(14); // endchar (Type 2 charstrings implicitly close the open subpath).
        return [.. cs];
    }

    /// <summary>A bare CFF program: GID 0 <c>.notdef</c>, GID 1 named "H" drawing <see cref="CapitalHCharstring"/>. When <paramref name="builtInEncodesCodeAAsH"/>, the CFF's own Encoding table also maps code 0x41 ('A') to GID 1, for the built-in-encoding (no <c>/Encoding</c> in the PDF) case.</summary>
    private static byte[] BuildType1CCff(bool builtInEncodesCodeAAsH) =>
        CffTestFontBuilder.Build(
            charstrings: [[14], CapitalHCharstring],
            charsetSids: [GlyphNameSidH],
            encodingCodes: builtInEncodesCodeAAsH ? (IReadOnlyList<byte>)[0x41] : null);

    private static PdfStream BuildFontFile3Stream(byte[] cffBytes, string subtype)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Get("Subtype"), PdfName.Get(subtype));
        return new PdfStream(dict, cffBytes);
    }

    [Fact]
    public void BlackH_EmbeddedType1CSimpleFont_WithNamedEncoding_ProducesVerticalBarCoverage()
    {
        // An embedded, non-CID bare CFF (/FontFile3 /Type1C) with an explicit /Encoding: the new
        // name-keyed RenderFont branch resolves code 'H' (0x48, WinAnsiEncoding leaves it as "H")
        // to the CFF's own charset and paints GID 1's own charstring — not a Liberation
        // substitute, so PLUME7510 must not fire.
        var fontFile = BuildFontFile3Stream(BuildType1CCff(builtInEncodesCodeAAsH: false), "Type1C");

        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Get("Type"), PdfName.Get("FontDescriptor"));
        descriptor.Set(PdfName.Get("FontName"), PdfName.Get("TestType1C"));
        descriptor.Set(PdfName.Get("Flags"), PdfNumber.Get(32)); // Nonsymbolic.
        descriptor.Set(PdfName.Get("FontFile3"), new PdfReference(new IndirectReference(1, 0)));

        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        fontDict.Set(PdfName.Get("Subtype"), PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("TestType1C"));
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));
        fontDict.Set(PdfName.Get("FontDescriptor"), new PdfReference(new IndirectReference(2, 0)));

        var objectsDict = new Dictionary<int, PdfObject>
        {
            [1] = fontFile,
            [2] = descriptor,
        };
        var objects = new ObjectRegistry(new InMemoryObjectSource(new PdfDictionary(), objectsDict), nextObjectNumber: 3);

        var resources = new PdfDictionary();
        var fontResources = new PdfDictionary();
        fontResources.Set(PdfName.Get("F1"), fontDict);
        resources.Set(PdfName.Get("Font"), fontResources);

        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td (H) Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            objects);

        AssertLooksLikeCapitalH(frame);
        Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME7510");
    }

    [Fact]
    public void BlackH_EmbeddedType1CSimpleFont_SymbolicNoEncoding_UsesBuiltInEncoding()
    {
        // A symbolic Type1C font with no /Encoding entry at all: EncodingResolver's null-/Encoding
        // branch must use the CFF's own built-in encoding (the non-authoritative builtInBase
        // RenderFontFactory now passes) instead of the opaque symbolic default, so code 0x41 ('A')
        // — which this program's own Encoding table maps to glyph "H" — still resolves through
        // the name-keyed branch.
        var fontFile = BuildFontFile3Stream(BuildType1CCff(builtInEncodesCodeAAsH: true), "Type1C");

        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Get("Type"), PdfName.Get("FontDescriptor"));
        descriptor.Set(PdfName.Get("FontName"), PdfName.Get("TestType1CSymbolic"));
        descriptor.Set(PdfName.Get("Flags"), PdfNumber.Get(4)); // Symbolic.
        descriptor.Set(PdfName.Get("FontFile3"), new PdfReference(new IndirectReference(1, 0)));

        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        fontDict.Set(PdfName.Get("Subtype"), PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("TestType1CSymbolic"));
        // Deliberately no /Encoding entry — this is the case the built-in-encoding fallback exists for.
        fontDict.Set(PdfName.Get("FontDescriptor"), new PdfReference(new IndirectReference(2, 0)));

        var objectsDict = new Dictionary<int, PdfObject>
        {
            [1] = fontFile,
            [2] = descriptor,
        };
        var objects = new ObjectRegistry(new InMemoryObjectSource(new PdfDictionary(), objectsDict), nextObjectNumber: 3);

        var resources = new PdfDictionary();
        var fontResources = new PdfDictionary();
        fontResources.Set(PdfName.Get("F1"), fontDict);
        resources.Set(PdfName.Get("Font"), fontResources);

        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td (A) Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            objects);

        AssertLooksLikeCapitalH(frame);
    }

    [Fact]
    public void BlackH_Type0OverType1CDescendant_TakesCidPath_NoNameLookup()
    {
        // A Type0/CIDFontType0 font whose descendant's /FontFile3 stream is (mis)labelled
        // /Type1C — some producers do this even for a program reached only via a CID wrapper — is
        // reached with isCid: true. This must never fall into the name-keyed branch (a
        // Type0 font has no /Encoding glyph-name table to resolve from in the first place): GID 1
        // is reached purely through code -> CID -> GID (identity: no /CIDToGIDMap, and this
        // particular CFF is not itself CID-keyed so CffParser.CidToGid is null too).
        var fontFile = BuildFontFile3Stream(BuildType1CCff(builtInEncodesCodeAAsH: false), "Type1C");

        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Get("Type"), PdfName.Get("FontDescriptor"));
        descriptor.Set(PdfName.Get("FontName"), PdfName.Get("TestType1COverCid"));
        descriptor.Set(PdfName.Get("Flags"), PdfNumber.Get(4));
        descriptor.Set(PdfName.Get("FontFile3"), new PdfReference(new IndirectReference(1, 0)));

        var cidFont = new PdfDictionary();
        cidFont.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        cidFont.Set(PdfName.Get("Subtype"), PdfName.Get("CIDFontType0"));
        cidFont.Set(PdfName.Get("BaseFont"), PdfName.Get("TestType1COverCid"));
        cidFont.Set(PdfName.Get("DW"), PdfNumber.Get(1000));
        cidFont.Set(PdfName.Get("FontDescriptor"), new PdfReference(new IndirectReference(2, 0)));

        var descendants = new PdfArray();
        descendants.Add(new PdfReference(new IndirectReference(3, 0)));

        var type0 = new PdfDictionary();
        type0.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        type0.Set(PdfName.Get("Subtype"), PdfName.Get("Type0"));
        type0.Set(PdfName.Get("BaseFont"), PdfName.Get("TestType1COverCid"));
        type0.Set(PdfName.Get("Encoding"), PdfName.Get("Identity-H"));
        type0.Set(PdfName.Get("DescendantFonts"), descendants);

        var objectsDict = new Dictionary<int, PdfObject>
        {
            [1] = fontFile,
            [2] = descriptor,
            [3] = cidFont,
        };
        var objects = new ObjectRegistry(new InMemoryObjectSource(new PdfDictionary(), objectsDict), nextObjectNumber: 4);

        var resources = new PdfDictionary();
        var fontResources = new PdfDictionary();
        fontResources.Set(PdfName.Get("F1"), type0);
        resources.Set(PdfName.Get("Font"), fontResources);

        // Identity-H: the two-byte hex code IS the CID; CID 1 -> GID 1 (identity) -> the "H" glyph.
        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(
            Bytes("BT /F1 48 Tf 20 30 Td <0001> Tj ET"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            objects);

        AssertLooksLikeCapitalH(frame);
        Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME7510");
    }

    [Fact]
    public void BlackH_CidKeyedType1C_NonIdentityCharset_MapsCidThroughCharset()
    {
        // The headline CID fix had no end-to-end test. A CID-keyed
        // CIDFontType0C whose charset maps GID 1 -> CID 5: Identity-H code <0005> must paint the
        // "H" at GID 1 (charset consulted), and <0001> — a CID no glyph declares — must paint
        // nothing (unmapped -> GID 0 = .notdef), which is exactly where main's identity assumption
        // painted the wrong glyph.
        var cidCff = CffTestFontBuilder.Build(
            charstrings: [[14], CapitalHCharstring],
            charsetSids: [5],
            charsetFormat: 0,
            cid: new CffTestFontBuilder.CidOptions("Adobe", "Identity", 0, new byte[] { 0, 0 }, 1));
        var fontFile = BuildFontFile3Stream(cidCff, "CIDFontType0C");

        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Get("Type"), PdfName.Get("FontDescriptor"));
        descriptor.Set(PdfName.Get("FontName"), PdfName.Get("TestCidKeyedCff"));
        descriptor.Set(PdfName.Get("Flags"), PdfNumber.Get(4));
        descriptor.Set(PdfName.Get("FontFile3"), new PdfReference(new IndirectReference(1, 0)));

        var cidFont = new PdfDictionary();
        cidFont.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        cidFont.Set(PdfName.Get("Subtype"), PdfName.Get("CIDFontType0"));
        cidFont.Set(PdfName.Get("BaseFont"), PdfName.Get("TestCidKeyedCff"));
        cidFont.Set(PdfName.Get("DW"), PdfNumber.Get(1000));
        cidFont.Set(PdfName.Get("FontDescriptor"), new PdfReference(new IndirectReference(2, 0)));

        var descendants = new PdfArray();
        descendants.Add(new PdfReference(new IndirectReference(3, 0)));

        var type0 = new PdfDictionary();
        type0.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        type0.Set(PdfName.Get("Subtype"), PdfName.Get("Type0"));
        type0.Set(PdfName.Get("BaseFont"), PdfName.Get("TestCidKeyedCff"));
        type0.Set(PdfName.Get("Encoding"), PdfName.Get("Identity-H"));
        type0.Set(PdfName.Get("DescendantFonts"), descendants);

        var objectsDict = new Dictionary<int, PdfObject>
        {
            [1] = fontFile,
            [2] = descriptor,
            [3] = cidFont,
        };

        var resources = new PdfDictionary();
        var fontResources = new PdfDictionary();
        fontResources.Set(PdfName.Get("F1"), type0);
        resources.Set(PdfName.Get("Font"), fontResources);

        RasterImageFrame Render(string content)
        {
            var objects = new ObjectRegistry(new InMemoryObjectSource(new PdfDictionary(), objectsDict), nextObjectNumber: 4);
            return Rasterizer.Rasterize(Bytes(content), resources, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100, PdfOptions.Default, RasterPaintContext.Default, new DiagnosticCollection(), objects);
        }

        AssertLooksLikeCapitalH(Render("BT /F1 48 Tf 20 30 Td <0005> Tj ET"));
        Assert.Equal(0, Analyze(Render("BT /F1 48 Tf 20 30 Td <0001> Tj ET")).Total);
    }

    [Fact]
    public void BlackH_EmbeddedType1_SymbolicNoEncoding_UsesProgramBuiltInEncoding()
    {
        // The Type 1 builtInBase wiring had no end-to-end test. An
        // embedded Type 1 program whose cleartext /Encoding maps code 65 -> /H, on a symbolic
        // font dictionary with NO /Encoding: "(A) Tj" must paint the H through the program's own
        // encoding (previously the opaque symbolic default -> no glyph name -> nothing painted).
        List<byte> t1 = [];
        void Op(int a, int b, byte op)
        {
            t1.AddRange(Type1TestBuilder.Number(a));
            t1.AddRange(Type1TestBuilder.Number(b));
            t1.Add(op);
        }

        Op(0, 1000, 13); // sbx wx hsbw
        Op(100, 100, 21); // rmoveto
        foreach (var (dx, dy) in new[] { (200, 0), (0, 300), (400, 0), (0, -300), (200, 0), (0, 800), (-200, 0), (0, -300), (-400, 0), (0, 300), (-200, 0) })
        {
            Op(dx, dy, 5); // rlineto
        }

        t1.Add(9);  // closepath
        t1.Add(14); // endchar

        var program = Type1TestBuilder.BuildFont(
            encoding: "/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\ndup 65 /H put\nreadonly def\n",
            ("H", [.. t1]));
        var fontFile = new PdfStream(new PdfDictionary(), program);

        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Get("Type"), PdfName.Get("FontDescriptor"));
        descriptor.Set(PdfName.Get("FontName"), PdfName.Get("TestType1BuiltIn"));
        descriptor.Set(PdfName.Get("Flags"), PdfNumber.Get(4)); // symbolic, no /Encoding on the font dict
        descriptor.Set(PdfName.Get("FontFile"), new PdfReference(new IndirectReference(1, 0)));

        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        fontDict.Set(PdfName.Get("Subtype"), PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("TestType1BuiltIn"));
        fontDict.Set(PdfName.Get("FontDescriptor"), new PdfReference(new IndirectReference(2, 0)));

        var objects = new ObjectRegistry(new InMemoryObjectSource(new PdfDictionary(), new Dictionary<int, PdfObject> { [1] = fontFile, [2] = descriptor }), nextObjectNumber: 3);

        var resources = new PdfDictionary();
        var fontResources = new PdfDictionary();
        fontResources.Set(PdfName.Get("F1"), fontDict);
        resources.Set(PdfName.Get("Font"), fontResources);

        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(Bytes("BT /F1 48 Tf 20 30 Td (A) Tj ET"), resources, mediaBoxWidth: 100, mediaBoxHeight: 100, pixelWidth: 100, pixelHeight: 100, PdfOptions.Default, RasterPaintContext.Default, diagnostics, objects);

        AssertLooksLikeCapitalH(frame);
        Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME7510");
    }

    [Fact]
    public void BlackH_EmbeddedType1CFont_ReachedOnlyThroughFormXObjectResources_ProducesVerticalBarCoverage()
    {
        // The same embedded Type1C font shape as the named-encoding case above, but declared only
        // in a Form XObject's own /Resources — never in the page's /Resources — to prove
        // RenderFontFactory/EncodingResolver wiring reaches text painted through a nested Do just
        // as well as page-level text.
        var fontFile = BuildFontFile3Stream(BuildType1CCff(builtInEncodesCodeAAsH: false), "Type1C");

        var descriptor = new PdfDictionary();
        descriptor.Set(PdfName.Get("Type"), PdfName.Get("FontDescriptor"));
        descriptor.Set(PdfName.Get("FontName"), PdfName.Get("TestType1CInForm"));
        descriptor.Set(PdfName.Get("Flags"), PdfNumber.Get(32));
        descriptor.Set(PdfName.Get("FontFile3"), new PdfReference(new IndirectReference(1, 0)));

        var fontDict = new PdfDictionary();
        fontDict.Set(PdfName.Get("Type"), PdfName.Get("Font"));
        fontDict.Set(PdfName.Get("Subtype"), PdfName.Get("Type1"));
        fontDict.Set(PdfName.Get("BaseFont"), PdfName.Get("TestType1CInForm"));
        fontDict.Set(PdfName.Get("Encoding"), PdfName.Get("WinAnsiEncoding"));
        fontDict.Set(PdfName.Get("FontDescriptor"), new PdfReference(new IndirectReference(2, 0)));

        var formFontResources = new PdfDictionary();
        formFontResources.Set(PdfName.Get("F1"), fontDict);
        var formResources = new PdfDictionary();
        formResources.Set(PdfName.Get("Font"), formFontResources);

        var bbox = new PdfArray();
        bbox.Add(PdfNumber.Get(0));
        bbox.Add(PdfNumber.Get(0));
        bbox.Add(PdfNumber.Get(100));
        bbox.Add(PdfNumber.Get(100));

        var formStreamDict = new PdfDictionary();
        formStreamDict.Set(PdfName.Get("Type"), PdfName.Get("XObject"));
        formStreamDict.Set(PdfName.Get("Subtype"), PdfName.Get("Form"));
        formStreamDict.Set(PdfName.Get("BBox"), bbox);
        formStreamDict.Set(PdfName.Get("Resources"), formResources);
        var formStream = new PdfStream(formStreamDict, Bytes("BT /F1 48 Tf 20 30 Td (H) Tj ET"));

        var pageXObjects = new PdfDictionary();
        pageXObjects.Set(PdfName.Get("Fm1"), formStream);
        var resources = new PdfDictionary(); // Page /Resources: no /Font — the font lives only in the form.
        resources.Set(PdfName.Get("XObject"), pageXObjects);

        var objectsDict = new Dictionary<int, PdfObject>
        {
            [1] = fontFile,
            [2] = descriptor,
        };
        var objects = new ObjectRegistry(new InMemoryObjectSource(new PdfDictionary(), objectsDict), nextObjectNumber: 3);

        var diagnostics = new DiagnosticCollection();
        var frame = Rasterizer.Rasterize(
            Bytes("/Fm1 Do"),
            resources,
            mediaBoxWidth: 100,
            mediaBoxHeight: 100,
            pixelWidth: 100,
            pixelHeight: 100,
            PdfOptions.Default, RasterPaintContext.Default,
            diagnostics,
            objects);

        AssertLooksLikeCapitalH(frame);
        Assert.DoesNotContain(diagnostics, d => d.Code == "PLUME7510");
    }
}
