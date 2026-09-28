namespace PlumePdf.Documents;

/// <summary>
/// Options controlling <c>PdfPage.ExtractText</c>. Analogous to <c>PdfOptions</c> but scoped
/// to one extraction call rather than a whole document (mirrors <c>PdfTextExtractionOptions</c>
/// living beside <see cref="ExtractedText"/> rather than beside <c>PdfOptions</c> —
/// extraction is result-scoped, not document-scoped. The resource-limit overrides here take
/// priority when set; a <see langword="null"/> override falls back to the document's own
/// <c>PdfOptions.MaxLettersPerPage</c>/<c>MaxXObjectNestingDepth</c> (and, for the content-stream
/// operator ceiling, <c>PdfOptions.MaxContentStreamOperators</c> directly — there is no
/// per-call override for that one, since it bounds cumulative work across the whole page, not
/// a single extraction concern).
/// </summary>
/// <example>
/// <code>
/// var options = new PdfTextExtractionOptions { MaxLettersPerPage = 50_000 };
/// ExtractedText text = page.ExtractText(options);
/// </code>
/// </example>
public sealed record PdfTextExtractionOptions
{
    /// <summary>The default extraction options.</summary>
    public static PdfTextExtractionOptions Default { get; } = new();

    /// <summary>Overrides the maximum number of letters a single page may produce before extraction refuses to continue (a resource-limit guard against a hostile or pathological content stream). Defaults to <see langword="null"/> (use the built-in conservative limit).</summary>
    public int? MaxLettersPerPage { get; init; }

    /// <summary>Overrides the maximum Form XObject (<c>Do</c>) recursion depth before extraction refuses to continue. Defaults to <see langword="null"/> (use the built-in conservative limit).</summary>
    public int? MaxXObjectNestingDepth { get; init; }
}

/// <summary>
/// The result of <c>PdfPage.ExtractText</c>: one page's text at every rung of the extraction
/// escape hatch — the reading-order
/// <see cref="Text"/> and <see cref="Lines"/>/<see cref="Words"/> PlumePDF assembled, and the
/// raw, unassembled <see cref="Letters"/> beneath them for a caller who disagrees with the
/// reading-order heuristic. Carries its own <see cref="Diagnostics"/>:
/// unlike <c>PdfDocument.Diagnostics</c> (open/parse-time, document-scoped), extraction
/// diagnostics are scoped to this one result so repeated extraction of the same document
/// doesn't grow an unbounded document-level collection.
/// </summary>
/// <example>
/// <code>
/// ExtractedText text = page.ExtractText();
/// Console.WriteLine(text.Text);
/// foreach (Letter letter in text.Letters)
/// {
///     Console.WriteLine($"'{letter.Value}' at ({letter.X:F1},{letter.Y:F1})");
/// }
/// </code>
/// </example>
public sealed class ExtractedText
{
    internal ExtractedText(IndirectReference pageReference, string text, IReadOnlyList<ExtractedLine> lines, IReadOnlyList<ExtractedWord> words, IReadOnlyList<Letter> letters, DiagnosticCollection diagnostics)
    {
        PageReference = pageReference;
        Text = text;
        Lines = lines;
        Words = words;
        Letters = letters;
        Diagnostics = diagnostics;
    }

    /// <summary>The page this result was extracted from.</summary>
    public IndirectReference PageReference { get; }

    /// <summary>The page's text in reading order: <see cref="Lines"/>' text joined with newlines.</summary>
    public string Text { get; }

    /// <summary>The page's lines, in reading order — structure-tree order for a tagged document, else the geometric heuristic (content-order baseline with geometric line/2-column ordering).</summary>
    public IReadOnlyList<ExtractedLine> Lines { get; }

    /// <summary>The page's words, in reading order — the same words as <see cref="Lines"/>, flattened.</summary>
    public IReadOnlyList<ExtractedWord> Words { get; }

    /// <summary>Every positioned glyph on the page, in content-stream order — the escape hatch beneath <see cref="Words"/>/<see cref="Lines"/>.</summary>
    public IReadOnlyList<Letter> Letters { get; }

    /// <summary>Deviations tolerated while extracting this page — never thrown under default options, never silently dropped.</summary>
    public DiagnosticCollection Diagnostics { get; }

    /// <inheritdoc/>
    public override string ToString() => Text;
}
