namespace PlumePdf.Documents;

/// <summary>
/// Walks a document's page tree (ISO 32000-1 §7.7.3) from its <c>/Pages</c> root down to
/// leaf <c>/Page</c> nodes, flattening it into an ordered list of leaves — enough for
/// <c>PageCollection</c> to enumerate, reorder, and remove pages. Each
/// leaf's returned dictionary has the four inheritable attributes (§7.7.3.4 Table 30:
/// <c>/Resources</c>, <c>/MediaBox</c>, <c>/CropBox</c>, <c>/Rotate</c>) resolved onto it
/// from the nearest ancestor <c>/Pages</c> node that sets them, when the leaf doesn't set
/// its own — a legal and common real-world layout (a single <c>/MediaBox</c>/<c>/Resources</c>
/// on the <c>/Pages</c> root, omitted from every page). <c>doc.Objects[pageReference]</c>
/// still returns the page's untouched original dictionary; this resolved copy is what
/// <c>PdfPage.Dictionary</c> (and therefore both save paths and <c>Pdf.Merge</c>/<c>Split</c>,
/// which serialize a flat page list with no ancestor <c>/Pages</c> node of their own to
/// inherit from) actually see. Malformed subtrees are skipped with a diagnostic under
/// lenient reading (never thrown) so a damaged page tree cannot prevent
/// <c>PdfDocument.Open</c> from succeeding — the same recovery-ladder philosophy as the
/// reading engine. A cycle guard and a depth cap protect against pathological/hostile trees.
/// </summary>
internal static class PageTreeReader
{
    private const int MaxDepth = 256;
    private const int MaxReferenceChain = 8;
    private static readonly PdfName PagesTypeValue = PdfName.Get("Pages");
    private static readonly PdfName PageTypeValue = PdfName.Get("Page");
    private static readonly PdfName KidsName = PdfName.Get("Kids");
    private static readonly PdfName ResourcesName = PdfName.Get("Resources");
    private static readonly PdfName MediaBoxName = PdfName.Get("MediaBox");
    private static readonly PdfName CropBoxName = PdfName.Get("CropBox");
    private static readonly PdfName RotateName = PdfName.Get("Rotate");

    /// <summary>The inheritable page attributes accumulated while descending the tree (§7.7.3.4 Table 30) — each overridden by a node that sets its own value.</summary>
    private readonly record struct InheritedAttributes(PdfObject? Resources, PdfObject? MediaBox, PdfObject? CropBox, PdfObject? Rotate)
    {
        public InheritedAttributes OverriddenBy(PdfDictionary node) => new(
            node.TryGetValue(ResourcesName, out var resources) ? resources : Resources,
            node.TryGetValue(MediaBoxName, out var mediaBox) ? mediaBox : MediaBox,
            node.TryGetValue(CropBoxName, out var cropBox) ? cropBox : CropBox,
            node.TryGetValue(RotateName, out var rotate) ? rotate : Rotate);
    }

    /// <summary>
    /// Returns the ordered list of leaf pages reachable from <paramref name="catalog"/>'s
    /// <c>/Pages</c> entry, each paired with its dictionary with inheritable attributes
    /// resolved (see class remarks).
    /// </summary>
    public static List<(IndirectReference Reference, PdfDictionary Dictionary)> CollectPages(ObjectRegistry objects, PdfDictionary catalog, PdfOptions options, DiagnosticCollection? diagnostics) =>
        CollectPages(objects, catalog, options, diagnostics, treeNodes: null);

    /// <summary>
    /// As <see cref="CollectPages(ObjectRegistry, PdfDictionary, PdfOptions, DiagnosticCollection?)"/>,
    /// also adding to <paramref name="treeNodes"/> the object number of every page-tree node the
    /// walk actually resolved (each <c>/Pages</c> node, each page, and any indirect <c>/Kids</c>
    /// array) — never a number that failed to resolve to its expected type, so an unresolvable
    /// (free-listed) number the registry may later reuse for a new object is never in the set.
    /// </summary>
    public static List<(IndirectReference Reference, PdfDictionary Dictionary)> CollectPages(ObjectRegistry objects, PdfDictionary catalog, PdfOptions options, DiagnosticCollection? diagnostics, HashSet<int>? treeNodes)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);

        var result = new List<(IndirectReference, PdfDictionary)>();

        if (!catalog.TryGetValue(PdfName.Get("Pages"), out var pagesValue) || pagesValue is not PdfReference pagesRef)
        {
            ReportDeviation("PLUME6002", "The catalog's /Pages entry is missing or is not an indirect reference; the document has no pages.", options, diagnostics);
            return result;
        }

        var visited = new HashSet<int>();
        Walk(objects, pagesRef.Target, visited, treeNodes, result, options, diagnostics, depth: 0, inherited: default);
        return result;
    }

    private static void Walk(ObjectRegistry objects, IndirectReference reference, HashSet<int> visited, HashSet<int>? treeNodes, List<(IndirectReference, PdfDictionary)> result, PdfOptions options, DiagnosticCollection? diagnostics, int depth, InheritedAttributes inherited)
    {
        if (depth > MaxDepth)
        {
            ReportDeviation("PLUME6011", $"Page tree nesting exceeded {MaxDepth} levels near object {reference.Number}; stopping the walk.", options, diagnostics);
            return;
        }

        if (!visited.Add(reference.Number))
        {
            ReportDeviation("PLUME6011", $"Page tree cycles back to object {reference.Number}; stopping the walk.", options, diagnostics);
            return;
        }

        if (objects[reference] is not PdfDictionary node)
        {
            ReportDeviation("PLUME6010", $"Page tree node {reference.Number} did not resolve to a dictionary; skipping it.", options, diagnostics);
            return;
        }

        treeNodes?.Add(reference.Number);

        var updatedInherited = inherited.OverriddenBy(node);
        var type = node.TryGetValue(PdfName.Type, out var typeValue) ? ResolveIndirect(objects, typeValue) as PdfName : null;

        if (ReferenceEquals(type, PageTypeValue))
        {
            result.Add((reference, ResolveInheritedAttributes(objects, node, inherited)));
            return;
        }

        // /Kids is "an array of indirect references" (ISO 32000-1 §7.7.3.2, Table 29), but the
        // array object itself may be written indirectly (`/Kids 8 0 R` pointing at `8 0 obj [...]`)
        // like any other PDF value (§7.3.10). PDFium's CPDF_Dictionary::GetArrayFor and pypdf both
        // resolve that reference before type-checking; matching only a direct PdfArray here left
        // every such document with zero pages (~7 % of a real-world corpus, two producer
        // families, both classic-xref and xref-stream layouts).
        if (!node.TryGetValue(KidsName, out var kidsValue) || ResolveIndirect(objects, kidsValue) is not PdfArray kids)
        {
            if (ReferenceEquals(type, PagesTypeValue))
            {
                ReportDeviation("PLUME6010", $"Pages node {reference.Number} has no /Kids array; treating it as having no pages.", options, diagnostics);
                return;
            }

            // Some producers omit /Type on leaf pages; a node with no /Kids and no /Type
            // is treated as a leaf page rather than an empty subtree.
            result.Add((reference, ResolveInheritedAttributes(objects, node, inherited)));
            return;
        }

        if (kidsValue is PdfReference kidsReference)
        {
            treeNodes?.Add(kidsReference.Target.Number);
        }

        foreach (var kid in kids)
        {
            if (kid is not PdfReference kidRef)
            {
                ReportDeviation("PLUME6010", $"Pages node {reference.Number} has a /Kids entry that is not an indirect reference; skipping it.", options, diagnostics);
                continue;
            }

            Walk(objects, kidRef.Target, visited, treeNodes, result, options, diagnostics, depth + 1, updatedInherited);
        }
    }

    /// <summary>
    /// Follows a <see cref="PdfReference"/> (or a short chain of them — an indirect object whose
    /// value is itself a reference is legal, if odd) to the object it names, so callers can
    /// type-check the resolved value the way ISO 32000-1 §7.3.10 intends. Non-reference values
    /// are returned unchanged; a chain longer than <see cref="MaxReferenceChain"/> is treated as
    /// unresolvable and returned as-is so a reference cycle cannot spin.
    /// </summary>
    private static PdfObject ResolveIndirect(ObjectRegistry objects, PdfObject value)
    {
        var hops = 0;
        while (value is PdfReference reference && hops++ < MaxReferenceChain)
        {
            value = objects[reference.Target];
        }

        return value;
    }


    /// <summary>Copies <paramref name="node"/>, adding whichever of the four inheritable attributes it doesn't already set for itself from <paramref name="inherited"/>.</summary>
    private static PdfDictionary ResolveInheritedAttributes(ObjectRegistry objects, PdfDictionary node, InheritedAttributes inherited)
    {
        var merged = new PdfDictionary();
        foreach (var (key, value) in node)
        {
            merged.Set(key, value);
        }

        MaterializeGeometry(objects, merged, node.ContainsKey(MediaBoxName) ? null : inherited.MediaBox, MediaBoxName);
        MaterializeGeometry(objects, merged, node.ContainsKey(CropBoxName) ? null : inherited.CropBox, CropBoxName);
        MaterializeGeometry(objects, merged, node.ContainsKey(RotateName) ? null : inherited.Rotate, RotateName);

        if (!merged.ContainsKey(ResourcesName) && inherited.Resources is not null)
        {
            merged.Set(ResourcesName, inherited.Resources);
        }

        return merged;
    }

    /// <summary>
    /// Writes the page's effective <c>/MediaBox</c>, <c>/CropBox</c> or <c>/Rotate</c> into
    /// <paramref name="merged"/> as a DIRECT value: <paramref name="inheritedValue"/> when the
    /// node doesn't set the key itself, and in either case with any <see cref="PdfReference"/> —
    /// the whole value, or an element of the box array — resolved through the registry. The
    /// geometry consumers (<c>PageSpace.GetMediaBox</c>/<c>GetRotation</c>, the raster, stamp and
    /// redaction engines) only see this merged dictionary and cannot resolve references
    /// themselves; before this, `/MediaBox 9 0 R` silently fell back to US Letter and
    /// `/Rotate 15 0 R` to 0 with no diagnostic (the same
    /// resolve-before-type-check defect as the `/Kids` fix, emitted by the same producers).
    /// PDFium's <c>CPDF_Dictionary::GetArrayFor</c>/<c>GetNumberAt</c> resolve both levels.
    /// </summary>
    private static void MaterializeGeometry(ObjectRegistry objects, PdfDictionary merged, PdfObject? inheritedValue, PdfName key)
    {
        PdfObject? value;
        if (merged.TryGetValue(key, out var own))
        {
            value = own;
        }
        else if (inheritedValue is not null)
        {
            value = inheritedValue;
        }
        else
        {
            return;
        }

        var resolved = ResolveIndirect(objects, value);
        if (resolved is PdfArray array)
        {
            var anyReference = false;
            var items = new PdfObject[array.Count];
            for (var i = 0; i < array.Count; i++)
            {
                var item = array[i];
                if (item is PdfReference)
                {
                    anyReference = true;
                    item = ResolveIndirect(objects, item);
                }

                items[i] = item;
            }

            if (anyReference)
            {
                resolved = new PdfArray(items);
            }
        }

        if (!ReferenceEquals(resolved, own))
        {
            merged.Set(key, resolved);
        }
    }

    private static void ReportDeviation(string code, string message, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        if (options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message));
    }
}
