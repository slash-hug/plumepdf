namespace PlumePdf.Documents;

/// <summary>One <c>/Subtype /Widget</c> annotation found on a page's <c>/Annots</c> array.</summary>
internal readonly record struct PageWidget(IndirectReference Reference, PdfDictionary Dictionary, IndirectReference? OwningFieldReference);

/// <summary>
/// Reads the widget annotations on a page's <c>/Annots</c> array (ISO 32000-1 §12.5.6.19) —
/// the read side <c>docs/architecture.md</c>/Phase 3 never built
/// (<c>grep -rn 'Annots' src/</c> returns zero hits on the Phase-3-exited tree). Used by
/// <see cref="FormFlattener"/> to enumerate every widget on a page (for stamping and
/// removal) independently of which field-tree walk found it — a page can, in principle,
/// carry a widget whose owning field is unreachable from <c>/AcroForm/Fields</c> (a
/// malformed document), and flatten needs to know about it either way.
/// </summary>
internal static class WidgetAnnotationReader
{
    /// <summary>Reads every <c>/Subtype /Widget</c> entry in <paramref name="pageDictionary"/>'s <c>/Annots</c> array.</summary>
    public static IReadOnlyList<PageWidget> ReadPageWidgets(ObjectRegistry objects, PdfDictionary pageDictionary, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(pageDictionary);
        ArgumentNullException.ThrowIfNull(options);

        var result = new List<PageWidget>();
        if (!pageDictionary.TryGetValue(AcroFormNames.Annots, out var annotsValue) || AcroFormReader.Resolve(objects, annotsValue) is not PdfArray annots)
        {
            return result;
        }

        foreach (var entry in annots)
        {
            if (result.Count >= options.MaxWidgetsPerPage)
            {
                throw new PlumePdfException("PLUME6035", $"A page's /Annots array contains more than the configured limit of {options.MaxWidgetsPerPage} widgets — refusing to continue into a hostile or pathologically large page.");
            }

            if (entry is not PdfReference annotRef)
            {
                Report(diagnostics, options, "A /Annots entry was not an indirect reference; skipping it.");
                continue;
            }

            if (AcroFormReader.Resolve(objects, entry) is not PdfDictionary annotDict)
            {
                Report(diagnostics, options, $"/Annots entry {annotRef.Target} did not resolve to a dictionary; skipping it.");
                continue;
            }

            var isWidget = annotDict.TryGetValue(AcroFormNames.Subtype, out var subtype) && subtype is PdfName subtypeName && ReferenceEquals(subtypeName, AcroFormNames.Widget);
            if (!isWidget)
            {
                continue;
            }

            var owningField = ResolveOwningField(objects, annotRef.Target, annotDict);
            result.Add(new PageWidget(annotRef.Target, annotDict, owningField));
        }

        return result;
    }

    /// <summary>
    /// The field that owns this widget: itself, if the widget dictionary carries its own
    /// <c>/T</c> (the merged field+widget case), otherwise the nearest ancestor reached by
    /// following <c>/Parent</c> up to (and including) the first node that carries <c>/T</c>.
    /// </summary>
    private static IndirectReference? ResolveOwningField(ObjectRegistry objects, IndirectReference selfReference, PdfDictionary dict)
    {
        if (dict.ContainsKey(AcroFormNames.T))
        {
            return selfReference;
        }

        var current = dict;
        var visited = new HashSet<int> { selfReference.Number };
        var depth = 0;

        while (current.TryGetValue(AcroFormNames.Parent, out var parentValue) && parentValue is PdfReference parentRef)
        {
            if (depth++ > 64 || !visited.Add(parentRef.Target.Number))
            {
                return null;
            }

            if (AcroFormReader.Resolve(objects, parentValue) is not PdfDictionary parentDict)
            {
                return null;
            }

            if (parentDict.ContainsKey(AcroFormNames.T))
            {
                return parentRef.Target;
            }

            current = parentDict;
        }

        return null;
    }

    private static void Report(DiagnosticCollection? diagnostics, PdfOptions options, string message)
    {
        const string code = "PLUME6040";
        if (options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message));
    }
}
