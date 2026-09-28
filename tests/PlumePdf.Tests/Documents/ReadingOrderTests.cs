using PlumePdf.Documents;
using Xunit;

namespace PlumePdf.Tests.Documents;

/// <summary><see cref="ReadingOrderer"/>'s line grouping, top-to-bottom ordering, and two-column gutter detection.</summary>
public class ReadingOrderTests
{
    [Fact]
    public void Order_SingleColumnMultipleLines_OrdersTopToBottom()
    {
        var words = new List<ExtractedWord>
        {
            MakeWord("Second", 0, 680),
            MakeWord("First", 0, 700),
            MakeWord("Third", 0, 660),
        };

        var (lines, _, text) = ReadingOrderer.Order(words);

        Assert.Equal(["First", "Second", "Third"], lines.Select(l => l.Text));
        Assert.Equal("First\nSecond\nThird", text);
    }

    [Fact]
    public void Order_WordsOnSameLine_OrderLeftToRight()
    {
        var words = new List<ExtractedWord>
        {
            MakeWord("world", 60, 700),
            MakeWord("Hello", 0, 700),
        };

        var (lines, _, _) = ReadingOrderer.Order(words);

        Assert.Single(lines);
        Assert.Equal("Hello world", lines[0].Text);
    }

    [Fact]
    public void Order_TwoColumnLayout_ReadsLeftColumnTopToBottomThenRightColumn()
    {
        var words = new List<ExtractedWord>();

        // Left column: x in [0,100], four lines.
        for (var i = 0; i < 4; i++)
        {
            words.Add(MakeWord($"L{i}", 10, 700 - (i * 20)));
            words.Add(MakeWord($"left{i}", 60, 700 - (i * 20)));
        }

        // Right column: x in [250,350], four lines - a persistent empty band from x=100 to
        // x=250 through the whole vertical extent, wide enough for FindColumnGutter to detect.
        for (var i = 0; i < 4; i++)
        {
            words.Add(MakeWord($"R{i}", 260, 700 - (i * 20)));
            words.Add(MakeWord($"right{i}", 310, 700 - (i * 20)));
        }

        var (lines, _, _) = ReadingOrderer.Order(words);

        Assert.Equal(8, lines.Count);

        // The first four lines are the left column, top-to-bottom; the last four are the
        // right column, top-to-bottom.
        for (var i = 0; i < 4; i++)
        {
            Assert.StartsWith($"L{i}", lines[i].Text);
        }

        for (var i = 0; i < 4; i++)
        {
            Assert.StartsWith($"R{i}", lines[4 + i].Text);
        }
    }

    [Fact]
    public void Order_EmptyInput_ReturnsEmptyResult()
    {
        var (lines, words, text) = ReadingOrderer.Order([]);

        Assert.Empty(lines);
        Assert.Empty(words);
        Assert.Equal(string.Empty, text);
    }

    private static ExtractedWord MakeWord(string text, double x, double y)
    {
        var letters = new List<Letter>
        {
            new(text, x, y, new PdfRectangle(x, y, x + (text.Length * 6), y + 12), "F1", 12, 0, text.Length * 6),
        };

        return new ExtractedWord(text, letters[0].BoundingBox, letters);
    }
}
