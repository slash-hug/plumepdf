using PlumePdf.Content;
using PlumePdf.Documents.Signing;
using PlumePdf.Objects;

namespace PlumePdf.Documents.Redaction;

/// <summary>
/// Orchestrates one <c>Redact</c> call end to end: guards (encrypted source,
/// signed source), target resolution (<see cref="RedactionTargetResolver"/>), per-page
/// content-stream editing (<see cref="Content.ContentStreamEditor"/>), non-content-stream
/// metadata scrubbing (<see cref="MetadataScrubber"/>), and assembling the
/// <see cref="RedactionResult"/> every caller inspects before trusting the output. The public
/// entry points are <see cref="PdfDocument.Redact"/> (the rich door) and <c>Pdf.Redact</c>
/// (the one-line path verb).
/// </summary>
/// <remarks>
/// <para>
/// The <c>SaveIncremental</c> guard is wired through here: once a redaction
/// actually removed or scrubbed anything, this engine calls
/// <see cref="PdfDocument.MarkRedactionDirty"/> so a subsequent <c>SaveIncremental</c>
/// refuses outright (<c>PLUME5016</c>) — only <c>Save</c>'s full rewrite actually removes
/// bytes; an incremental update by construction preserves every prior revision, which would
/// leave the "redacted" content fully recoverable from the file's earlier bytes. A zero-match
/// call that touched nothing does not mark the document, per
/// <see cref="PdfDocument.MarkRedactionDirty"/>'s own contract.
/// </para>
/// </remarks>
internal static class RedactionEngine
{
    /// <summary>Redacts <paramref name="document"/> against <paramref name="targets"/> — see this type's remarks for the one known integration gap.</summary>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME6061</c> — <paramref name="document"/> was opened from an encrypted source
    /// (redaction requires a full rewrite, and encrypted-source <c>Save</c> is out of scope
    /// until encryption write ships). <c>PLUME6062</c> — the source carries
    /// one or more existing signatures/document timestamps and <see cref="PdfRedactOptions.AllowInvalidatingSignatures"/>
    /// was not set. <c>PLUME6060</c> — target resolution exceeded
    /// <see cref="PdfRedactOptions.MaxMatches"/>. <c>PLUME6074</c> — a region intersects an
    /// image and <see cref="PdfRedactOptions.RefuseOnImageRemoval"/> requested refusal instead
    /// of whole-image removal. <c>PLUME7018</c> — a page and its nested Form
    /// XObjects exceeded <c>PdfOptions.MaxContentStreamOperators</c> cumulatively.
    /// </exception>
    public static RedactionResult Redact(PdfDocument document, IReadOnlyList<RedactionTarget> targets, PdfRedactOptions? options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(targets);

        var effective = options ?? PdfRedactOptions.Default;

        if (document.HasEncryptedSource)
        {
            throw new PlumePdfException("PLUME6061", "Redact refuses an encrypted source: true redaction requires a full rewrite (Save), and PlumePDF does not support writing an encrypted document in this release — a documented v1.x gap, not a silent no-op. Decrypt the source through an external tool first if redaction is required.");
        }

        var existingSignatures = SignatureDictionaryReader.ReadAll(document);
        var signaturesStripped = 0;
        if (existingSignatures.Count > 0)
        {
            if (!effective.AllowInvalidatingSignatures)
            {
                var names = string.Join(", ", existingSignatures.Select(static s => s.FieldName));
                throw new PlumePdfException("PLUME6062", $"Redact would invalidate {existingSignatures.Count} existing signature(s)/timestamp(s) ({names}) — every redaction routes through a full rewrite, which moves the bytes their /ByteRange names. Pass PdfRedactOptions.AllowInvalidatingSignatures to strip them and proceed with an honestly-unsigned result; a redacted-yet-apparently-signed document is a false-trust failure mode PlumePDF refuses to produce by default.");
            }

            signaturesStripped = SignatureStripper.Strip(document, existingSignatures);
        }

        var (regions, matchCount) = RedactionTargetResolver.Resolve(targets, document, effective);

        var textOperatorsRemoved = 0;
        var imagesRemoved = 0;
        var inlineImagesRemoved = 0;
        var inlineImagesSkipped = 0;
        var annotationAppearancesWiped = 0;

        foreach (var group in regions.GroupBy(static r => r.PageIndex))
        {
            var page = document.Pages[group.Key];
            var mediaBox = PageSpace.GetMediaBox(page.Dictionary);
            var rotate = PageSpace.GetRotation(page.Dictionary);
            var pageRects = group.Select(static g => g.Rect).ToList();
            var editorRegions = pageRects.Select(static r => new EditorRect(r.Left, r.Bottom, r.Right, r.Top)).ToList();

            var outcome = ContentStreamEditor.RedactPage(page.Dictionary, page.Reference, document.Objects, document.Options.Filters, document.Options, mediaBox, rotate, editorRegions, effective.RefuseOnImageRemoval);
            textOperatorsRemoved += outcome.TextOperatorsRemoved;
            imagesRemoved += outcome.ImagesRemoved;
            inlineImagesRemoved += outcome.InlineImagesRemoved;
            inlineImagesSkipped += outcome.InlineImagesSkipped;

            // Annotation-rendered content lives outside the page content stream entirely
            // (appearance streams, §12.5.5) — an annotation whose /Rect intersects a region is
            // wiped whole, the same over-redact bias the editor applies to a matched form.
            annotationAppearancesWiped += AnnotationRedactor.WipeIntersecting(document, page, pageRects);
        }

        var scrubbedSurfaces = MetadataScrubber.Scrub(document, targets);

        // See this type's remarks: anything actually removed or scrubbed makes
        // SaveIncremental a coded refusal from here on — the original source bytes still carry
        // what this call just removed. Page-content matches, stripped signature machinery, and
        // metadata-surface scrubs each independently count as "removed something".
        if (matchCount > 0 || signaturesStripped > 0 || scrubbedSurfaces.Count > 0)
        {
            document.MarkRedactionDirty();
        }

        return new RedactionResult(matchCount, regions.Count, textOperatorsRemoved, imagesRemoved, inlineImagesRemoved, inlineImagesSkipped, annotationAppearancesWiped, signaturesStripped, scrubbedSurfaces);
    }
}
