using PlumePdf.Fonts.Shaping;
using Xunit;

namespace PlumePdf.Tests.Fonts;

/// <summary>
/// Spot-checks the source-generated <see cref="UnicodeShapingData"/> tables
/// (<c>scripts/generate-unicode-data.csx</c>) against known Unicode Character Database values —
/// a verification step, not a substitute for the UCD conformance oracles (BidiTest,
/// the hermetic shaping-oracle fixtures) added elsewhere.
/// </summary>
public class UnicodeShapingDataTests
{
    [Fact]
    public void ArabicBeh_HasJoiningTypeDualJoining()
    {
        // U+0628 ARABIC LETTER BEH — a worked example:
        // Joining_Type = D (Dual_Joining), per ArabicShaping.txt.
        Assert.Equal(JoiningType.D, UnicodeShapingData.GetJoiningType(0x0628));
        Assert.Equal(JoiningGroup.BEH, UnicodeShapingData.GetJoiningGroup(0x0628));
        Assert.Equal(UnicodeScript.Arabic, UnicodeShapingData.GetScript(0x0628));
    }

    [Fact]
    public void DevanagariVowelSignI_HasLeftPositionalCategory()
    {
        // U+093F DEVANAGARI VOWEL SIGN I — a worked example: Indic_Positional_Category
        // = Left (it visually renders to the left of its base consonant, per IndicPositionalCategory.txt).
        Assert.Equal(IndicPositionalCategory.Left, UnicodeShapingData.GetIndicPositionalCategory(0x093F));
        Assert.Equal(UnicodeScript.Devanagari, UnicodeShapingData.GetScript(0x093F));
    }

    [Fact]
    public void ArabicLetters_HaveArabicLetterBidiClass()
    {
        // U+0627 ARABIC LETTER ALEF — Bidi_Class = AL (Arabic_Letter) per UAX #9/DerivedBidiClass.txt.
        Assert.Equal(BidiClass.AL, UnicodeShapingData.GetBidiClass(0x0627));
    }

    [Fact]
    public void LatinDigits_HaveEuropeanNumberBidiClass()
    {
        // U+0030 DIGIT ZERO — Bidi_Class = EN (European_Number); this is exactly why UAX #9 bidi
        // matters for "Arabic + digits" documents, the headline motivating case.
        Assert.Equal(BidiClass.EN, UnicodeShapingData.GetBidiClass('0'));
    }

    [Fact]
    public void CombiningAcuteAccent_HasNonZeroCombiningClass()
    {
        // U+0301 COMBINING ACUTE ACCENT — Canonical_Combining_Class = 230 (Above).
        Assert.Equal(230, UnicodeShapingData.GetCombiningClass(0x0301));

        // A base Latin letter has combining class 0 (not reordered).
        Assert.Equal(0, UnicodeShapingData.GetCombiningClass('A'));
    }

    [Fact]
    public void ParenIsMirroredUnderRtl()
    {
        // U+0028 LEFT PARENTHESIS mirrors to U+0029 RIGHT PARENTHESIS under UAX #9 L4.
        Assert.True(UnicodeShapingData.TryGetBidiMirror(0x0028, out var mirror));
        Assert.Equal(0x0029, mirror);

        // An ordinary Latin letter has no mirror glyph.
        Assert.False(UnicodeShapingData.TryGetBidiMirror('A', out _));
    }

    [Fact]
    public void DevanagariConsonant_HasConsonantSyllabicCategory()
    {
        // U+0915 DEVANAGARI LETTER KA — Indic_Syllabic_Category = Consonant.
        Assert.Equal(IndicSyllabicCategory.Consonant, UnicodeShapingData.GetIndicSyllabicCategory(0x0915));
    }

    [Fact]
    public void DevanagariVirama_HasGraphemeExtendBreakProperty()
    {
        // U+094D DEVANAGARI SIGN VIRAMA — Grapheme_Cluster_Break = Extend, so UAX #29-safe line
        // wrapping never splits a consonant from a following virama.
        Assert.Equal(GraphemeClusterBreak.Extend, UnicodeShapingData.GetGraphemeClusterBreak(0x094D));
    }

    [Fact]
    public void UnassignedPrivateUseCodepoint_FallsBackToDefaultScriptValue()
    {
        // U+E000 (start of the Private Use Area) has Script = Unknown in Scripts.txt — the
        // property tables must not throw or silently return a wrong specific script for it.
        Assert.Equal(UnicodeScript.Unknown, UnicodeShapingData.GetScript(0xE000));
    }
}
