namespace PlumePdf.Documents;

/// <summary>
/// A line of text assembled from <see cref="ExtractedWord"/>s that share a baseline, ordered
/// left-to-right by <c>ReadingOrderer</c> (ISO 32000-1 defines no logical line concept; this is
/// PlumePDF's own geometric grouping).
/// </summary>
/// <example>
/// <code>
/// foreach (ExtractedLine line in text.Lines)
/// {
///     Console.WriteLine(line.Text);
/// }
/// </code>
/// </example>
public sealed class ExtractedLine
{
    internal ExtractedLine(string text, PdfRectangle boundingBox, IReadOnlyList<ExtractedWord> words)
    {
        Text = text;
        BoundingBox = boundingBox;
        Words = words;
    }

    /// <summary>The line's text: <see cref="Words"/>' text joined with single spaces, in reading order.</summary>
    public string Text { get; }

    /// <summary>The smallest rectangle containing every word in <see cref="Words"/>, in page space.</summary>
    public PdfRectangle BoundingBox { get; }

    /// <summary>The words on this line, left-to-right.</summary>
    public IReadOnlyList<ExtractedWord> Words { get; }

    /// <inheritdoc/>
    public override string ToString() => Text;
}
