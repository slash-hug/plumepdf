using PlumePdf.Objects;

namespace PlumePdf.Content;

/// <summary>
/// One operator plus its operands from a page content stream (ISO 32000-1 §8.2, §9.2):
/// e.g. <c>1 0 0 1 72 720 cm</c> parses to <see cref="Operator"/> <c>"cm"</c> with five
/// numeric <see cref="Operands"/>. Produced by <see cref="ContentStreamReader"/> and
/// consumed by the graphics/text-state machinery (<c>PlumePdf.Documents.TextExtractor</c>,
/// <c>PlumePdf.Documents.ImageExtractor</c>) — the read-side mirror of
/// <see cref="ContentStreamBuilder"/>'s write-side operator emission.
/// </summary>
/// <param name="Operator">The operator keyword, e.g. <c>"Tj"</c>, <c>"cm"</c>, <c>"BT"</c>. For an inline image, this is <c>"BI"</c> and <see cref="Operands"/> holds a single <see cref="PdfDictionary"/> (the inline image's parameter dictionary) — the binary data itself is skipped, never materialized.</param>
/// <param name="Operands">The operator's operands, in document order, as already-parsed <see cref="PdfObject"/> values.</param>
/// <param name="Offset">The byte offset in the decoded content stream where this operator's keyword began — for diagnostic context.</param>
internal readonly record struct ContentOperation(string Operator, IReadOnlyList<PdfObject> Operands, long Offset)
{
    /// <summary>Whether this operation represents a skipped inline image (<c>BI…ID…EI</c>).</summary>
    public bool IsInlineImage => Operator == "BI";

    /// <summary>
    /// For a <c>BI</c> operation only: the exact <c>[Start, End)</c> byte span of the whole
    /// <c>BI…ID…EI</c> run within the decoded content bytes <see cref="ContentStreamReader.Read"/>
    /// scanned — including any whitespace immediately before the <c>BI</c> keyword and the
    /// closing <c>EI</c> itself — recorded by the reader at the moment it performs the ID/EI
    /// payload skip (it is the only component that ever knows both ends). <see langword="null"/>
    /// for every non-<c>BI</c> operation, and for a malformed inline image whose terminator
    /// could not be located (the <c>PLUME7012</c> deviations): a consumer that re-emits inline
    /// images verbatim (<c>ContentStreamEditor</c>) must treat <see langword="null"/> as
    /// "no safe span exists" and drop the image rather than splice raw bytes by guesswork.
    /// </summary>
    public (int Start, int End)? InlineImageSpan { get; init; }

    /// <summary>
    /// For a <c>BI</c> operation only: the <c>[Start, End)</c> byte span of just the binary
    /// payload — the first byte after the single whitespace that follows <c>ID</c>
    /// (ISO 32000-1 §8.9.7), through the byte before the whitespace-delimited closing
    /// <c>EI</c> — within the same decoded content bytes <see cref="InlineImageSpan"/> indexes
    /// (the render path slices this to synthesize a
    /// <see cref="PdfStream"/> for the image resolver, where <see cref="InlineImageSpan"/>'s
    /// whole-run span exists for verbatim re-emission). <see langword="null"/> exactly when
    /// <see cref="InlineImageSpan"/> is <see langword="null"/> — same malformed-image rule.
    /// </summary>
    public (int Start, int End)? InlineImageDataSpan { get; init; }
}
