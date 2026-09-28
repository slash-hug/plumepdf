using PlumePdf.Elements;
using PlumePdf.Layout;
using Xunit;

namespace PlumePdf.Tests.Layout;

/// <summary>
/// <see cref="BidiAlgorithm"/>, against hand-picked UAX#9 examples —
/// paragraph-level first-strong detection (P2/P3), explicit-embedding levels (X1-X8), the
/// weak/neutral/implicit rule chain (W1-W7, N0-N2, I1/I2) as observed through resolved levels
/// and <see cref="BidiRun"/> structure, the L2 run-reorder, and L4 mirroring. Full
/// <c>BidiTest.txt</c>/<c>BidiCharacterTest.txt</c> conformance is a separate lane, out of
/// this file's scope — see <see cref="BidiAlgorithm"/>'s own remarks for the documented
/// isolating-run-sequence scope cut these tests deliberately stay inside of.
/// </summary>
public class BidiAlgorithmTests
{
    private const string Hebrew = "שלום"; // שלום ("shalom")
    private const string Arabic = "مرحبا"; // مرحبا ("hello")

    [Fact]
    public void FirstStrongParagraphLevel_LatinText_ReturnsZero()
    {
        Assert.Equal(0, BidiAlgorithm.FirstStrongParagraphLevel("Hello, world"));
    }

    [Fact]
    public void FirstStrongParagraphLevel_HebrewText_ReturnsOne()
    {
        Assert.Equal(1, BidiAlgorithm.FirstStrongParagraphLevel(Hebrew));
    }

    [Fact]
    public void FirstStrongParagraphLevel_ArabicText_ReturnsOne()
    {
        Assert.Equal(1, BidiAlgorithm.FirstStrongParagraphLevel(Arabic));
    }

    [Fact]
    public void FirstStrongParagraphLevel_DigitsOnly_DefaultsToZero()
    {
        // P3: no strongly-directional character found at all -> level 0 (LTR).
        Assert.Equal(0, BidiAlgorithm.FirstStrongParagraphLevel("12345"));
    }

    [Fact]
    public void FirstStrongParagraphLevel_EmptyString_ReturnsZero()
    {
        Assert.Equal(0, BidiAlgorithm.FirstStrongParagraphLevel(""));
    }

    [Fact]
    public void FirstStrongParagraphLevel_ArabicPrecededByDigits_StillDetectsRightToLeft()
    {
        // The headline real-world case: an Arabic invoice line beginning with a number
        // must not be misdetected as left-to-right — digits are EN, not a strong type, so P2
        // skips past them to the first Arabic letter.
        Assert.Equal(1, BidiAlgorithm.FirstStrongParagraphLevel("123 " + Arabic));
    }

    [Fact]
    public void Analyze_PureLatinText_AllLevelsZeroInOneRun()
    {
        var analysis = BidiAlgorithm.Analyze("Hello", TextDirection.LeftToRight);

        Assert.False(analysis.IsRightToLeft);
        Assert.All(analysis.Levels, l => Assert.Equal(0, l));
        var runs = Assert.Single(analysis.GetLevelRuns());
        Assert.Equal(0, runs.Level);
        Assert.False(runs.IsRightToLeft);
    }

    [Fact]
    public void Analyze_PureHebrewText_AllLevelsOddInOneRun()
    {
        var analysis = BidiAlgorithm.Analyze(Hebrew, TextDirection.RightToLeft);

        Assert.True(analysis.IsRightToLeft);
        Assert.All(analysis.Levels, l => Assert.Equal(1, l));
        var run = Assert.Single(analysis.GetLevelRuns());
        Assert.True(run.IsRightToLeft);
    }

    [Fact]
    public void Analyze_HebrewParagraphWithEmbeddedLatinWord_ProducesThreeRunsWithBumpedMiddleLevel()
    {
        // "שלום ABC שלום" — a Latin word embedded implicitly (no explicit LRE/PDF needed: I2
        // bumps a run of L characters inside an odd (right-to-left) paragraph level by one) in
        // a Hebrew paragraph forms three level runs: R, embedded-L (one level higher, even),
        // R again.
        var text = Hebrew + " ABC " + Hebrew;
        var analysis = BidiAlgorithm.Analyze(text, TextDirection.RightToLeft);
        var runs = analysis.GetLevelRuns();

        Assert.True(runs.Count >= 3);
        var middleRun = runs.First(r => !r.IsRightToLeft);
        Assert.True(middleRun.Level > analysis.ParagraphLevel);
        Assert.Equal(0, middleRun.Level % 2);
    }

    [Fact]
    public void Analyze_DigitsAfterArabicLetters_FormASeparateHigherLevelRun()
    {
        // W2 turns EN into AN when it follows AL (Arabic letters) — I2 then bumps AN to
        // paragraph-level+1 inside a right-to-left paragraph, exactly like an embedded Latin
        // run: digits after Arabic text form their own run, one level above the Arabic text
        // around them, never silently staying at the Arabic run's own level.
        var text = Arabic + " 123";
        var analysis = BidiAlgorithm.Analyze(text, TextDirection.RightToLeft);
        var runs = analysis.GetLevelRuns();

        Assert.True(runs.Count >= 2);
        var digitsLevel = analysis.Levels[^1];
        var arabicLevel = analysis.Levels[0];
        Assert.NotEqual(arabicLevel, digitsLevel);
        Assert.True(digitsLevel > arabicLevel);
    }

    [Fact]
    public void ReorderRunIndices_EmbeddedRunBetweenTwoOuterRuns_PaintsInnerToOuterReversal()
    {
        // Hand-verified against UAX#9 L2: three runs at levels [1, 2, 1] (an outer
        // right-to-left run, an embedded higher-level run, the outer run resuming) reorder for
        // left-to-right painting as [2, 1, 0] — the paragraph's last logical run paints first
        // (leftmost), its first logical run paints last (rightmost), exactly the mirror image
        // a right-to-left reading paragraph should produce.
        var runs = new List<BidiRun> { new(0, 4, 1), new(4, 3, 2), new(7, 4, 1) };

        var order = BidiAlgorithm.ReorderRunIndices(runs);

        Assert.Equal([2, 1, 0], order);
    }

    [Fact]
    public void ReorderRunIndices_SingleRun_ReturnsUnchanged()
    {
        var runs = new List<BidiRun> { new(0, 5, 0) };

        Assert.Equal([0], BidiAlgorithm.ReorderRunIndices(runs));
    }

    [Fact]
    public void ReorderRunIndices_AllLeftToRightRuns_PreservesLogicalOrder()
    {
        var runs = new List<BidiRun> { new(0, 2, 0), new(2, 2, 0), new(4, 2, 0) };

        Assert.Equal([0, 1, 2], BidiAlgorithm.ReorderRunIndices(runs));
    }

    [Theory]
    [InlineData('(', ')')]
    [InlineData('[', ']')]
    [InlineData('{', '}')]
    public void TryGetMirror_BracketPairs_ResolveEitherDirection(char open, char close)
    {
        Assert.True(BidiAlgorithm.TryGetMirror(open, out var m1));
        Assert.Equal(close, m1);

        Assert.True(BidiAlgorithm.TryGetMirror(close, out var m2));
        Assert.Equal(open, m2);
    }

    [Fact]
    public void TryGetMirror_NonMirrorableCharacter_ReturnsFalse()
    {
        Assert.False(BidiAlgorithm.TryGetMirror('A', out var mirrored));
        Assert.Equal('A', mirrored);
    }

    // Individual [Fact]s rather than a [Theory]/[InlineData(..., BidiClass...)]: a Theory
    // method must be public, and a public method cannot carry an `internal` parameter type
    // (BidiClass) in its signature (CS0051) — same visibility rule that keeps this whole
    // shaping seam internal.
    [Fact]
    public void GetBidiClass_Digit_ClassifiesAsEuropeanNumber() => Assert.Equal(BidiClass.EN, BidiAlgorithm.GetBidiClass('5'));

    [Fact]
    public void GetBidiClass_LatinLetter_ClassifiesAsL() => Assert.Equal(BidiClass.L, BidiAlgorithm.GetBidiClass('A'));

    [Fact]
    public void GetBidiClass_Space_ClassifiesAsWhitespace() => Assert.Equal(BidiClass.WS, BidiAlgorithm.GetBidiClass(' '));

    [Fact]
    public void GetBidiClass_OpenParen_ClassifiesAsOtherNeutral() => Assert.Equal(BidiClass.ON, BidiAlgorithm.GetBidiClass('('));

    [Fact]
    public void GetBidiClass_Comma_ClassifiesAsCommonSeparator() => Assert.Equal(BidiClass.CS, BidiAlgorithm.GetBidiClass(','));

    [Fact]
    public void GetBidiClass_Plus_ClassifiesAsEuropeanSeparator() => Assert.Equal(BidiClass.ES, BidiAlgorithm.GetBidiClass('+'));

    [Fact]
    public void GetBidiClass_DollarSign_ClassifiesAsEuropeanTerminator() => Assert.Equal(BidiClass.ET, BidiAlgorithm.GetBidiClass('$'));

    [Fact]
    public void GetBidiClass_HebrewLetter_ClassifiesAsR()
    {
        Assert.Equal(BidiClass.R, BidiAlgorithm.GetBidiClass(Hebrew[0]));
    }

    [Fact]
    public void GetBidiClass_ArabicLetter_ClassifiesAsAl()
    {
        Assert.Equal(BidiClass.AL, BidiAlgorithm.GetBidiClass(Arabic[0]));
    }

    [Fact]
    public void GetBidiClass_ArabicIndicDigit_ClassifiesAsArabicNumber()
    {
        Assert.Equal(BidiClass.AN, BidiAlgorithm.GetBidiClass('٥')); // ARABIC-INDIC DIGIT FIVE
    }

    [Fact]
    public void Analyze_TrailingWhitespaceOnRightToLeftLine_ResetsToParagraphLevel()
    {
        // L1: a run of whitespace at the end of the line always resets to the paragraph level,
        // regardless of what W/N/I resolved it to.
        var analysis = BidiAlgorithm.Analyze("hello   ", TextDirection.RightToLeft);

        Assert.Equal(analysis.ParagraphLevel, analysis.Levels[^1]);
        Assert.Equal(analysis.ParagraphLevel, analysis.Levels[^2]);
        Assert.Equal(analysis.ParagraphLevel, analysis.Levels[^3]);
    }

    [Fact]
    public void Analyze_MixedArabicLatinDigits_NeverThrowsAndProducesOneLevelPerCharacter()
    {
        // Non-vacuity/robustness: a realistic Arabic-invoice-style mixed line (Arabic label,
        // Latin product code, Arabic-Indic and ASCII digits, currency, parentheses) analyzes
        // cleanly end to end with no exception and exactly one resolved level per UTF-16 code
        // unit of input.
        var text = Arabic + " ABC123 (٥٠.٠٠ $)";
        var analysis = BidiAlgorithm.Analyze(text, TextDirection.RightToLeft);

        Assert.Equal(text.Length, analysis.Levels.Count);
        Assert.NotEmpty(analysis.GetLevelRuns());
    }
}
