using PlumePdf.Layout;
using Xunit;

namespace PlumePdf.Tests;

/// <summary>
/// <see cref="TextLayouter"/>: greedy word-wrapping and the bootstrap glyph
/// width heuristic that measures it.
/// </summary>
public class TextLayouterTests
{
    [Fact]
    public void MeasureText_LongerString_MeasuresWiderThanShorterAtSameSize()
    {
        var shortWidth = TextLayouter.MeasureText("hi", fontSize: 12, bold: false);
        var longWidth = TextLayouter.MeasureText("hello world", fontSize: 12, bold: false);

        Assert.True(longWidth > shortWidth);
    }

    [Fact]
    public void MeasureText_Bold_MeasuresAtLeastAsWideAsRegular()
    {
        var regular = TextLayouter.MeasureText("Invoice Total", fontSize: 12, bold: false);
        var bold = TextLayouter.MeasureText("Invoice Total", fontSize: 12, bold: true);

        Assert.True(bold >= regular);
    }

    [Fact]
    public void WrapLines_NarrowWidth_BreaksAtWordBoundaries()
    {
        var lines = TextLayouter.WrapLines("The quick brown fox jumps over the lazy dog", fontSize: 12, bold: false, availableWidth: 80);

        Assert.True(lines.Count > 1);
        foreach (var line in lines)
        {
            Assert.True(TextLayouter.MeasureText(line, 12, false) <= 80 + 0.5);
        }

        Assert.Equal("The quick brown fox jumps over the lazy dog", string.Join(' ', lines));
    }

    [Fact]
    public void WrapLines_WordWiderThanAvailableWidth_HardBreaksWithoutOverflowingAnyLine()
    {
        var longToken = new string('m', 50);
        var lines = TextLayouter.WrapLines(longToken, fontSize: 12, bold: false, availableWidth: 40);

        Assert.True(lines.Count > 1);
        foreach (var line in lines)
        {
            Assert.True(TextLayouter.MeasureText(line, 12, false) <= 40 + 0.5);
        }
    }

    [Fact]
    public void WrapLines_EmptyString_ReturnsOneEmptyLine()
    {
        var lines = TextLayouter.WrapLines(string.Empty, fontSize: 12, bold: false, availableWidth: 100);

        Assert.Single(lines);
        Assert.Equal(string.Empty, lines[0]);
    }

    [Fact]
    public void WrapLines_NonPositiveAvailableWidth_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TextLayouter.WrapLines("text", 12, false, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextLayouter.WrapLines("text", 12, false, -10));
    }
}
