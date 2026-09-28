using System.Text;
using PlumePdf.Fonts;
using PlumePdf.Objects;
using Xunit;

namespace PlumePdf.Tests.Fonts;

/// <summary>
/// <c>/ToUnicode</c> is cluster-driven rather than
/// the plain per-codepoint <c>cmap</c> walk <see cref="FontObjectBuilder.BuildEmbeddedTrueType"/>
/// used exclusively before this change. The first group below is the regression this fixes —
/// a pre-existing, currently-silent bug: any substituted glyph (starting
/// with today's Latin ligatures) gets no <c>/ToUnicode</c> entry at all, because the
/// independent per-Rune <c>cmap</c> lookup ManuscriptRenderer performs today never resolves to
/// the ligature glyph ID the shaper actually drew. The remaining groups exercise the same
/// cluster-driven CMap logic against Arabic- and Devanagari-shaped codepoint sequences,
/// constructed directly against <see cref="GlyphCluster"/> (no ArabicShaper/
/// IndicShaper exists yet; this proves the ToUnicode *consumer* side of the
/// cluster contract independently of who produces the clusters).
/// </summary>
public class ToUnicodeClusterTests
{
    private static Func<PdfObject, IndirectReference> MakeAllocator(Dictionary<int, PdfObject> objects)
    {
        var next = 1;
        return obj =>
        {
            var number = next++;
            objects[number] = obj;
            return new IndirectReference(number, 0);
        };
    }

    private static string DecodeToUnicodeCMap(Dictionary<int, PdfObject> objects, EmbeddedFontResult result)
    {
        var type0 = (PdfDictionary)objects[result.FontDictionaryReference.Number];
        var toUnicodeRef = (PdfReference)type0[PdfName.Get("ToUnicode")];
        var stream = (PdfStream)objects[toUnicodeRef.Target.Number];
        return Encoding.ASCII.GetString(stream.GetDecodedBytes(PdfFilterRegistry.Default));
    }

    private static int DescriptorFlags(Dictionary<int, PdfObject> objects, EmbeddedFontResult result)
    {
        var type0 = (PdfDictionary)objects[result.FontDictionaryReference.Number];
        var cidFontRef = (PdfReference)((PdfArray)type0[PdfName.Get("DescendantFonts")])[0];
        var cidFont = (PdfDictionary)objects[cidFontRef.Target.Number];
        var descriptorRef = (PdfReference)cidFont[PdfName.Get("FontDescriptor")];
        var descriptor = (PdfDictionary)objects[descriptorRef.Target.Number];
        return (int)((PdfNumber)descriptor[PdfName.Get("Flags")]).ToInt64();
    }

    /// <summary>A <c>bfchar</c> entry's exact text as <see cref="FontObjectBuilder"/> emits it: <c>&lt;CID&gt; &lt;UTF16BE-dest&gt;</c>.</summary>
    private static string BfcharLine(int cid, params int[] codepoints)
    {
        var dest = string.Concat(codepoints.Select(static cp => cp.ToString("X4")));
        return $"<{cid:X4}> <{dest}>";
    }

    // ------------------------------------------------------------------
    // The pre-existing Latin ligature regression.
    // ------------------------------------------------------------------

    [Fact]
    public void FiLigature_LegacyPerCodepointPath_HasNoToUnicodeEntryForTheLigatureGlyph()
    {
        // Establishes the bug this task fixes: without a cluster map, the independent
        // per-Rune cmap lookup ManuscriptRenderer performs today ('f' -> its own glyph, 'i'
        // -> its own glyph) never touches the actual ligature glyph SimpleShaper drew, so
        // that glyph — the one PlumePDF embeds and paints — gets zero /ToUnicode coverage.
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.EbGaramond));
        var run = new SimpleShaper().Shape("fi", font, ShapingOptions.Default);
        var ligatureGlyph = Assert.Single(run.Glyphs);

        Assert.True(font.TryGetGlyphId('f', out var glyphF));
        Assert.True(font.TryGetGlyphId('i', out var glyphI));
        var legacyCodepointToGlyphId = new Dictionary<int, int> { ['f'] = glyphF, ['i'] = glyphI };

        var objects = new Dictionary<int, PdfObject>();
        var result = FontObjectBuilder.BuildEmbeddedTrueType(
            font,
            new HashSet<int> { ligatureGlyph.GlyphId, glyphF, glyphI },
            legacyCodepointToGlyphId,
            MakeAllocator(objects),
            PdfFilterRegistry.Default,
            PdfOptions.Default);

        var cmapText = DecodeToUnicodeCMap(objects, result);
        var ligatureCid = result.GlyphIdMap[ligatureGlyph.GlyphId];
        Assert.DoesNotContain($"<{ligatureCid:X4}>", cmapText, StringComparison.Ordinal);
    }

    [Fact]
    public void FiLigature_ClusterDrivenPath_MapsLigatureGlyphToBothCodepoints()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.EbGaramond));
        var run = new SimpleShaper().Shape("fi", font, ShapingOptions.Default);
        var ligatureGlyph = Assert.Single(run.Glyphs);
        Assert.Equal(2, ligatureGlyph.CodepointCount); // 'f' + 'i' consumed by one ligature glyph.

        var clusters = FontObjectBuilder.BuildClustersFromShapedRun(run, "fi");
        var cluster = Assert.Single(clusters);
        Assert.Equal(ligatureGlyph.GlyphId, cluster.GlyphId);
        Assert.Equal(['f', 'i'], cluster.Codepoints);

        var objects = new Dictionary<int, PdfObject>();
        var result = FontObjectBuilder.BuildEmbeddedTrueType(
            font,
            new HashSet<int> { ligatureGlyph.GlyphId },
            codepointToGlyphId: null,
            MakeAllocator(objects),
            PdfFilterRegistry.Default,
            PdfOptions.Default,
            clusters);

        var cmapText = DecodeToUnicodeCMap(objects, result);
        var ligatureCid = result.GlyphIdMap[ligatureGlyph.GlyphId];
        Assert.Contains(BfcharLine(ligatureCid, 'f', 'i'), cmapText, StringComparison.Ordinal);
    }

    [Fact]
    public void NoClustersSupplied_FallsBackToLegacyMapping_UnaffectedGlyphsStillCovered()
    {
        // Backward compatibility: an ordinary (non-ligature) codepoint must keep working
        // exactly as before when a caller hasn't been updated to supply clusters yet.
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.EbGaramond));
        Assert.True(font.TryGetGlyphId('A', out var glyphA));

        var objects = new Dictionary<int, PdfObject>();
        var result = FontObjectBuilder.BuildEmbeddedTrueType(
            font,
            new HashSet<int> { glyphA },
            new Dictionary<int, int> { ['A'] = glyphA },
            MakeAllocator(objects),
            PdfFilterRegistry.Default,
            PdfOptions.Default);

        var cmapText = DecodeToUnicodeCMap(objects, result);
        var cid = result.GlyphIdMap[glyphA];
        Assert.Contains(BfcharLine(cid, 'A'), cmapText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Arabic-form case: a mandatory ligature (LAM + ALEF -> the single combined glyph every
    // Arabic-capable font carries) is exactly an N-codepoint, one-glyph cluster — the same
    // shape as "fi" above. No ArabicShaper exists yet, so the cluster is constructed
    // directly; this proves FontObjectBuilder's consumer side of the cluster contract.
    // ------------------------------------------------------------------

    [Fact]
    public void ArabicLamAlefLigature_ClusterDrivenToUnicode_MapsBothCodepoints()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('A', out var ligatureGlyphId)); // stand-in glyph — CMap construction doesn't inspect the outline.

        const int lam = 0x0644;
        const int alef = 0x0627;
        var clusters = new[] { new GlyphCluster(ligatureGlyphId, [lam, alef]) };

        var objects = new Dictionary<int, PdfObject>();
        var result = FontObjectBuilder.BuildEmbeddedTrueType(
            font,
            new HashSet<int> { ligatureGlyphId },
            codepointToGlyphId: null,
            MakeAllocator(objects),
            PdfFilterRegistry.Default,
            PdfOptions.Default,
            clusters);

        var cmapText = DecodeToUnicodeCMap(objects, result);
        var cid = result.GlyphIdMap[ligatureGlyphId];
        Assert.Contains(BfcharLine(cid, lam, alef), cmapText, StringComparison.Ordinal);

        // Neither LAM nor ALEF is in WinAnsiEncoding — the descriptor must report Symbolic.
        Assert.Equal(0x04, DescriptorFlags(objects, result));
    }

    // ------------------------------------------------------------------
    // Devanagari-conjunct case: KA + VIRAMA + SSA forming क्ष is a three-codepoint, one-glyph
    // cluster (conjunct formation, not reordering — representable without the cluster-id
    // field, which only reordered 1:N clusters need).
    // ------------------------------------------------------------------

    [Fact]
    public void DevanagariKshaConjunct_ClusterDrivenToUnicode_MapsAllThreeCodepoints()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('B', out var conjunctGlyphId));

        const int ka = 0x0915;
        const int virama = 0x094D;
        const int ssa = 0x0937;
        var clusters = new[] { new GlyphCluster(conjunctGlyphId, [ka, virama, ssa]) };

        var objects = new Dictionary<int, PdfObject>();
        var result = FontObjectBuilder.BuildEmbeddedTrueType(
            font,
            new HashSet<int> { conjunctGlyphId },
            codepointToGlyphId: null,
            MakeAllocator(objects),
            PdfFilterRegistry.Default,
            PdfOptions.Default,
            clusters);

        var cmapText = DecodeToUnicodeCMap(objects, result);
        var cid = result.GlyphIdMap[conjunctGlyphId];
        Assert.Contains(BfcharLine(cid, ka, virama, ssa), cmapText, StringComparison.Ordinal);
        Assert.Equal(0x04, DescriptorFlags(objects, result));
    }

    [Fact]
    public void ReorderedClusterTrailingGlyph_EmptyCodepoints_OmitsBfcharEntryRatherThanErroring()
    {
        // The reordered-cluster rule: the cluster's first glyph carries every codepoint,
        // trailing glyphs of that same cluster carry none — a missing bfchar entry is
        // spec-legal, never a wrong mapping.
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('A', out var firstGlyphId));
        Assert.True(font.TryGetGlyphId('B', out var trailingGlyphId));

        const int matra = 0x093F; // Devanagari vowel sign I (pre-base, reordering matra).
        var clusters = new[]
        {
            new GlyphCluster(firstGlyphId, [matra]),
            new GlyphCluster(trailingGlyphId, []),
        };

        var objects = new Dictionary<int, PdfObject>();
        var result = FontObjectBuilder.BuildEmbeddedTrueType(
            font,
            new HashSet<int> { firstGlyphId, trailingGlyphId },
            codepointToGlyphId: null,
            MakeAllocator(objects),
            PdfFilterRegistry.Default,
            PdfOptions.Default,
            clusters);

        var cmapText = DecodeToUnicodeCMap(objects, result);
        var firstCid = result.GlyphIdMap[firstGlyphId];
        var trailingCid = result.GlyphIdMap[trailingGlyphId];

        Assert.Contains(BfcharLine(firstCid, matra), cmapText, StringComparison.Ordinal);
        Assert.DoesNotContain($"<{trailingCid:X4}>", cmapText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Descriptor Symbolic/Nonsymbolic re-derivation.
    // ------------------------------------------------------------------

    [Fact]
    public void OnlyWinAnsiCodepoints_DescriptorIsNonsymbolic()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('A', out var glyphA));

        var objects = new Dictionary<int, PdfObject>();
        var result = FontObjectBuilder.BuildEmbeddedTrueType(
            font,
            new HashSet<int> { glyphA },
            new Dictionary<int, int> { ['A'] = glyphA },
            MakeAllocator(objects),
            PdfFilterRegistry.Default,
            PdfOptions.Default);

        Assert.Equal(0x20, DescriptorFlags(objects, result));
    }

    [Fact]
    public void NoCodepointProvenanceSupplied_DescriptorDefaultsToNonsymbolic()
    {
        if (!FontFixtures.SkipUnlessAvailable())
        {
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(FontFixtures.NotoSansRegular));
        Assert.True(font.TryGetGlyphId('A', out var glyphA));

        var objects = new Dictionary<int, PdfObject>();
        var result = FontObjectBuilder.BuildEmbeddedTrueType(
            font,
            new HashSet<int> { glyphA },
            codepointToGlyphId: null,
            MakeAllocator(objects),
            PdfFilterRegistry.Default,
            PdfOptions.Default);

        Assert.Equal(0x20, DescriptorFlags(objects, result));
    }
}
