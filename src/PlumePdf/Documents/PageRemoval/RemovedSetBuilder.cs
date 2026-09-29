using PlumePdf.Objects;

namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Computes the object numbers a full rewrite must exclude after pages were removed: the
/// removed pages themselves, every original page-tree node (the writer always replaces the tree
/// with a fresh flat <c>/Pages</c> node), and the annotations — form widgets included — that
/// belonged to a removed page. The writers never follow a reference into this set and write
/// <c>null</c> in its place, which is what keeps a removed page's content out of the output no
/// matter what still references it (an outline, a link, an open action, a form field, a
/// structure element, ...).
/// </summary>
/// <remarks>
/// Only objects that are unambiguously page-owned join the set. An entry of a removed page's
/// <c>/Annots</c> joins only when it resolves to an annotation dictionary (a <c>/Subtype</c>
/// name, and a <c>/Type</c> that is absent or <c>/Annot</c>), so a malformed <c>/Annots</c>
/// naming a shared font or XObject can never null it on the kept pages; and an annotation a kept
/// page also lists stays (keep wins). The catalog, the trailer's <c>/Info</c> and the
/// <c>/AcroForm</c> dictionary are never excluded.
/// </remarks>
internal static class RemovedSetBuilder
{
    private const int MaxReferenceChain = 8;

    private static readonly PdfName AnnotsName = PdfName.Get("Annots");
    private static readonly PdfName AnnotTypeName = PdfName.Get("Annot");
    private static readonly PdfName FieldsName = PdfName.Get("Fields");
    private static readonly PdfName PName = PdfName.Get("P");
    private static readonly PdfName StructTreeRootName = PdfName.Get("StructTreeRoot");
    private static readonly PdfName KName = PdfName.Get("K");
    private static readonly PdfName PgName = PdfName.Get("Pg");
    private static readonly PdfName ObjName = PdfName.Get("Obj");
    private static readonly PdfName McrName = PdfName.Get("MCR");
    private static readonly PdfName ObjrName = PdfName.Get("OBJR");
    private static readonly PdfName[] ValueKeys = [PdfName.Get("V"), PdfName.Get("RV")];

    /// <summary>Builds the exclusion set for a full rewrite of <paramref name="keptPages"/>.</summary>
    /// <param name="objects">The document's object graph.</param>
    /// <param name="catalogReference">The catalog's identity, when it has one.</param>
    /// <param name="catalog">The catalog dictionary, when resolvable.</param>
    /// <param name="openTimePageTree">Every page-tree node and page the document was opened with.</param>
    /// <param name="openTimePages">The pages the document was opened with, in their original order.</param>
    /// <param name="keptPages">The pages being saved.</param>
    /// <param name="options">The effective save options (walk caps).</param>
    /// <returns>The excluded object numbers, and the subset that are removed pages.</returns>
    public static (HashSet<int> Excluded, HashSet<int> RemovedPages) Build(
        ObjectRegistry objects,
        IndirectReference? catalogReference,
        PdfDictionary? catalog,
        IReadOnlySet<int> openTimePageTree,
        IReadOnlyList<IndirectReference> openTimePages,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> keptPages,
        PdfOptions options)
    {
        var kept = new HashSet<int>(keptPages.Select(static p => p.Reference.Number));

        var excluded = new HashSet<int>(openTimePageTree);
        excluded.ExceptWith(kept);

        var removedPages = new HashSet<int>();
        foreach (var page in openTimePages)
        {
            if (!kept.Contains(page.Number))
            {
                removedPages.Add(page.Number);
            }
        }

        if (removedPages.Count == 0)
        {
            return (excluded, removedPages);
        }

        // Keep wins: an annotation any kept page lists is never excluded.
        var keptAnnotations = new HashSet<int>();
        foreach (var (_, dictionary) in keptPages)
        {
            foreach (var number in AnnotationNumbers(objects, dictionary))
            {
                keptAnnotations.Add(number);
            }
        }

        foreach (var pageNumber in removedPages)
        {
            if (objects[new IndirectReference(pageNumber, 0)] is not PdfDictionary page)
            {
                continue;
            }

            foreach (var number in AnnotationNumbers(objects, page))
            {
                if (!keptAnnotations.Contains(number) && IsAnnotation(objects[new IndirectReference(number, 0)]))
                {
                    excluded.Add(number);
                }
            }
        }

        // The form's field tree: a widget placed on a removed page is excluded even when no page
        // lists it in /Annots, and a field all of whose widgets are excluded is excluded with them
        // — value included — so a removed page's form data cannot survive through /Fields.
        if (catalog is not null && Resolve(objects, catalog.TryGetValue(PdfName.AcroForm, out var acroForm) ? acroForm : null) is PdfDictionary form
            && form.TryGetValue(FieldsName, out var fields))
        {
            var visited = new HashSet<int>();
            ExcludeFieldTree(objects, fields, removedPages, keptAnnotations, excluded, visited, depth: 0, options.MaxFieldTreeDepth);
        }

        // Tagged documents: a structure element whose every content item is on a removed page (or
        // is an excluded annotation) is excluded, recursively — its /Alt and /ActualText
        // routinely repeat the removed page's text.
        if (catalog is not null && Resolve(objects, catalog.TryGetValue(StructTreeRootName, out var structRoot) ? structRoot : null) is PdfDictionary root
            && root.TryGetValue(KName, out var rootKids))
        {
            var walk = new StructureWalk(objects, removedPages, excluded, options.MaxStructureTreeDepth, options.MaxStructureElementCount);
            walk.Visit(rootKids, inheritedPage: null, depth: 0);
        }

        // Never excluded, whatever a malformed file makes them look like.
        if (catalogReference is { } catalogRef)
        {
            excluded.Remove(catalogRef.Number);
        }

        if (objects.Trailer.TryGetValue(PdfName.Info, out var info) && info is PdfReference infoRef)
        {
            excluded.Remove(infoRef.Target.Number);
        }

        if (catalog is not null && catalog.TryGetValue(PdfName.AcroForm, out var acroFormValue) && acroFormValue is PdfReference acroFormRef)
        {
            excluded.Remove(acroFormRef.Target.Number);
        }

        excluded.ExceptWith(kept);
        return (excluded, removedPages);
    }

    // Walks one /Kids (or /Fields) array; returns whether every entry it holds ended up excluded
    // (false for an empty or unreadable array, so an empty field is never removed on that basis).
    private static bool ExcludeFieldTree(
        ObjectRegistry objects,
        PdfObject kids,
        HashSet<int> removedPages,
        HashSet<int> keptAnnotations,
        HashSet<int> excluded,
        HashSet<int> visited,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth || Resolve(objects, kids) is not PdfArray array || array.Count == 0)
        {
            return false;
        }

        var allExcluded = true;
        foreach (var entry in array)
        {
            if (entry is not PdfReference reference)
            {
                allExcluded = false;
                continue;
            }

            var number = reference.Target.Number;
            if (!visited.Add(number) || objects[reference.Target] is not PdfDictionary node)
            {
                allExcluded &= excluded.Contains(number);
                continue;
            }

            if (node.TryGetValue(PdfName.Kids, out var children))
            {
                // A field with kids: excluded when every kid (widget or child field) is.
                if (ExcludeFieldTree(objects, children, removedPages, keptAnnotations, excluded, visited, depth + 1, maxDepth)
                    && !keptAnnotations.Contains(number))
                {
                    ExcludeField(node, number, excluded);
                }
            }
            else if (IsAnnotation(node) && !keptAnnotations.Contains(number)
                && node.TryGetValue(PName, out var page) && page is PdfReference pageRef
                && removedPages.Contains(pageRef.Target.Number))
            {
                // A widget (or merged field/widget) placed on a removed page, listed in some
                // /Annots or not.
                ExcludeField(node, number, excluded);
            }

            allExcluded &= excluded.Contains(number);
        }

        return allExcluded;
    }

    // A field's own value objects go with it: /V and /RV may be indirect (a signature field's
    // /V is its signature dictionary, which /Perms /DocMDP can also reach).
    private static void ExcludeField(PdfDictionary node, int number, HashSet<int> excluded)
    {
        excluded.Add(number);
        foreach (var key in ValueKeys)
        {
            if (node.TryGetValue(key, out var value) && value is PdfReference valueRef)
            {
                excluded.Add(valueRef.Target.Number);
            }
        }
    }

    // Where a structure node's content lives: nowhere (a grouping element with no content), only
    // on removed pages, or (at least partly) somewhere kept or unknown — the conservative answer.
    private enum Placement
    {
        None,
        Removed,
        Kept,
    }

    private sealed class StructureWalk(ObjectRegistry objects, HashSet<int> removedPages, HashSet<int> excluded, int maxDepth, int maxNodes)
    {
        private readonly HashSet<int> _visited = [];
        private int _nodes;

        // Visits a /K value (one item or an array of them) under the given default page.
        public Placement Visit(PdfObject kids, int? inheritedPage, int depth)
        {
            var value = Resolve(objects, kids);
            if (value is PdfArray array)
            {
                var placement = Placement.None;
                foreach (var item in array)
                {
                    placement = Combine(placement, VisitItem(item, inheritedPage, depth));
                }

                return placement;
            }

            return VisitItem(kids, inheritedPage, depth);
        }

        private Placement VisitItem(PdfObject item, int? page, int depth)
        {
            if (depth > maxDepth || ++_nodes > maxNodes)
            {
                return Placement.Kept;
            }

            if (item is PdfNumber)
            {
                return OnPage(page);
            }

            var ownNumber = item is PdfReference reference ? reference.Target.Number : (int?)null;
            if (ownNumber is int seen && !_visited.Add(seen))
            {
                return excluded.Contains(seen) ? Placement.Removed : Placement.Kept;
            }

            if (Resolve(objects, item) is not PdfDictionary node)
            {
                return Placement.Kept;
            }

            var ownPage = node.TryGetValue(PgName, out var pg) && pg is PdfReference pgRef ? pgRef.Target.Number : page;
            var type = node.TryGetValue(PdfName.Type, out var typeValue) ? typeValue : null;
            if (ReferenceEquals(type, McrName))
            {
                return OnPage(ownPage);
            }

            if (ReferenceEquals(type, ObjrName))
            {
                return node.TryGetValue(ObjName, out var obj) && obj is PdfReference objRef && excluded.Contains(objRef.Target.Number)
                    ? Placement.Removed
                    : Placement.Kept;
            }

            // A structure element.
            var placement = node.TryGetValue(KName, out var kids) ? Visit(kids, ownPage, depth + 1) : Placement.None;
            if (placement == Placement.Removed && ownNumber is int number)
            {
                excluded.Add(number);
            }

            return placement;
        }

        private Placement OnPage(int? page) =>
            page is int number && removedPages.Contains(number) ? Placement.Removed : Placement.Kept;

        private static Placement Combine(Placement a, Placement b) =>
            a == Placement.Kept || b == Placement.Kept ? Placement.Kept
            : a == Placement.Removed || b == Placement.Removed ? Placement.Removed
            : Placement.None;
    }

    /// <summary>The object numbers a page's resolved <c>/Annots</c> array lists (direct entries carry no number and are skipped).</summary>
    internal static IEnumerable<int> AnnotationNumbers(ObjectRegistry objects, PdfDictionary page)
    {
        if (!page.TryGetValue(AnnotsName, out var annots) || Resolve(objects, annots) is not PdfArray array)
        {
            yield break;
        }

        foreach (var entry in array)
        {
            if (entry is PdfReference reference)
            {
                yield return reference.Target.Number;
            }
        }
    }

    private static bool IsAnnotation(PdfObject value) =>
        value is PdfDictionary dictionary
        && dictionary.TryGetValue(PdfName.Subtype, out var subtype) && subtype is PdfName
        && (!dictionary.TryGetValue(PdfName.Type, out var type) || ReferenceEquals(type, AnnotTypeName));

    private static PdfObject? Resolve(ObjectRegistry objects, PdfObject? value)
    {
        var hops = 0;
        while (value is PdfReference reference && hops++ < MaxReferenceChain)
        {
            value = objects[reference.Target];
        }

        return value;
    }
}
