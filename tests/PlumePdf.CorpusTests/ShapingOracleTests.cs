using PlumePdf.Fonts;
using PlumePdf.Fonts.Shaping;
using Xunit;

namespace PlumePdf.CorpusTests;

/// <summary>
/// Hermetic shaping-oracle lane for Phase 6.5's shaper boundary: the primary, always-on, gating half of the dual conformance oracle — no install, no
/// live external process, no fetched corpus beyond the already-pinned Arabic/Devanagari fixture
/// fonts every other shaping test already depends on. <see cref="HbShapeInteropTests"/>
/// is the ARMED, live-process half; this lane instead replays checked-in golden glyph data
/// captured from that same external reference shaper (HarfBuzz's <c>hb-shape</c>, v14.3.1) once,
/// at fixture-authoring time, against <see cref="ComplexShaper"/>'s real output for the identical
/// pinned Noto fonts — so a regression is caught even on a machine with no <c>hb-shape</c>
/// installed at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provenance (NOTICE):</b> the input text for each fixture below is a real word
/// (or, for the two shortest, a canonical linguistic example) chosen in the spirit of HarfBuzz's
/// own MIT-licensed <c>test/shape/data/in-house/tests/</c> corpus — several of these codepoint
/// sequences are the exact sequences HarfBuzz's own <c>arabic-fallback-shaping.tests</c> and
/// <c>indic-syllable.tests</c> exercise (e.g. U+0639,U+0644,U+0649 and
/// U+0926,U+093F,U+0938,U+0902,U+092C,U+0930, respectively; see
/// https://github.com/harfbuzz/harfbuzz/tree/main/test/shape/data/in-house/tests). No HarfBuzz
/// file text is vendored — HarfBuzz's own fixtures pin specific per-test fonts (glyph IDs and
/// glyph names meaningless against Noto), so only the input *codepoints* are drawn from that
/// public corpus; the expected glyph IDs/advances/offsets below are this project's own, captured
/// once against the Noto Naskh Arabic / Noto Sans Devanagari fixture fonts via
/// <c>hb-shape --output-format=json --no-glyph-names</c>, the same tool <see cref="HbShapeInteropTests"/>
/// runs live. See <c>NOTICE</c>'s HarfBuzz entry.
/// </para>
/// <para>
/// <b>Comparison scope:</b> at the shaper boundary — glyph ID, advance width, and GPOS
/// XOffset/YOffset, exactly what a paint pass consumes — never via PlumePDF's own extraction.
/// <see cref="ShapedGlyph.Cluster"/>/<see cref="ShapedGlyph.TextIndex"/> are deliberately NOT
/// compared against hb-shape's own cluster numbering: HarfBuzz clusters a whole reordered Indic
/// syllable under one id, while <see cref="IndicShaper"/> keeps each glyph's own source position
/// (see <c>IndicShaperTests.PreBaseMatra_ReordersBeforeTheBaseConsonant</c>'s remarks) — a
/// deliberate, already-tested difference in bookkeeping convention, not a shaping divergence.
/// </para>
/// <para>
/// <b>Direction:</b> <see cref="ComplexShaper.Shape"/> itself never reverses a run for RTL
/// painting (that is <c>TextLayouter</c>'s job, once per bidi-resolved line); its
/// glyph list stays in logical (input) order regardless of script. hb-shape's own default output
/// for an Arabic (RTL) run *is* already visually reordered, so the three Arabic fixtures' expected
/// glyph lists below are hb-shape's raw output reversed back to logical order before comparison —
/// documented per fixture, not silently baked in.
/// </para>
/// <para>
/// <b>Formerly excluded divergences, now root-caused, fixed, and gated here:</b> the
/// oracle history on mark positioning ran through three states. First, an erroneous
/// unconditional cumulative-advance term inflated every attached mark's XOffset by its base's
/// advance — wrong for Arabic. A later "fix" removed the term unconditionally (anchor delta
/// alone) — which made every Arabic fixture match while leaving the Devanagari reph ("र्क") and
/// anusvara-over-multi-letter-base ("दिसंबर"/"कं") cases exactly one base-advance too far
/// right, the divergence this section used to document as not-root-caused. The actual rule is
/// direction-dependent (HarfBuzz's <c>propagate_attachment_offsets</c> split): a mark's
/// pen-relative offset subtracts the advances between base and mark for a left-to-right-painted
/// run (Devanagari — hence anusvara dx = anchor delta − base advance, e.g. 547 − 768 = −221 for
/// "कं") and adds them (all zero in practice) for a right-to-left-painted run (Arabic — hence
/// anchor delta alone), so each earlier one-direction formula validated against only one
/// script's fixtures. See <c>OpenTypeLayoutEngine.PenRelativeAttachmentDelta</c>'s remarks; the
/// Devanagari mark fixtures below now gate both directions. The earlier "لا" (Lam-Alef)
/// exclusion was likewise root-caused (GSUB non-chaining SequenceContextFormat3 field order —
/// see <c>TryReadLookupRecordsCount</c>'s remarks) and its fixture
/// gates that fix above. The "क्त" fixture gates two further GPOS fixes at once: the
/// first-matching-subtable-wins rule (Noto Sans Devanagari's <c>dist</c> lookup carries a
/// PairPos format-1 AND format-2 subtable that both hold −73 for k+t — applying every subtable
/// doubled it) and the <c>dist</c> adjustment itself flowing into the painted advance.
/// </para>
/// </remarks>
public class ShapingOracleTests
{
    public readonly record struct ExpectedGlyph(int GlyphId, double AdvanceWidth, double XOffset, double YOffset);

    private static ExpectedGlyph G(int glyphId, double advanceWidth, double xOffset = 0, double yOffset = 0) => new(glyphId, advanceWidth, xOffset, yOffset);

    /// <summary>
    /// Arabic fixtures (script <c>arab</c>, <see cref="ScriptTag.Arabic"/>), against
    /// <c>NotoNaskhArabic-Regular.ttf</c>. Each entry's expected glyph list is hb-shape's own
    /// visual-order output reversed to logical order (see this type's remarks).
    /// </summary>
    public static TheoryData<string, string, ExpectedGlyph[]> ArabicFixtures => new()
    {
        {
            // "على" (U+0639,U+0644,U+0649) — "on/upon"; HarfBuzz arabic-fallback-shaping.tests
            // exercises this exact sequence. Pure isol/init/medi/fina joining, no marks.
            "على",
            "on/upon — pure joining, no marks",
            [G(47, 505), G(68, 245), G(100, 687)]
        },
        {
            // "كتاب" (U+0643,U+062A,U+0627,U+0628) — "book". Noto Naskh Arabic renders teh's and
            // beh's dots as separate zero-advance GPOS-attached mark glyphs rather than baking
            // them into the base outline — a real-font mark-attachment case with no vocalization.
            "كتاب",
            "book — dot marks attached via GPOS, no harakat",
            [G(59, 415), G(18, 360), G(294, 0, 53, -444), G(9, 253), G(14, 772), G(323, 0, 308, -31)]
        },
        {
            // "مَعَ" (U+0645,U+064E,U+0639,U+064E) — "with". Two fatha harakat, each attached
            // above a different base letter — the mark-to-base XOffset fix's primary regression
            // guard.
            "مَعَ",
            "with — joining plus two mark-to-base attachments",
            [G(77, 456), G(380, 0, 157, -196), G(45, 477), G(380, 0, 152, -181)]
        },
        {
            // "بِسْمِ" (U+0628,U+0650,U+0633,U+0652,U+0645,U+0650) — the opening word of
            // "Bismillah". Three harakat (kasra, sukun, kasra) across a four-letter joined word.
            "بِسْمِ",
            "opening word of Bismillah — joining plus three mark-to-base attachments",
            [G(19, 275), G(323, 0, 70, -31), G(436, 0, 39, -201), G(34, 663), G(388, 0, 294, -222), G(75, 528), G(436, 0, 238, -62)]
        },
        {
            // "لا" (U+0644,U+0627) — Lam-Alef, Arabic's single most canonical mandatory
            // ligature (a required verification fixture). Was excluded from this
            // gate for the reason this type's remarks used to document: Noto Naskh Arabic's
            // rlig-specific contextual alternate pair (uni0644.init.rlig/uni0627.fina.rlig)
            // wasn't selected — traced to a real OpenTypeLayoutEngine bug (a GSUB LookupType 5
            // Format 3 non-chaining ContextSubst's SubstCount field sits immediately after
            // GlyphCount, *before* the Coverage array, unlike the chaining ChainContextSubst
            // layout the parser assumed uniformly — see TryReadLookupRecordsCount's own remarks)
            // and fixed; this fixture now gates it for real.
            "لا",
            "Lam-Alef — the rlig contextual-alternate ligature",
            [G(71, 218), G(10, 300)]
        },
    };

    /// <summary>
    /// Devanagari fixtures (script <c>deva</c>/<c>dev2</c>, <see cref="ScriptTag.Devanagari"/>),
    /// against <c>NotoSansDevanagari-Regular.ttf</c>. hb-shape's default output for Devanagari
    /// (an LTR-storage script) is already in logical order — no reversal needed.
    /// </summary>
    public static TheoryData<string, string, ExpectedGlyph[]> DevanagariFixtures => new()
    {
        {
            // "क्ष" (U+0915,U+094D,U+0937) — KA+VIRAMA+SSA, a conjunct common enough that Noto
            // Sans Devanagari carries a single dedicated ligature glyph for the whole cluster.
            "क्ष",
            "ka-virama-ssa conjunct — one ligature glyph",
            [G(90, 717)]
        },
        {
            // "कि" (U+0915,U+093F) — KA + pre-base vowel-sign-I: the minimal matra-reorder case
            // (visually matra-then-consonant; logically consonant-then-matra).
            "कि",
            "ka + pre-base matra — minimal reorder",
            [G(545, 259), G(56, 768)]
        },
        {
            // "हिन्दी" (U+0939,U+093F,U+0928,U+094D,U+0926,U+0940) — "Hindi". Pre-base matra
            // reorder plus a न्द half-form conjunct, in a real, recognizable word.
            "हिन्दी",
            "Hindi — matra reorder plus a half-form conjunct",
            [G(544, 259), G(88, 531), G(245, 309), G(73, 531), G(33, 259)]
        },
        {
            // "मित्र" (U+092E,U+093F,U+0924,U+094D,U+0930) — "friend". Pre-base matra reorder
            // plus the त्र (ta-virama-ra) conjunct ligature, another real, recognizable word.
            "मित्र",
            "friend — matra reorder plus the ta-virama-ra conjunct",
            [G(546, 259), G(80, 598), G(304, 552)]
        },
        {
            // "कं" (U+0915,U+0902) — KA + anusvara: the minimal LTR mark-to-base case. The
            // anusvara's pen-relative XOffset is the anchor delta (547) MINUS ka's own advance
            // (768) = -221 — the direction-dependent term the mark-attachment fix exists for
            // (the pre-fix engine painted it at +547, one base-advance too far right).
            "कं",
            "ka + anusvara — LTR mark-to-base with the base-advance compensation",
            [G(56, 768), G(100, 0, -221, 0)]
        },
        {
            // "र्क" (U+0930,U+094D,U+0915) — RA+VIRAMA+KA: the reph case. rphf ligates
            // Ra+Virama into the reph glyph, IndicShaper reorders it after the base, and abvm
            // mark-to-base places it — the exact post-reorder attachment this lane's remarks
            // used to document as a known, excluded divergence.
            "र्क",
            "reph over ka — post-reorder abvm attachment",
            [G(56, 768), G(506, 0, -221, 0)]
        },
        {
            // "दिसंबर" (U+0926,U+093F,U+0938,U+0902,U+092C,U+0930) — "December"; HarfBuzz's
            // indic-syllable.tests exercises this exact sequence. Matra reorder plus an
            // anusvara over a multi-letter word — the other formerly-excluded divergence.
            "दिसंबर",
            "December — matra reorder plus anusvara over a multi-letter base",
            [G(544, 259), G(73, 531), G(87, 676), G(100, 0), G(78, 571), G(82, 409)]
        },
        {
            // "क्त" (U+0915,U+094D,U+0924) — half-KA + TA: Noto Sans Devanagari's 'dist'
            // feature kerns the pair -73 (609 → 536), via a lookup whose format-1 AND format-2
            // PairPos subtables both carry the value — gating first-matching-subtable-wins
            // (applying both used to double the kern) and the dist advance adjustment itself.
            "क्त",
            "half-ka + ta — dist kern via a format-1 + format-2 subtable pair",
            [G(232, 536), G(71, 570)]
        },
    };

    private static string FontPath(string fileName) => Path.Combine(CorpusFixture.CorporaRoot, "fonts", fileName);

    [Theory]
    [MemberData(nameof(ArabicFixtures))]
    public void ComplexShaperOutput_MatchesHermeticHarfBuzzGoldenData_Arabic(string text, string description, ExpectedGlyph[] expected)
    {
        var fontPath = FontPath("NotoNaskhArabic-Regular.ttf");
        if (!File.Exists(fontPath))
        {
            Console.WriteLine($"SKIPPED ({description}): requires {fontPath} — run scripts/fetch-corpora.sh");
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(fontPath));
        var shaped = new ComplexShaper().Shape(text, font, ShapingOptions.Default);

        AssertMatches(expected, shaped, description);
    }

    [Theory]
    [MemberData(nameof(DevanagariFixtures))]
    public void ComplexShaperOutput_MatchesHermeticHarfBuzzGoldenData_Devanagari(string text, string description, ExpectedGlyph[] expected)
    {
        var fontPath = FontPath("NotoSansDevanagari-Regular.ttf");
        if (!File.Exists(fontPath))
        {
            Console.WriteLine($"SKIPPED ({description}): requires {fontPath} — run scripts/fetch-corpora.sh");
            return;
        }

        var font = TrueTypeFontProgram.Parse(File.ReadAllBytes(fontPath));
        var shaped = new ComplexShaper().Shape(text, font, ShapingOptions.Default);

        AssertMatches(expected, shaped, description);
    }

    private static void AssertMatches(IReadOnlyList<ExpectedGlyph> expected, ShapedRun shaped, string description)
    {
        Assert.True(expected.Count == shaped.Glyphs.Count,
            $"{description}: expected {expected.Count} glyphs, got {shaped.Glyphs.Count}.");

        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var g = shaped.Glyphs[i];
            Assert.True(e.GlyphId == g.GlyphId, $"{description}[{i}]: glyph id expected {e.GlyphId}, got {g.GlyphId}.");
            Assert.True(e.AdvanceWidth == g.AdvanceWidth, $"{description}[{i}]: advance expected {e.AdvanceWidth}, got {g.AdvanceWidth}.");
            Assert.True(e.XOffset == g.XOffset, $"{description}[{i}]: XOffset expected {e.XOffset}, got {g.XOffset}.");
            Assert.True(e.YOffset == g.YOffset, $"{description}[{i}]: YOffset expected {e.YOffset}, got {g.YOffset}.");
        }
    }

    [Fact]
    public void ArabicFixtures_IsNonEmpty()
    {
        // Corpus-lesson discipline, applied to a fixed in-source fixture set too (same
        // pattern as HbShapeInteropTests.LatinTestStrings_IsNonEmpty): prove the Theory actually
        // has cases to run rather than trusting an empty TheoryData to read as a vacuous pass.
        Assert.NotEmpty(ArabicFixtures);
    }

    [Fact]
    public void DevanagariFixtures_IsNonEmpty()
    {
        Assert.NotEmpty(DevanagariFixtures);
    }
}
