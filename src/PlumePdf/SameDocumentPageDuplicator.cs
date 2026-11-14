using PlumePdf.Documents;
using PlumePdf.Objects;

namespace PlumePdf;

/// <summary>
/// Duplicates a page within its own document (<see cref="PageCollection.Insert(int, PdfPage)"/>
/// with a page of the destination). Only the page dictionary, its content streams and its
/// annotations are copied; resources and every other object are shared with the original page,
/// since they are the document's own and a full copy would only bloat the saved file. Nothing is
/// nulled: every reference still has its target.
/// </summary>
/// <remarks>
/// Annotations are copied shallowly and point at the new page through <c>/P</c>; a pop-up and
/// its parent annotation, both on the page, point at each other's copies. A reply's <c>/IRT</c>
/// and a link's <c>/Dest</c> keep their original targets. Widgets are left off (a field would
/// otherwise have two widgets in one place), with one <c>PLUME6094</c> per document. The copy
/// drops <c>/StructParents</c> and <c>/B</c>, as a page copied from another document does.
/// </remarks>
internal static class SameDocumentPageDuplicator
{
    private static readonly PdfName ParentName = PdfName.Get("Parent");
    private static readonly PdfName AnnotsName = PdfName.Get("Annots");
    private static readonly PdfName ContentsName = PdfName.Get("Contents");
    private static readonly PdfName StructParentsName = PdfName.Get("StructParents");
    private static readonly PdfName StructParentName = PdfName.Get("StructParent");
    private static readonly PdfName BeadsName = PdfName.Get("B");
    private static readonly PdfName SubtypeName = PdfName.Get("Subtype");
    private static readonly PdfName WidgetName = PdfName.Get("Widget");
    private static readonly PdfName PageName = PdfName.Get("P");
    private static readonly PdfName PopupName = PdfName.Get("Popup");

    /// <summary>Duplicates <paramref name="page"/> of <paramref name="document"/> at <paramref name="index"/> and returns the new page.</summary>
    internal static PdfPage Insert(PdfDocument document, int index, PdfPage page)
    {
        var preflight = PageAddEngine.Preflight(document);
        var objects = document.Objects;
        var staging = new StagingTarget();
        var pageReference = staging.Allocate();

        var copy = new PdfDictionary();
        foreach (var (key, value) in page.Dictionary)
        {
            if (!ReferenceEquals(key, ParentName) && !ReferenceEquals(key, AnnotsName) && !ReferenceEquals(key, ContentsName)
                && !ReferenceEquals(key, StructParentsName) && !ReferenceEquals(key, BeadsName))
            {
                copy.Set(key, value);
            }
        }

        if (page.Dictionary.TryGetValue(ContentsName, out var contents))
        {
            copy.Set(ContentsName, CopyContents(objects, staging, contents));
        }

        var droppedWidgets = CopyAnnotations(objects, staging, page.Dictionary, pageReference, copy);
        copy.Set(ParentName, new PdfReference(preflight.PagesReference));
        staging.Store(pageReference, copy);

        var added = PageAddEngine.Commit(document, index, staging, pageReference, copy, formPlan: null);

        if (droppedWidgets)
        {
            PageAddEngine.ReportOnce(document, "PLUME6094", DiagnosticSeverity.Info, "A page duplicated within this document was copied without its form widgets: a field can't have two widgets in one place. To get independent fields, open the file a second time and copy the page from that document.");
        }

        PageAddEngine.Report(document, preflight, sourceTagged: preflight.Claims.IsTagged, formPlan: null);
        return added;
    }

    // /Contents as a fresh stream (or array of fresh streams) with the same bytes, so the two pages
    // can be changed independently later.
    private static PdfObject CopyContents(ObjectRegistry objects, StagingTarget staging, PdfObject contents)
    {
        var resolved = contents is PdfReference reference ? objects[reference.Target] : contents;
        if (resolved is PdfArray array)
        {
            var copies = new PdfArray();
            foreach (var item in array)
            {
                copies.Add(CopyStream(objects, staging, item));
            }

            return contents is PdfReference ? Stage(staging, copies) : copies;
        }

        return CopyStream(objects, staging, contents);
    }

    private static PdfObject CopyStream(ObjectRegistry objects, StagingTarget staging, PdfObject value)
    {
        if (value is not PdfReference reference || objects[reference.Target] is not PdfStream stream)
        {
            return value; // not a stream reference: nothing to copy, share it as it is
        }

        var dictionary = new PdfDictionary();
        foreach (var (key, item) in stream.Dictionary)
        {
            dictionary.Set(key, item);
        }

        return Stage(staging, new PdfStream(dictionary, stream.RawBytes));
    }

    // Copies every non-widget annotation onto the new page; returns whether any widget was left off.
    private static bool CopyAnnotations(ObjectRegistry objects, StagingTarget staging, PdfDictionary original, IndirectReference newPage, PdfDictionary copy)
    {
        if (!original.TryGetValue(AnnotsName, out var annotsValue)
            || (annotsValue is PdfReference annotsRef ? objects[annotsRef.Target] : annotsValue) is not PdfArray annots)
        {
            return false;
        }

        var dropped = false;
        var copies = new PdfArray();
        var byOriginal = new Dictionary<int, (IndirectReference Reference, PdfDictionary Copy)>();
        foreach (var entry in annots)
        {
            var annotation = (entry is PdfReference r ? objects[r.Target] : entry) as PdfDictionary;
            if (annotation is null)
            {
                continue;
            }

            if (annotation.TryGetValue(SubtypeName, out var subtype) && ReferenceEquals(subtype, WidgetName))
            {
                dropped = true;
                continue;
            }

            var annotationCopy = new PdfDictionary();
            foreach (var (key, item) in annotation)
            {
                if (!ReferenceEquals(key, StructParentName))
                {
                    annotationCopy.Set(key, item);
                }
            }

            if (annotationCopy.ContainsKey(PageName))
            {
                annotationCopy.Set(PageName, new PdfReference(newPage));
            }

            if (entry is PdfReference originalRef)
            {
                var staged = staging.Allocate();
                staging.Store(staged, annotationCopy);
                byOriginal[originalRef.Target.Number] = (staged, annotationCopy);
                copies.Add(new PdfReference(staged));
            }
            else
            {
                copies.Add(annotationCopy);
            }
        }

        // A pop-up and its parent annotation, both copied, point at each other's copies.
        foreach (var (_, annotationCopy) in byOriginal.Values)
        {
            foreach (var key in new[] { PopupName, ParentName })
            {
                if (annotationCopy.TryGetValue(key, out var target) && target is PdfReference targetRef && byOriginal.TryGetValue(targetRef.Target.Number, out var copied))
                {
                    annotationCopy.Set(key, new PdfReference(copied.Reference));
                }
            }
        }

        if (copies.Count > 0)
        {
            copy.Set(AnnotsName, copies);
        }

        return dropped;
    }

    private static PdfReference Stage(StagingTarget staging, PdfObject value)
    {
        var reference = staging.Allocate();
        staging.Store(reference, value);
        return new PdfReference(reference);
    }
}
