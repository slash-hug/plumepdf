using PlumePdf.Documents;
using Xunit;

namespace PlumePdf.Tests.Documents;

/// <summary><see cref="WordAssembler"/>'s nearest-neighbour-style gap/overlap clustering.</summary>
public class WordAssemblyTests
{
    private const double FontSize = 12;
    private const double GlyphWidth = 6;

    [Fact]
    public void Assemble_TightlyKernedLetters_JoinOneWord()
    {
        var letters = new List<Letter>
        {
            MakeLetter('H', 0),
            MakeLetter('i', GlyphWidth),
        };

        var words = WordAssembler.Assemble(letters);

        Assert.Single(words);
        Assert.Equal("Hi", words[0].Text);
    }

    [Fact]
    public void Assemble_HyphenAndSpaceLessGap_SplitsIntoTwoWords()
    {
        // No space glyph between "Hello" and "World" - just a horizontal gap far wider than
        // ordinary inter-letter kerning, as produced by a TJ positioning adjustment instead of
        // an explicit space character.
        var letters = new List<Letter>
        {
            MakeLetter('H', 0),
            MakeLetter('e', GlyphWidth),
            MakeLetter('l', GlyphWidth * 2),
            MakeLetter('l', GlyphWidth * 3),
            MakeLetter('o', GlyphWidth * 4),
            MakeLetter('W', (GlyphWidth * 5) + (FontSize * 2)), // a gap several em-widths beyond the last letter's advance
            MakeLetter('o', (GlyphWidth * 5) + (FontSize * 2) + GlyphWidth),
            MakeLetter('r', (GlyphWidth * 5) + (FontSize * 2) + (GlyphWidth * 2)),
        };

        var words = WordAssembler.Assemble(letters);

        Assert.Equal(2, words.Count);
        Assert.Equal("Hello", words[0].Text);
        Assert.Equal("Wor", words[1].Text);
    }

    [Fact]
    public void Assemble_DecodedSpaceCharacter_EndsWordWithoutIncludingTheSpace()
    {
        var letters = new List<Letter>
        {
            MakeLetter('A', 0),
            MakeLetter(' ', GlyphWidth),
            MakeLetter('B', GlyphWidth * 2),
        };

        var words = WordAssembler.Assemble(letters);

        Assert.Equal(["A", "B"], words.Select(w => w.Text));
    }

    [Fact]
    public void Assemble_RotatedVerticalRun_ClustersAlongTheWritingDirection()
    {
        // Text rotated 90 degrees: each letter's origin advances in +Y with direction (0,1)
        // instead of the ordinary horizontal (1,0) - WordAssembler must project the gap along
        // that direction, not assume a horizontal page.
        var letters = new List<Letter>
        {
            MakeRotatedLetter('U', 0),
            MakeRotatedLetter('p', GlyphWidth),
            MakeRotatedLetter('!', GlyphWidth * 2),
        };

        var words = WordAssembler.Assemble(letters);

        Assert.Single(words);
        Assert.Equal("Up!", words[0].Text);
    }

    [Fact]
    public void Assemble_DifferentBaseline_StartsNewWord()
    {
        var letters = new List<Letter>
        {
            MakeLetter('A', 0, y: 700),
            MakeLetter('B', 0, y: 650), // a different line entirely
        };

        var words = WordAssembler.Assemble(letters);

        Assert.Equal(2, words.Count);
    }

    private static Letter MakeLetter(char value, double x, double y = 700) =>
        new(value.ToString(), x, y, new PdfRectangle(x, y, x + GlyphWidth, y + FontSize), "F1", FontSize, 0, GlyphWidth);

    private static Letter MakeRotatedLetter(char value, double alongAxis) =>
        new(value.ToString(), 100, 700 + alongAxis, new PdfRectangle(100, 700 + alongAxis, 100 + FontSize, 700 + alongAxis + GlyphWidth), "F1", FontSize, 0, GlyphWidth, directionX: 0, directionY: 1);
}
