namespace PlumePdf.Documents;

/// <summary>
/// A word assembled from consecutive <see cref="Letter"/>s by <c>WordAssembler</c>'s
/// gap/overlap clustering (ported from PdfPig's <c>NearestNeighbourWordExtractor</c>,
/// Apache-2.0 — see <c>NOTICE</c> and the provenance comment in <c>WordAssembler.cs</c>).
/// </summary>
/// <example>
/// <code>
/// foreach (ExtractedWord word in text.Words)
/// {
///     Console.WriteLine($"{word.Text} @ {word.BoundingBox}");
/// }
/// </code>
/// </example>
public sealed class ExtractedWord
{
    internal ExtractedWord(string text, PdfRectangle boundingBox, IReadOnlyList<Letter> letters, int? mcid = null)
    {
        Text = text;
        BoundingBox = boundingBox;
        Letters = letters;
        Mcid = mcid;
    }

    /// <summary>The word's decoded text, letters concatenated in content order.</summary>
    public string Text { get; }

    /// <summary>The smallest rectangle containing every letter in <see cref="Letters"/>, in page space.</summary>
    public PdfRectangle BoundingBox { get; }

    /// <summary>The letters this word was assembled from, in content order — the escape hatch below word assembly.</summary>
    public IReadOnlyList<Letter> Letters { get; }

    /// <summary>
    /// The word's owning marked-content identifier — its first letter's <see cref="Letter.Mcid"/>
    /// — or <see langword="null"/> when the word was painted outside any MCID-carrying
    /// marked-content sequence. The per-word provenance <c>ReadingOrderer</c>'s
    /// structure-tree-order path ranks words by; in the vanishingly rare case
    /// of a word whose letters straddle two sequences, the first letter's id wins (the word was
    /// clustered geometrically, so its letters were painted together anyway).
    /// </summary>
    public int? Mcid { get; }

    /// <inheritdoc/>
    public override string ToString() => Text;
}
