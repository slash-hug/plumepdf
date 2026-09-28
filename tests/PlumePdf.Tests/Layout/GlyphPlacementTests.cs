using System.Globalization;
using System.Text.RegularExpressions;
using PlumePdf.Elements;
using PlumePdf.Fonts;
using PlumePdf.Layout;
using PlumePdf.Objects;
using PlumePdf.Tests.Fonts;
using Xunit;
using TextDirection = PlumePdf.Elements.TextDirection;

namespace PlumePdf.Tests.Layout;

/// <summary>
/// A golden content-stream fragment test: proves GPOS mark placement
/// (<see cref="Fonts.ShapedGlyph.XOffset"/>/<see cref="Fonts.ShapedGlyph.YOffset"/>) is actually
/// emitted into the painted content stream (<c>Ts</c>/<c>TJ</c> — see
/// <see cref="ManuscriptRenderer"/>'s <c>ShowLine</c> own remarks), not just computed and
/// discarded — the bug this type exists to close ("every mark the shaper attaches...
/// is painted at the pen position with zero X/Y offset").
/// </summary>
public class GlyphPlacementTests
{
    [Fact]
    public void ArabicMarkAttachment_EmitsATsOperatorWithTheShapersComputedYOffset()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var arabicFont = PdfFont.FromFile(FontFixtures.NotoNaskhArabic);
        const double fontSize = 16;

        var manuscript = new Manuscript
        {
            Sections =
            [
                new Section
                {
                    PageSize = new PageSize(300, 200),
                    Margins = Margins.Uniform(20),
                    // "مَعَ" ("with") — the same real-word fixture ShapingOracleTests pins as
                    // golden data: two fatha harakat, each attached above a different base
                    // letter. hb-shape's own captured anchor math gives the second fatha (the
                    // one attached to the AIN base) YOffset -196 font units — at fontSize 16
                    // against this font's 1000 unitsPerEm, that is exactly -3.136 points of Ts.
                    Body = new Text("مَعَ") { Font = arabicFont, FontSize = fontSize, Direction = TextDirection.RightToLeft },
                },
            ],
        };

        using var document = manuscript.Render(PdfOptions.Default);
        var contentBytes = DecodedPageContent(document);
        var contentText = System.Text.Encoding.Latin1.GetString(contentBytes);

        var tsMatches = Regex.Matches(contentText, @"(-?[\d.]+) Ts");
        Assert.True(tsMatches.Count > 0, $"Expected at least one Ts (text rise) operator in the content stream; got none. Content:\n{contentText}");

        var tsValues = tsMatches.Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();
        Assert.Contains(-3.136, tsValues);

        // Every emitted Ts must eventually be reset to 0 before the text object ends (ManuscriptRenderer's
        // ShowLine/EmitPage contract) — a leftover nonzero rise would misplace whatever paints next.
        Assert.Equal(0.0, tsValues[^1]);
    }

    [Fact]
    public void DevanagariDistKerning_IsPaintedAsATjAdjustment_AndMeasuredWidthAgrees()
    {
        // "क्त" (half-KA + TA): Noto Sans Devanagari's 'dist' feature kerns the pair -73 font
        // units (hb-shape ground truth: half-ka 609 -> 536; ShapingOracleTests pins the shaper
        // boundary). The GPOS XAdvance delta is folded into ShapedGlyph.AdvanceWidth for
        // measurement AND surfaced as ShapedGlyph.KernAdjustment so ShowLine reproduces it as
        // a TJ adjustment — this test is the regression guard for the state where the delta
        // affected wrap/alignment math but never reached the content stream (measured and
        // painted geometry disagreed by exactly the kern).
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var devanagariFont = PdfFont.FromFile(FontFixtures.NotoSansDevanagari);
        const string word = "क्त";

        // Measured side: the shaped line's width must include the dist kern.
        // hb-shape ground truth: 536 + 570 = 1106 font units (the unkerned hmtx sum is
        // 609 + 570 = 1179).
        var visual = TextLayouter.ShapeVisualLine(word, devanagariFont.Metrics, TextDirection.LeftToRight);
        Assert.Equal(1106.0, visual.Width);
        Assert.Equal(-73.0, visual.Glyphs[0].KernAdjustment);

        var manuscript = new Manuscript
        {
            Sections =
            [
                new Section
                {
                    PageSize = new PageSize(300, 200),
                    Margins = Margins.Uniform(20),
                    Body = new Text(word) { Font = devanagariFont, FontSize = 16 },
                },
            ],
        };

        using var document = manuscript.Render(PdfOptions.Default);
        var contentText = System.Text.Encoding.Latin1.GetString(DecodedPageContent(document));

        // Painted side: the kern must appear as a TJ adjustment. ToAdjustment negates the
        // font-unit delta and scales by 1000/unitsPerEm (= 1000 here): -(-73) = 73. The viewer
        // then paints half-ka's full /W width (609) and pulls the pen back 73/1000 em, so the
        // painted advance is the same 536 the measured width used.
        var tjArrays = Regex.Matches(contentText, @"\[[^\]]*\]\s*TJ").Select(m => m.Value).ToArray();
        Assert.True(tjArrays.Length > 0, $"Expected a TJ (show with adjustments) operator in the content stream; got none. Content:\n{contentText}");
        Assert.Contains(tjArrays, tj => Regex.IsMatch(tj, @"(?<![\d.-])73(?![\d.])"));
    }

    [Fact]
    public void ArabicLigature_GetsARealClusterDrivenToUnicodeEntry_NotSilentlyMissing()
    {
        // Production wiring, end to end: GlyphClusterMap.cs (the cluster contract)
        // used to have no production consumer at all — ManuscriptRenderer built /ToUnicode from
        // an independent per-Rune cmap walk keyed by each codepoint's *isolated* cmap glyph ID.
        // "لا" (lam-alef) shapes to two rlig-*substituted* glyphs (lam.init.rlig, alef.fina.rlig)
        // — neither equals either letter's isolated cmap glyph ID, so the old per-Rune fallback
        // would silently produce zero /ToUnicode entries for them (the exact gap
        // ToUnicodeClusterTests.FiLigature_LegacyPerCodepointPath_... proves still exists on the
        // *unwired* FontObjectBuilder call directly). Cluster-driven /ToUnicode keys each glyph
        // by what the shaper actually traced it back to, so both codepoints get a real entry.
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var arabicFont = PdfFont.FromFile(FontFixtures.NotoNaskhArabic);
        var manuscript = new Manuscript
        {
            Sections =
            [
                new Section
                {
                    PageSize = new PageSize(300, 200),
                    Margins = Margins.Uniform(20),
                    Body = new Text("لا") { Font = arabicFont, FontSize = 16, Direction = TextDirection.RightToLeft },
                },
            ],
        };

        using var document = manuscript.Render(PdfOptions.Default);
        var toUnicodeText = DecodedToUnicodeCMapText(document);

        // bfchar entries are UTF-16BE hex codepoints; both letters must resolve to a real entry.
        Assert.Contains("<0627>", toUnicodeText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<0644>", toUnicodeText, StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodedToUnicodeCMapText(PdfDocument document)
    {
        var page = document.Pages[0];
        Assert.True(page.Dictionary.TryGetValue(PdfName.Get("Resources"), out var resourcesValue));
        var resources = resourcesValue as PdfDictionary ?? (document.Objects[((PdfReference)resourcesValue).Target] as PdfDictionary);
        Assert.NotNull(resources);
        Assert.True(resources!.TryGetValue(PdfName.Get("Font"), out var fontDictValue));
        var fontDict = fontDictValue as PdfDictionary ?? (document.Objects[((PdfReference)fontDictValue).Target] as PdfDictionary);
        Assert.NotNull(fontDict);

        var fontRef = Assert.Single(fontDict!.Values.OfType<PdfReference>());
        var font = document.Objects[fontRef.Target] as PdfDictionary;
        Assert.NotNull(font);
        Assert.True(font!.TryGetValue(PdfName.Get("ToUnicode"), out var toUnicodeValue));
        var toUnicodeStream = document.Objects[((PdfReference)toUnicodeValue).Target] as PdfStream;
        Assert.NotNull(toUnicodeStream);

        var bytes = toUnicodeStream!.GetDecodedBytes(PdfFilterRegistry.Default, PdfOptions.Default);
        return System.Text.Encoding.Latin1.GetString(bytes);
    }

    private static byte[] DecodedPageContent(PdfDocument document)
    {
        var page = document.Pages[0];
        Assert.True(page.Dictionary.TryGetValue(PdfName.Get("Contents"), out var contentsValue));
        var stream = contentsValue switch
        {
            PdfReference reference => document.Objects[reference.Target] as PdfStream,
            _ => null,
        };
        Assert.NotNull(stream);
        return stream!.GetDecodedBytes(PdfFilterRegistry.Default, PdfOptions.Default);
    }
}
