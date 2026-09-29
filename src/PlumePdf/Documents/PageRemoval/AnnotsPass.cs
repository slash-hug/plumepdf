using PlumePdf.Objects;

namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Kept pages: their /Annots is written compacted, without entries in the excluded set, and a
/// link annotation whose destination resolves to a removed page is removed (and excluded).
/// </summary>
/// <remarks>
/// <para>
/// A changed <c>/Annots</c> is written as a direct array on the page's copy (an indirect array
/// may be shared, so it is never rewritten in place), and dropped from the page when nothing is
/// left. A page whose <c>/Annots</c> needs no change is left exactly as it was.
/// </para>
/// <para>
/// Kept annotations are tidied too: a <c>/Popup</c> or <c>/IRT</c> (with its <c>/RT</c>) naming an
/// excluded annotation is dropped from a replacement copy, and a pop-up whose <c>/Parent</c>
/// annotation is excluded goes with it. Widgets are left to the form's pass.
/// </para>
/// </remarks>
internal static class AnnotsPass
{
    private static readonly PdfName LinkName = PdfName.Get("Link");
    private static readonly PdfName PopupName = PdfName.Get("Popup");
    private static readonly PdfName IrtName = PdfName.Get("IRT");
    private static readonly PdfName RtName = PdfName.Get("RT");

    /// <summary>Applies this pass to <paramref name="context"/>.</summary>
    public static void Apply(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RemovedPages.Count == 0)
        {
            return;
        }

        var objects = context.Objects;
        var protectedNumbers = new HashSet<int>(context.Pages.Select(static p => p.Reference.Number));
        if (context.CatalogReference is { } catalogReference)
        {
            protectedNumbers.Add(catalogReference.Number);
        }

        // First every dead link, so the tidying below sees the whole excluded set.
        var resolver = DestinationResolver.ForOriginalNames(context);
        foreach (var (_, page) in context.Pages)
        {
            foreach (var entry in Entries(objects, page))
            {
                if (entry is PdfReference reference && !protectedNumbers.Contains(reference.Target.Number)
                    && !context.Excluded.Contains(reference.Target.Number)
                    && DestinationResolver.Resolve(objects, entry) is PdfDictionary annotation && IsDeadLink(annotation, resolver, context))
                {
                    context.Excluded.Add(reference.Target.Number);
                    context.Counts.Links++;
                }
            }
        }

        for (var i = 0; i < context.Pages.Count; i++)
        {
            var (pageReference, page) = context.Pages[i];
            if (!page.TryGetValue(PdfName.Annots, out var annotsValue))
            {
                continue;
            }

            var arrayExcluded = annotsValue is PdfReference arrayReference && context.Excluded.Contains(arrayReference.Target.Number);
            var array = arrayExcluded ? [] : DestinationResolver.Resolve(objects, annotsValue) as PdfArray;
            if (array is null)
            {
                continue;
            }

            var changed = arrayExcluded;
            var compacted = new PdfArray();
            foreach (var entry in array)
            {
                if (entry is PdfReference reference)
                {
                    var number = reference.Target.Number;
                    if (context.Excluded.Contains(number))
                    {
                        changed = true;
                        continue;
                    }

                    if (!protectedNumbers.Contains(number) && DestinationResolver.Resolve(objects, entry) is PdfDictionary annotation)
                    {
                        if (IsOrphanPopup(annotation, context))
                        {
                            context.Excluded.Add(number);
                            changed = true;
                            continue;
                        }

                        if (!context.Replacements.ContainsKey(number) && Detached(annotation, context) is { } tidied)
                        {
                            context.Replacements[number] = tidied;
                        }
                    }
                }
                else if (entry is PdfDictionary direct && IsDeadLink(direct, resolver, context))
                {
                    context.Counts.Links++;
                    changed = true;
                    continue;
                }

                compacted.Add(entry);
            }

            if (!changed)
            {
                continue;
            }

            var copy = Copy(page);
            if (compacted.Count == 0)
            {
                copy.Remove(PdfName.Annots);
            }
            else
            {
                copy.Set(PdfName.Annots, compacted);
            }

            context.Pages[i] = (pageReference, copy);
        }
    }

    private static IEnumerable<PdfObject> Entries(ObjectRegistry objects, PdfDictionary page) =>
        page.TryGetValue(PdfName.Annots, out var annots) && DestinationResolver.Resolve(objects, annots) is PdfArray array ? array : [];

    private static bool IsDeadLink(PdfDictionary annotation, DestinationResolver resolver, SaveCleanupContext context) =>
        annotation.TryGetValue(PdfName.Subtype, out var subtype) && ReferenceEquals(DestinationResolver.Resolve(context.Objects, subtype), LinkName)
        && resolver.ResolveTarget(annotation) is int page && context.RemovedPages.Contains(page);

    // A pop-up belongs to its parent annotation; with the parent gone it has nothing to show.
    private static bool IsOrphanPopup(PdfDictionary annotation, SaveCleanupContext context) =>
        annotation.TryGetValue(PdfName.Subtype, out var subtype) && ReferenceEquals(DestinationResolver.Resolve(context.Objects, subtype), PopupName)
        && annotation.TryGetValue(PdfName.Parent, out var parent) && parent is PdfReference parentReference
        && context.Excluded.Contains(parentReference.Target.Number);

    // A copy without the /Popup or /IRT that names an excluded annotation, or null when none does.
    private static PdfDictionary? Detached(PdfDictionary annotation, SaveCleanupContext context)
    {
        if (annotation.TryGetValue(PdfName.Subtype, out var subtype) && ReferenceEquals(DestinationResolver.Resolve(context.Objects, subtype), PdfName.Widget))
        {
            return null;
        }

        var dropPopup = annotation.TryGetValue(PopupName, out var popup) && popup is PdfReference popupReference && context.Excluded.Contains(popupReference.Target.Number);
        var dropReply = annotation.TryGetValue(IrtName, out var irt) && irt is PdfReference irtReference && context.Excluded.Contains(irtReference.Target.Number);
        if (!dropPopup && !dropReply)
        {
            return null;
        }

        var copy = Copy(annotation);
        if (dropPopup)
        {
            copy.Remove(PopupName);
        }

        if (dropReply)
        {
            copy.Remove(IrtName);
            copy.Remove(RtName);
        }

        return copy;
    }

    private static PdfDictionary Copy(PdfDictionary source)
    {
        var copy = new PdfDictionary();
        foreach (var (key, value) in source)
        {
            copy.Set(key, value);
        }

        return copy;
    }
}
