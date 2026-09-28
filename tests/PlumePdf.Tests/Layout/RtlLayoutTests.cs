using System.Globalization;
using System.Text.RegularExpressions;
using PlumePdf.Elements;
using PlumePdf.Fonts;
using PlumePdf.Layout;
using PlumePdf.Objects;
using PlumePdf.Tests.Fonts;
using Xunit;
// See TextLayouter.cs's identical alias note — every bare TextDirection in
// this test file is the public, Auto-capable PlumePdf.Elements.TextDirection.
using TextDirection = PlumePdf.Elements.TextDirection;

namespace PlumePdf.Tests.Layout;

/// <summary>
/// <see cref="Elements.Text.Direction"/>/<see cref="HorizontalAlign.Start"/>/
/// <see cref="HorizontalAlign.End"/>'s public API, <see cref="TextLayouter.ResolveDirection"/>/
/// <see cref="TextLayouter.ShapeVisualLine"/>'s bidi-aware layout+shaping, and end-to-end
/// right-to-left rendering through <see cref="Manuscript.Render(PdfOptions?)"/> — the LTR
/// byte-identity regression, the RTL line anchor and Start/End alignment
/// resolution, determinism for right-to-left content, and grapheme-cluster-safe
/// wrapping. Every test here uses only Standard-14 Helvetica (WinAnsi-safe ASCII/
/// bracket/digit content) except the two explicitly gated on the fetched font corpus, so the
/// suite runs hermetically without <c>scripts/fetch-corpora.sh</c> — the same convention
/// <c>FontFixtures.SkipUnlessAvailable()</c> establishes elsewhere in this project.
/// </summary>
public class RtlLayoutTests
{
    // Hebrew "shalom" and Arabic "hello" — real strongly-directional script text, used only in
    // pure BidiAlgorithm/ResolveDirection string-analysis tests below (never shaped/painted
    // through a font — Standard-14/WinAnsi has no glyphs for either script).
    private const string Hebrew = "שלום";
    private const string Arabic = "مرحبا";

    // === public API ===

    [Fact]
    public void Text_DefaultDirection_IsAuto()
    {
        Assert.Equal(TextDirection.Auto, new Text("hello").Direction);
    }

    [Fact]
    public void HorizontalAlign_HasStartAndEndMembers()
    {
        Assert.True(Enum.IsDefined(HorizontalAlign.Start));
        Assert.True(Enum.IsDefined(HorizontalAlign.End));
    }

    // === TextLayouter.ResolveDirection ===

    [Fact]
    public void ResolveDirection_AutoWithLatinContent_ResolvesLeftToRight()
    {
        Assert.Equal(TextDirection.LeftToRight, TextLayouter.ResolveDirection("Invoice #1042", TextDirection.Auto));
    }

    [Fact]
    public void ResolveDirection_AutoWithHebrewContent_ResolvesRightToLeft()
    {
        Assert.Equal(TextDirection.RightToLeft, TextLayouter.ResolveDirection(Hebrew, TextDirection.Auto));
    }

    [Fact]
    public void ResolveDirection_AutoWithArabicContent_ResolvesRightToLeft()
    {
        Assert.Equal(TextDirection.RightToLeft, TextLayouter.ResolveDirection(Arabic, TextDirection.Auto));
    }

    [Fact]
    public void ResolveDirection_ExplicitRequestOverridesContent()
    {
        // An Arabic invoice line beginning with a product code, forced right-to-left because
        // auto-detection would otherwise see the leading Latin/digits first.
        Assert.Equal(TextDirection.RightToLeft, TextLayouter.ResolveDirection("SKU-100 " + Arabic, TextDirection.RightToLeft));
        Assert.Equal(TextDirection.LeftToRight, TextLayouter.ResolveDirection(Hebrew, TextDirection.LeftToRight));
    }

    // === LayoutEngine resolves and carries direction on MeasuredText ===

    [Fact]
    public void Measure_TextWithAutoDirectionAndLatinContent_ResolvesLeftToRight()
    {
        var measured = (MeasuredText)LayoutEngine.Measure(new Text("Hello"), 200, "Body");
        Assert.Equal(TextDirection.LeftToRight, measured.Direction);
    }

    [Fact]
    public void Measure_TextWithExplicitRightToLeftDirection_CarriesItOnMeasuredText()
    {
        var measured = (MeasuredText)LayoutEngine.Measure(new Text("Hello") { Direction = TextDirection.RightToLeft }, 200, "Body");
        Assert.Equal(TextDirection.RightToLeft, measured.Direction);
    }

    // === ShapeVisualLine — mirroring (L4) + run reorder (L2), Standard-14-safe ===

    [Fact]
    public void ShapeVisualLine_ParenthesizedNumberForcedRightToLeft_MirrorsAndReordersBackToTheSameGlyphSequence()
    {
        // "(50)" as an isolated right-to-left-paragraph example: N0 resolves both brackets to
        // the embedding (right-to-left) direction since the enclosed digits are strong-R for
        // N0's purposes, splitting the line into three level runs ['(':1, '50':2, ')':1]; L2
        // reorders those runs [2,1,0] for painting, and L4 substitutes each bracket for its
        // mirror before shaping. The two effects cancel exactly: a parenthesized number reads
        // the same visually whether embedded in a left-to-right or right-to-left paragraph —
        // this is the real-world-correct answer, not a coincidence of this test's numbers.
        var metrics = PdfFont.Helvetica.Metrics;
        var visual = TextLayouter.ShapeVisualLine("(50)", metrics, TextDirection.RightToLeft);
        var plain = new SimpleShaper().Shape("(50)", metrics, ShapingOptions.Default);

        Assert.Equal(plain.Glyphs.Select(g => g.GlyphId), visual.Glyphs.Select(g => g.GlyphId));
    }

    [Fact]
    public void ShapeVisualLine_ExplicitRightToLeftOverrideInLeftToRightParagraph_ReversesOnlyTheOverriddenGlyphs()
    {
        // "abc" RLO "DEF" PDF "ghi" in a left-to-right paragraph: RLO (unlike plain RLE) forces
        // every character it covers to resolved type R regardless of its own script, so "DEF"
        // — otherwise strongly left-to-right Latin text that I2 would instead bump to an even
        // (left-to-right) level, see the sibling RLE-based test below — keeps the odd
        // (right-to-left) embedding level X2 assigns it. The RLO/PDF format characters
        // themselves are dropped before shaping (BuildRunText's documented "removing" variant —
        // no font maps them to a visible glyph); L2 leaves the three-run sequence's order
        // unchanged (one higher-level run sandwiched between two equal lower-level runs) but
        // reverses the embedded run's own glyphs for painting.
        const char Rlo = (char)0x202E;
        const char Pdf = (char)0x202C;
        var metrics = PdfFont.Helvetica.Metrics;

        var visual = TextLayouter.ShapeVisualLine($"abc{Rlo}DEF{Pdf}ghi", metrics, TextDirection.LeftToRight);
        var expected = new SimpleShaper().Shape("abcFEDghi", metrics, ShapingOptions.Default);

        Assert.Equal(expected.Glyphs.Select(g => g.GlyphId), visual.Glyphs.Select(g => g.GlyphId));
    }

    [Fact]
    public void ShapeVisualLine_PlainRightToLeftEmbeddingOfLatinText_StaysLeftToRightInternally()
    {
        // The contrasting case: plain RLE (no override) only sets the EMBEDDING level odd —
        // "DEF" is still strongly left-to-right Latin text, so I2 bumps it one level higher
        // (to an even level) and it paints in its own natural left-to-right order, unreversed.
        // This is the correct, spec-mandated behavior (implicit resolution wins over a bare
        // embedding with no override), not a gap in this implementation.
        const char Rle = (char)0x202B;
        const char Pdf = (char)0x202C;
        var metrics = PdfFont.Helvetica.Metrics;

        var visual = TextLayouter.ShapeVisualLine($"abc{Rle}DEF{Pdf}ghi", metrics, TextDirection.LeftToRight);
        var expected = new SimpleShaper().Shape("abcDEFghi", metrics, ShapingOptions.Default);

        Assert.Equal(expected.Glyphs.Select(g => g.GlyphId), visual.Glyphs.Select(g => g.GlyphId));
    }

    [Fact]
    public void ShapeVisualLine_MirroredBracketsInRightToLeftRuns_RecordSourceCharactersIntoTheClusterMap()
    {
        // The Phase 6.5 review's /ToUnicode-mirroring regression: "(50)" under a right-to-left
        // base direction resolves both brackets to right-to-left level runs, so L4 substitutes
        // each for its mirror before shaping — the ")" GLYPH is what gets painted where the
        // author wrote "(". /ToUnicode answers "what characters does this glyph represent", so
        // the cluster map must record the SOURCE characters: the painted ")"-glyph maps back to
        // "(" and vice versa. (Recording the mirrored text instead made copy/paste round-trip
        // the mirrored bracket — the bug this test pins.)
        var metrics = PdfFont.Helvetica.Metrics;
        var clusterMap = new GlyphClusterMap();
        TextLayouter.ShapeVisualLine("(50)", metrics, TextDirection.RightToLeft, clusterMap);

        var shaper = new SimpleShaper();
        var openParenGlyph = shaper.Shape("(", metrics, ShapingOptions.Default).Glyphs[0].GlyphId;
        var closeParenGlyph = shaper.Shape(")", metrics, ShapingOptions.Default).Glyphs[0].GlyphId;

        Assert.Equal("(", clusterMap.GlyphIdToCodepoints[closeParenGlyph]);
        Assert.Equal(")", clusterMap.GlyphIdToCodepoints[openParenGlyph]);
    }

    [Fact]
    public void ExtractText_RightToLeftLineWithMirroredParenthesis_RoundTripsTheSourceCharacter()
    {
        // End-to-end half of the mirroring regression above (corpus-gated): an Arabic line whose
        // trailing "«" (LEFT-POINTING GUILLEMET — a mirrored pair NotoNaskhArabic actually
        // carries glyphs for, unlike ASCII parentheses) resolves right-to-left (neutral between
        // strong-R text and the line edge under an RTL base direction), so the "»" glyph is
        // painted — extraction must still yield the source "«" via the cluster-driven
        // /ToUnicode, never the mirrored "»".
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
                    Body = new Text(Arabic + " «") { Font = arabicFont, FontSize = 16, Direction = TextDirection.RightToLeft },
                },
            ],
        };

        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-mirror-{Guid.NewGuid():N}.pdf");
        try
        {
            using (var document = manuscript.Render(PdfOptions.Default))
            {
                document.Save(path, PdfOptions.Default);
            }

            var extracted = Pdf.ExtractText(path);
            Assert.Contains('«', extracted); // « — the source character the author wrote
            Assert.DoesNotContain('»', extracted); // » — the mirrored glyph that was painted
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ShapeVisualLine_PureLeftToRightLine_MatchesPlainShapeExactly()
    {
        // The LTR regression, at the shaping layer directly: one run, shaped once, byte-for-byte
        // what pre-Phase-6.5 SimpleShaper.Shape produced.
        var metrics = PdfFont.Helvetica.Metrics;
        var visual = TextLayouter.ShapeVisualLine("Invoice #1042", metrics, TextDirection.LeftToRight);
        var plain = new SimpleShaper().Shape("Invoice #1042", metrics, ShapingOptions.Default);

        Assert.Equal(plain.Glyphs, visual.Glyphs);
    }

    [Fact]
    public void ShapeVisualLine_EmptyLine_ReturnsZeroWidthAndNoGlyphs()
    {
        var visual = TextLayouter.ShapeVisualLine(string.Empty, PdfFont.Helvetica.Metrics, TextDirection.LeftToRight);
        Assert.Equal(0, visual.Width);
        Assert.Empty(visual.Glyphs);
    }

    // === end-to-end render — RTL line anchor + Start/End alignment resolution ===

    [Fact]
    public void Render_StartAlignedRightToLeftText_AnchorsToThePhysicalRightEdge()
    {
        const double pageWidth = 300;
        const double margin = 20;
        var manuscript = BuildSinglePageManuscript(pageWidth, "HELLO", TextDirection.RightToLeft, align: null, margin);

        using var document = manuscript.Render(PdfOptions.Default);
        var x = Assert.Single(ExtractTmXCoordinates(DecodedPageContent(document)));

        var contentWidth = pageWidth - (2 * margin);
        var lineWidth = TextLayouter.MeasureText("HELLO", 12, PdfFont.Helvetica.Metrics);
        var expectedX = margin + (contentWidth - lineWidth);

        Assert.Equal(expectedX, x, precision: 1);
    }

    [Fact]
    public void Render_StartAlignedLeftToRightText_AnchorsToThePhysicalLeftEdge()
    {
        const double pageWidth = 300;
        const double margin = 20;
        var manuscript = BuildSinglePageManuscript(pageWidth, "HELLO", TextDirection.LeftToRight, align: null, margin);

        using var document = manuscript.Render(PdfOptions.Default);
        var x = Assert.Single(ExtractTmXCoordinates(DecodedPageContent(document)));

        Assert.Equal(margin, x, precision: 1);
    }

    [Fact]
    public void Render_EndAlignedRightToLeftText_AnchorsToThePhysicalLeftEdge()
    {
        // End on a right-to-left paragraph is the trailing edge, i.e. physical left — the
        // mirror image of Start.
        const double pageWidth = 300;
        const double margin = 20;
        var manuscript = BuildSinglePageManuscript(pageWidth, "HELLO", TextDirection.RightToLeft, HorizontalAlign.End, margin);

        using var document = manuscript.Render(PdfOptions.Default);
        var x = Assert.Single(ExtractTmXCoordinates(DecodedPageContent(document)));

        Assert.Equal(margin, x, precision: 1);
    }

    [Fact]
    public void Render_LeftAlignStaysPhysicalLeftUnderRightToLeftDirection()
    {
        // The pinned rule: Left never gets reinterpreted as a logical side, even when the
        // paragraph itself is right-to-left.
        const double pageWidth = 300;
        const double margin = 20;
        var manuscript = BuildSinglePageManuscript(pageWidth, "HELLO", TextDirection.RightToLeft, HorizontalAlign.Left, margin);

        using var document = manuscript.Render(PdfOptions.Default);
        var x = Assert.Single(ExtractTmXCoordinates(DecodedPageContent(document)));

        Assert.Equal(margin, x, precision: 1);
    }

    [Fact]
    public void Render_TwiceWithRightToLeftContent_ProducesByteIdenticalOutput()
    {
        Manuscript Build() => BuildSinglePageManuscript(300, "HELLO (2024) WORLD", TextDirection.RightToLeft, align: null, margin: 20);

        var options = PdfOptions.Default with { Deterministic = true };
        var bytesA = RenderToBytes(Build(), options);
        var bytesB = RenderToBytes(Build(), options);

        Assert.Equal(bytesA, bytesB);
    }

    [Fact]
    public void Render_DefaultsVsExplicitLeftToRightStart_ProducesByteIdenticalOutput()
    {
        // A Text left at every Phase-6.5 default (Direction = Auto resolving
        // left-to-right for Latin content, Align = null) renders identically to the same content
        // with the new properties set explicitly to what they resolve to — the additive members
        // change nothing for a document that never touches them.
        var options = PdfOptions.Default with { Deterministic = true };
        var defaults = BuildSinglePageManuscript(300, "Invoice #1042 (Q3)", TextDirection.Auto, align: null, margin: 20);
        var explicitLtr = BuildSinglePageManuscript(300, "Invoice #1042 (Q3)", TextDirection.LeftToRight, HorizontalAlign.Start, 20);

        Assert.Equal(RenderToBytes(defaults, options), RenderToBytes(explicitLtr, options));
    }

    // === grapheme-cluster-safe hard-break wrapping (corpus-gated) ===

    [Fact]
    public void WrapLines_LongUnbrokenWordWithCombiningMarks_NeverStartsAWrappedLineWithACombiningMark()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        // 60 decomposed "é" clusters (base 'e' + COMBINING ACUTE ACCENT), one unbroken "word" —
        // forces WrapParagraph's hard-break path to fire repeatedly across a narrow width.
        var word = string.Concat(Enumerable.Repeat("é", 60));

        var lines = TextLayouter.WrapLines(word, fontSize: 12, font, availableWidth: 40);

        Assert.True(lines.Count > 1, "the word should have hard-broken across multiple lines for this test to be meaningful");
        foreach (var line in lines.Skip(1))
        {
            Assert.True(line.Length > 0);
            var category = CharUnicodeInfo.GetUnicodeCategory(line[0]);
            Assert.False(
                category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark,
                $"line '{line}' starts with a combining mark — a base+mark cluster was split across a hard break.");
        }
    }

    [Fact]
    public void WrapLines_EmbeddedLatinFontWithKerningAndLigatures_AccumulatedWrapWidthsMatchWholeLineShaping()
    {
        // Guards the Phase 6.5 review's throughput fix: plain left-to-right paragraphs wrap by
        // per-word width accumulation (each word shaped once) instead of re-shaping the whole
        // candidate line — exact so long as the font doesn't ligate or kern against the space
        // glyph (the same additivity the pre-6.5 TextLayouter shipped). NotoSans carries real
        // GSUB ligatures ("fi") and GPOS kerning, so this asserts the accumulated decisions
        // still produce lines whose true (whole-line, bidi-aware) shaped width fits the
        // available width the wrap promised.
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        const double availableWidth = 90;
        const double fontSize = 12;
        var lines = TextLayouter.WrapLines(
            "The quick brown fox jumps over five lazy dogs while affix officers verify waffle certificates",
            fontSize,
            font,
            availableWidth);

        Assert.True(lines.Count > 1);
        foreach (var line in lines)
        {
            var shapedWidth = TextLayouter.ShapeVisualLine(line, font, TextDirection.LeftToRight).Width * fontSize / font.UnitsPerEm;
            Assert.True(
                shapedWidth <= availableWidth + 0.01,
                $"line '{line}' re-shapes to {shapedWidth:0.###}pt, wider than the {availableWidth}pt the wrap promised — per-word accumulation diverged from whole-line shaping.");
        }
    }

    [Fact]
    public void ShapeVisualLine_RightToLeftRunWithKerning_ReattributesTheKernGapToTheCorrectVisuallyAdjacentPair()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        // NotoSans-Regular's GPOS 'kern' carries a -20 unit adjustment for the (b, v) pair
        // (the same fixture fact SimpleShaperTests.KerningPair_AltersFirstGlyphAdvance pins).
        // Plain Latin "bv" never resolves to a right-to-left level run on its own — I2 bumps
        // strong-L characters to an even (left-to-right) level regardless of the paragraph's
        // own base direction, exactly as the sibling PlainRightToLeftEmbeddingOfLatinText test
        // above documents; only an explicit RLO override (X6) forces the level odd by fiat.
        // An RLO/PDF wrap is the correct way to force a real right-to-left run here, the same
        // technique the ExplicitOverride test above already exercises. Forced right-to-left,
        // "bv" reverses to paint as [v, b] — the -20 gap must move with it, landing after 'v'
        // (now first) rather than staying on 'b'.
        const char Rlo = (char)0x202E;
        const char Pdf = (char)0x202C;
        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        var plain = new SimpleShaper().Shape("bv", font, ShapingOptions.Default);
        var visual = TextLayouter.ShapeVisualLine($"{Rlo}bv{Pdf}", font, TextDirection.LeftToRight);

        Assert.Equal(2, visual.Glyphs.Count);
        Assert.Equal(plain.Glyphs[1].GlyphId, visual.Glyphs[0].GlyphId); // 'v' painted first
        Assert.Equal(plain.Glyphs[0].GlyphId, visual.Glyphs[1].GlyphId); // 'b' painted second
        Assert.Equal(plain.Glyphs[0].KernAdjustment, visual.Glyphs[0].KernAdjustment); // the -20 gap moved to the new first glyph
        Assert.Equal(0, visual.Glyphs[1].KernAdjustment);

        // The sum of advances (what LayoutEngine measures with) is unchanged by the reorder —
        // measured width equals painted width by construction.
        Assert.Equal(plain.Glyphs.Sum(g => g.AdvanceWidth), visual.Glyphs.Sum(g => g.AdvanceWidth), precision: 6);
    }

    private static Manuscript BuildSinglePageManuscript(double pageWidth, string content, TextDirection direction, HorizontalAlign? align, double margin) =>
        new()
        {
            Sections =
            [
                new Section
                {
                    PageSize = new PageSize(pageWidth, 200),
                    Margins = Margins.Uniform(margin),
                    Body = new Text(content) { FontSize = 12, Direction = direction, Align = align },
                },
            ],
        };

    private static byte[] RenderToBytes(Manuscript manuscript, PdfOptions options)
    {
        var path = Path.Combine(Path.GetTempPath(), $"plumepdf-rtl-{Guid.NewGuid():N}.pdf");
        using var document = manuscript.Render(options);
        document.Save(path, options);
        try
        {
            return File.ReadAllBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
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

    private static double[] ExtractTmXCoordinates(byte[] contentBytes)
    {
        var text = System.Text.Encoding.Latin1.GetString(contentBytes);
        var matches = Regex.Matches(text, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) Tm");
        return [.. matches.Select(m => double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture))];
    }
}
