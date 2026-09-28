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

            signaturesStripped = StripSignatures(document, existingSignatures);
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

    // Strips every discovered signature/timestamp's machinery for real
    // ("strips signature fields", not merely their /V): the signature fields are removed from
    // the AcroForm /Fields tree, their widget annotations from every page's /Annots, and —
    // when nothing else uses the form — the catalog's /AcroForm itself; plus the catalog's
    // /Perms (DocMDP/usage-rights signatures) and /DSS (LTV validation material for every
    // signature, whether or not it had a field). Redaction is full-rewrite-only, so the
    // detached field/widget/signature objects are then garbage-collected by Save rather than
    // surviving unreferenced. A signature discovered somewhere no field-tree/annotation surgery
    // reaches (a bare /Perms entry) falls back to /V removal so it can never verify.
    private static int StripSignatures(PdfDocument document, IReadOnlyList<SignatureDictionaryInfo> signatures)
    {
        var form = AcroFormReader.Read(document, document.Diagnostics);

        // Every indirect object that IS a signature field or one of its widgets — removal is
        // by reference identity wherever a /Fields, /Kids, or /Annots array carries one.
        var removal = new HashSet<IndirectReference>();
        foreach (var signature in signatures)
        {
            removal.Add(signature.FieldReference);
            foreach (var field in form.Fields)
            {
                if (field.Reference == signature.FieldReference)
                {
                    foreach (var (widgetReference, _) in field.Widgets)
                    {
                        removal.Add(widgetReference);
                    }
                }
            }
        }

        RemoveFromFieldTree(document, form, removal);
        RemoveFromPageAnnotations(document, removal);

        var stripped = 0;
        foreach (var signature in signatures)
        {
            // /V removal is the belt-and-braces fallback for a signature whose owning object
            // no array surgery detached (e.g. a /Perms-only signature with no field at all) —
            // and harmless double work for the ones the tree surgery already detached.
            if (document.Objects[signature.FieldReference] is PdfDictionary field && field.Remove(PdfName.V))
            {
                document.Objects.MarkDirty(signature.FieldReference);
            }

            stripped++;
        }

        if (document.Catalog is { } catalog)
        {
            var touchedCatalog = false;
            if (catalog.Dictionary.Remove(PdfName.Perms))
            {
                touchedCatalog = true;
            }

            if (catalog.Dictionary.Remove(PdfName.DSS))
            {
                touchedCatalog = true;
            }

            if (touchedCatalog)
            {
                document.Objects.MarkDirty(catalog.Reference);
            }
        }

        return stripped;
    }

    // Removes every reference in `removal` from the /AcroForm /Fields array and, recursively,
    // from any surviving field's /Kids array (a signature field may be nested under a parent);
    // when /Fields ends up empty the catalog's /AcroForm entry is dropped entirely, matching
    // FormFlattener's "no interactive form left at all" end state.
    private static void RemoveFromFieldTree(PdfDocument document, AcroFormReadResult form, HashSet<IndirectReference> removal)
    {
        if (form.AcroFormDictionary is not { } acroForm
            || !acroForm.TryGetValue(PdfName.Fields, out var fieldsValue)
            || Resolve(document, fieldsValue) is not PdfArray fields)
        {
            return;
        }

        var visited = new HashSet<IndirectReference>();
        PruneArray(document, fields, removal, visited, depth: 0);

        if (fieldsValue is PdfReference fieldsReference)
        {
            document.Objects.MarkDirty(fieldsReference.Target);
        }

        if (form.AcroFormReference is { } acroFormReference)
        {
            document.Objects.MarkDirty(acroFormReference);
        }

        if (fields.Count == 0 && document.Catalog is { } catalog && catalog.Dictionary.Remove(PdfName.AcroForm))
        {
            document.Objects.MarkDirty(catalog.Reference);
        }
    }

    private static void PruneArray(PdfDocument document, PdfArray array, HashSet<IndirectReference> removal, HashSet<IndirectReference> visited, int depth)
    {
        if (depth > document.Options.MaxFieldTreeDepth)
        {
            return; // FieldTree's own reader cap already diagnosed/refused deeper trees on read.
        }

        for (var i = array.Count - 1; i >= 0; i--)
        {
            if (array[i] is not PdfReference reference)
            {
                continue;
            }

            if (removal.Contains(reference.Target))
            {
                array.RemoveAt(i);
                continue;
            }

            if (!visited.Add(reference.Target) || document.Objects[reference.Target] is not PdfDictionary node)
            {
                continue;
            }

            if (node.TryGetValue(PdfName.Kids, out var kidsValue) && Resolve(document, kidsValue) is PdfArray kids)
            {
                var before = kids.Count;
                PruneArray(document, kids, removal, visited, depth + 1);
                if (kids.Count != before)
                {
                    document.Objects.MarkDirty(reference.Target);
                    if (kidsValue is PdfReference kidsReference)
                    {
                        document.Objects.MarkDirty(kidsReference.Target);
                    }
                }
            }
        }
    }

    private static void RemoveFromPageAnnotations(PdfDocument document, HashSet<IndirectReference> removal)
    {
        var annotsName = PdfName.Get("Annots");
        foreach (var page in document.Pages)
        {
            if (!page.Dictionary.TryGetValue(annotsName, out var annotsValue) || Resolve(document, annotsValue) is not PdfArray annots)
            {
                continue;
            }

            var before = annots.Count;
            for (var i = annots.Count - 1; i >= 0; i--)
            {
                if (annots[i] is PdfReference reference && removal.Contains(reference.Target))
                {
                    annots.RemoveAt(i);
                }
            }

            if (annots.Count != before)
            {
                document.Objects.MarkDirty(page.Reference);
                if (annotsValue is PdfReference annotsReference)
                {
                    document.Objects.MarkDirty(annotsReference.Target);
                }
            }
        }
    }

    private static PdfObject? Resolve(PdfDocument document, PdfObject value) =>
        value is PdfReference reference ? document.Objects[reference.Target] : value;
}
