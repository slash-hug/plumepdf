using PlumePdf.Objects;

namespace PlumePdf.Documents.Signing;

/// <summary>
/// Removes a document's existing signatures and document timestamps before a full rewrite,
/// so the saved output is honestly unsigned instead of carrying signatures that no longer
/// verify. Shared by the verbs that rewrite a signed source only when the caller allows it
/// (<c>Redact</c> and <c>Compress</c>).
/// </summary>
internal static class SignatureStripper
{
    // Strips every discovered signature/timestamp's machinery for real
    // ("strips signature fields", not merely their /V): the signature fields are removed from
    // the AcroForm /Fields tree, their widget annotations from every page's /Annots, and —
    // when nothing else uses the form — the catalog's /AcroForm itself; plus the catalog's
    // /Perms (DocMDP/usage-rights signatures) and /DSS (LTV validation material for every
    // signature, whether or not it had a field). Every caller saves through a full rewrite, so the
    // detached field/widget/signature objects are then garbage-collected by Save rather than
    // surviving unreferenced. A signature discovered somewhere no field-tree/annotation surgery
    // reaches (a bare /Perms entry) falls back to /V removal so it can never verify.
    /// <summary>Strips <paramref name="signatures"/> from <paramref name="document"/> and returns how many were stripped.</summary>
    public static int Strip(PdfDocument document, IReadOnlyList<SignatureDictionaryInfo> signatures)
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
