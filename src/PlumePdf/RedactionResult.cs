namespace PlumePdf;

/// <summary>
/// The rich result of <c>PdfDocument.Redact</c> (the
/// <see cref="SignatureVerificationResult"/> precedent applied to redaction): how many
/// targets matched, what content-level and metadata-level surfaces were touched, and — most
/// importantly for a caller who must not silently ship an unredacted document — whether
/// anything matched at all. A zero-match redaction is never silent: check
/// <see cref="MatchCount"/> (or <see cref="HadNoMatches"/>) before trusting the output, the
/// same discipline the cookbook demonstrates (<c>docs/cookbook/redact.md</c>).
/// </summary>
/// <example>
/// <code>
/// var result = document.Redact([RedactionTarget.Text("Jane Doe")]);
/// if (result.HadNoMatches)
/// {
///     throw new InvalidOperationException("Expected at least one redaction match.");
/// }
///
/// Console.WriteLine($"{result.MatchCount} match(es), {result.ImagesRemoved} image(s) removed");
/// document.Save("redacted.pdf");
/// </code>
/// </example>
public sealed class RedactionResult
{
    internal RedactionResult(
        int matchCount,
        int regionsRedacted,
        int textOperatorsRemoved,
        int imagesRemoved,
        int inlineImagesRemoved,
        int inlineImagesSkipped,
        int annotationAppearancesWiped,
        int signaturesStripped,
        IReadOnlyDictionary<string, int> scrubbedSurfaces)
    {
        MatchCount = matchCount;
        RegionsRedacted = regionsRedacted;
        TextOperatorsRemoved = textOperatorsRemoved;
        ImagesRemoved = imagesRemoved;
        InlineImagesRemoved = inlineImagesRemoved;
        InlineImagesSkipped = inlineImagesSkipped;
        AnnotationAppearancesWiped = annotationAppearancesWiped;
        SignaturesStripped = signaturesStripped;
        ScrubbedSurfaces = scrubbedSurfaces;
    }

    /// <summary>
    /// The total number of <see cref="PlumePdf.Documents.Redaction.RedactionTarget"/> matches this call resolved: one per
    /// caller-supplied region, plus one per text/pattern occurrence found across every page in
    /// scope. Zero is loud, not silent — see this type's remarks.
    /// </summary>
    public int MatchCount { get; }

    /// <summary>Whether <see cref="MatchCount"/> is zero — a convenience for the "did anything actually get redacted" check the cookbook leads with.</summary>
    public bool HadNoMatches => MatchCount == 0;

    /// <summary>The number of distinct (page, rectangle) regions redaction content-stream editing was applied against — may be fewer than <see cref="MatchCount"/> when adjacent matches on the same page were merged.</summary>
    public int RegionsRedacted { get; }

    /// <summary>The number of content-stream text-showing operators (<c>Tj</c>/<c>TJ</c>/<c>'</c>/<c>"</c>) removed because they intersected a target region.</summary>
    public int TextOperatorsRemoved { get; }

    /// <summary>The number of image XObjects removed in full because they intersected a target region (whole-XObject removal, never a best-effort blank rectangle over surviving pixels). <see cref="PdfRedactOptions.RefuseOnImageRemoval"/> turns any such removal into a coded refusal (<c>PLUME6074</c>) instead.</summary>
    public int ImagesRemoved { get; }

    /// <summary>
    /// The number of inline images (<c>BI</c>…<c>ID</c>…<c>EI</c>) dropped whole from a rebuilt
    /// content stream — because they intersected a target region (never surviving
    /// pixels under a redaction), or because their raw byte span could not be safely determined
    /// (a malformed terminator): emitting nothing is the fail-safe direction; splicing guessed
    /// raw bytes is how removed content resurrects. <see cref="PdfRedactOptions.RefuseOnImageRemoval"/>
    /// turns an intersecting inline image into a coded refusal (<c>PLUME6074</c>) instead.
    /// </summary>
    public int InlineImagesRemoved { get; }

    /// <summary>
    /// The number of inline images whose painted geometry was genuinely indeterminate (a
    /// non-finite transform at the <c>BI</c>) and so could not be intersection-tested — they
    /// survive verbatim, reported here loudly rather than silently. Non-zero means this
    /// redaction call could not prove those images sit outside every target region; inspect the
    /// source if redaction completeness matters.
    /// </summary>
    public int InlineImagesSkipped { get; }

    /// <summary>
    /// The number of annotations whose appearance streams were wiped because their <c>/Rect</c>
    /// intersected a target region: every <c>/AP</c> stream (<c>/N</c>, <c>/R</c>, <c>/D</c>,
    /// including per-state sub-dictionaries) is emptied and the annotation's <c>/Contents</c>
    /// text removed — the over-redact-never-under-redact bias applied to annotation-rendered
    /// content, which lives outside the page content stream entirely. Each wipe also records a
    /// <c>PLUME6075</c> diagnostic naming the annotation subtype and rectangle.
    /// </summary>
    public int AnnotationAppearancesWiped { get; }

    /// <summary>The number of existing signature/document-timestamp dictionaries stripped (only non-zero when <see cref="PdfRedactOptions.AllowInvalidatingSignatures"/> was set): the signature fields are removed from the AcroForm <c>/Fields</c> tree, their widget annotations from page <c>/Annots</c>, and the catalog's <c>/Perms</c>/<c>/DSS</c> dropped, leaving the output honestly unsigned.</summary>
    public int SignaturesStripped { get; }

    /// <summary>
    /// How many text/pattern-target matches were scrubbed from each non-content-stream surface
    /// (the enumerated scrub-surface list), keyed by surface name: <c>"DocInfo"</c>,
    /// <c>"Xmp"</c>, <c>"AnnotationContents"</c>, <c>"OutlineTitles"</c>,
    /// <c>"EmbeddedFileNames"</c> (matched attachments also have their embedded <c>/EF</c>
    /// stream bytes dropped), <c>"PieceInfo"</c> (catalog- and page-level), and
    /// <c>"StructureTree"</c> (<c>/ActualText</c>/<c>/Alt</c>/<c>/T</c> values in the source's
    /// structure tree). A surface absent from this map was never touched (not present in the
    /// source, or no target matched it) — never a silent skip: see <c>MetadataScrubber</c>'s
    /// remarks for the one documented v1.0 carve-out (embedded font-subset glyph residue, never
    /// scrubbed).
    /// </summary>
    public IReadOnlyDictionary<string, int> ScrubbedSurfaces { get; }
}
