using System.Text;
using PlumePdf.Elements;
using PlumePdf.Fonts;
using Xunit;

namespace PlumePdf.Tests.Fonts;

/// <summary>Regressions for the Phase 2 review-round findings fixed after max-rounds.</summary>
public class ReviewRegressionTests
{
    [Fact]
    public void Subsetter_AlphabetBeyondFormat4Capacity_ThrowsCodedNotOverflow()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        // Finding: 16 + 8*(entries+1) is written checked((ushort)…) — >8189 distinct mapped
        // codepoints (a legitimate CJK-scale alphabet) threw a bare OverflowException.
        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('A', out var glyphA));

        var manyCodepoints = new Dictionary<int, int>();
        for (var cp = 0x4E00; cp < 0x4E00 + 9000; cp++)
        {
            manyCodepoints[cp] = glyphA; // many codepoints, one retained glyph — inside MaxSubsetGlyphs
        }

        var ex = Assert.Throws<PlumePdfException>(
            () => FontSubsetter.Subset(font, new HashSet<int> { glyphA }, manyCodepoints));

        Assert.Equal("PLUME8013", ex.Code);
    }

    [Fact]
    public void ToUnicode_SupplementaryPlaneCodepoint_EmitsSurrogatePair()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        // Finding: X4 formatting emitted a 5-digit (odd-length) bfchar destination for
        // astral codepoints, decoding as garbage in every text extractor.
        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('A', out var glyphA));

        const int astral = 0x10780; // maps in NotoSans-Regular per fetch-corpora.sh
        Assert.True(font.TryGetGlyphId(astral, out var astralGlyph), "fixture font must map U+10780");

        var objects = new Dictionary<int, PdfObject>();
        var number = 0;
        IndirectReference Allocate(PdfObject value)
        {
            objects[++number] = value;
            return new IndirectReference(number, 0);
        }

        var result = FontObjectBuilder.BuildEmbeddedTrueType(
            font,
            new HashSet<int> { glyphA, astralGlyph },
            new Dictionary<int, int> { ['A'] = glyphA, [astral] = astralGlyph },
            Allocate,
            PdfFilterRegistry.Default,
            PdfOptions.Default);

        var toUnicode = objects.Values.OfType<PdfStream>()
            .Select(s => Encoding.ASCII.GetString(s.GetDecodedBytes(PdfFilterRegistry.Default)))
            .Single(static text => text.Contains("beginbfchar", StringComparison.Ordinal));

        Assert.Contains("D801DF80", toUnicode.Replace(" ", "", StringComparison.Ordinal)); // U+10780 as UTF-16BE pair
        Assert.DoesNotContain("<10780>", toUnicode, StringComparison.Ordinal);
        _ = result;
    }

    [Fact]
    public void Render_KernedPair_PaintsWithTjAdjustments()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        // Finding: GPOS kerning was folded into MEASUREMENT but painted with plain Tj, so
        // measured and painted widths diverged. A kerned line must paint via TJ.
        var font = PdfFont.FromFile(FontFixtures.NotoSansRegular);
        var manuscript = new Manuscript
        {
            Sections =
            [
                new Section
                {
                    Body = new Text("AVATAR Wave To Yo.") { Font = font, FontSize = 12 },
                },
            ],
        };

        using var document = manuscript.Render();
        var page = Assert.Single(document.Pages);
        var contentsRef = Assert.IsType<PdfReference>(page.Dictionary[PdfName.Get("Contents")]);
        var contentStream = Assert.IsType<PdfStream>(document.Objects[contentsRef.Target]);
        var operators = Encoding.ASCII.GetString(contentStream.GetDecodedBytes(PdfFilterRegistry.Default));

        Assert.Contains("] TJ", operators, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_SecondContentCall_ThrowsCoded()
    {
        // Finding: repeated descriptor calls silently discarded earlier content,
        // contradicting the fail-fast policy.
        var ex = Assert.Throws<PlumePdfException>(() => PdfDocument.Compose(page =>
        {
            page.Content().Text("a");
            page.Content().Text("b");
        }));

        Assert.Equal("PLUME9009", ex.Code);

        var ex2 = Assert.Throws<PlumePdfException>(() => PdfDocument.Compose(page =>
        {
            var content = page.Content();
            content.Text("x");
            content.Row(static _ => { });
        }));

        Assert.Equal("PLUME9009", ex2.Code);
    }
}
