namespace PlumePdf.Documents.Structure;

/// <summary>
/// The read side of tagged PDF (ISO 32000-1 §14.7): resolves an opened
/// <see cref="PdfDocument"/>'s <c>/MarkInfo</c>, <c>/Lang</c>, and <c>/StructTreeRoot</c> into
/// the same <see cref="StructureElement"/>/<see cref="MarkedContentReference"/> model
/// <see cref="StructureTreeBuilder"/> writes from — lenient by default (the "open
/// anything" philosophy applies here just as it does to the rest of the reading engine): a
/// malformed or cyclic structure tree records a diagnostic and truncates rather than throwing,
/// unless <see cref="PdfOptions.Strict"/> is set. Consumed by the public
/// <see cref="PdfStructureInfo"/> facade and by <c>ReadingOrderer</c>'s structure-tree-order
/// path.
/// </summary>
internal static class StructureTreeReader
{
    private static readonly PdfName MarkInfoName = PdfName.Get("MarkInfo");
    private static readonly PdfName MarkInfoMarkedName = PdfName.Get("Marked");
    private static readonly PdfName LangName = PdfName.Get("Lang");
    private static readonly PdfName StructTreeRootName = PdfName.Get("StructTreeRoot");
    private static readonly PdfName SName = PdfName.Get("S");
    private static readonly PdfName PgName = PdfName.Get("Pg");
    private static readonly PdfName KName = PdfName.Get("K");
    private static readonly PdfName AltName = PdfName.Get("Alt");
    private static readonly PdfName ActualTextName = PdfName.Get("ActualText");
    private static readonly PdfName MCRName = PdfName.Get("MCR");
    private static readonly PdfName MCIDName = PdfName.Get("MCID");
    private static readonly PdfName AttributeName = PdfName.Get("A");
    private static readonly PdfName ScopeName = PdfName.Get("Scope");
    private static readonly PdfName StructElemName = PdfName.Get("StructElem");

    /// <summary>
    /// Reads <paramref name="document"/>'s tagging metadata and structure tree, if any.
    /// Never throws for a malformed document (unless <paramref name="options"/> is
    /// <see cref="PdfOptions.Strict"/>) — deviations go to <paramref name="diagnostics"/>, and a
    /// document with no catalog, no <c>/StructTreeRoot</c>, or an unresolvable one simply reads
    /// back as "not tagged" (<see cref="StructureTreeReadResult.Root"/> is <see langword="null"/>).
    /// </summary>
    public static StructureTreeReadResult Read(PdfDocument document, PdfOptions options, DiagnosticCollection diagnostics)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var catalog = document.Catalog;
        if (catalog is null)
        {
            return new StructureTreeReadResult(false, null, null);
        }

        var isMarked = false;
        if (catalog.Dictionary.TryGetValue(MarkInfoName, out var markInfoValue) && Resolve(document, markInfoValue) is PdfDictionary markInfo
            && markInfo.TryGetValue(MarkInfoMarkedName, out var markedValue) && markedValue is PdfBoolean markedBoolean)
        {
            isMarked = markedBoolean.Value;
        }

        string? language = null;
        if (catalog.Dictionary.TryGetValue(LangName, out var langValue) && Resolve(document, langValue) is PdfString langString)
        {
            language = langString.GetText();
        }

        if (!catalog.Dictionary.TryGetValue(StructTreeRootName, out var structTreeRootValue) || Resolve(document, structTreeRootValue) is not PdfDictionary structTreeRootDict)
        {
            return new StructureTreeReadResult(isMarked, language, null);
        }

        var pageIndexByRef = new Dictionary<IndirectReference, int>(document.Pages.Count);
        for (var i = 0; i < document.Pages.Count; i++)
        {
            pageIndexByRef[document.Pages[i].Reference] = i;
        }

        var context = new ReadContext(document, options, diagnostics, pageIndexByRef, MaxDepth(options), MaxNodes(options));
        var topLevel = ReadChildren(structTreeRootDict.TryGetValue(KName, out var rootKids) ? rootKids : PdfNull.Instance, context, currentPageRef: null, parent: null, depth: 0);

        StructureElement? root = topLevel.Count switch
        {
            0 => null,
            1 when topLevel[0] is StructureElement single => single,
            _ => WrapAsRoot(topLevel),
        };

        return new StructureTreeReadResult(isMarked, language, root);
    }

    private static StructureElement WrapAsRoot(List<StructureTreeNode> children)
    {
        var wrapper = new StructureElement { Role = "StructTreeRoot" };
        foreach (var child in children)
        {
            child.Parent = wrapper;
            wrapper.Children.Add(child);
        }

        return wrapper;
    }

    private static List<StructureTreeNode> ReadChildren(PdfObject kValue, ReadContext ctx, IndirectReference? currentPageRef, StructureElement? parent, int depth)
    {
        var resolved = Resolve(ctx.Document, kValue);
        var results = new List<StructureTreeNode>();

        if (resolved is PdfArray array)
        {
            foreach (var item in array)
            {
                var node = ReadOneChild(item, ctx, currentPageRef, parent, depth);
                if (node is not null)
                {
                    results.Add(node);
                }
            }
        }
        else
        {
            var node = ReadOneChild(kValue, ctx, currentPageRef, parent, depth);
            if (node is not null)
            {
                results.Add(node);
            }
        }

        return results;
    }

    private static StructureTreeNode? ReadOneChild(PdfObject rawValue, ReadContext ctx, IndirectReference? currentPageRef, StructureElement? parent, int depth)
    {
        // A bare integer /K entry is a shorthand MCID that inherits its page from the nearest
        // ancestor's /Pg (ISO 32000-1 §14.7.4.3) — resolved without following any indirect
        // reference (a raw integer is never itself indirect).
        if (rawValue is PdfNumber mcidNumber)
        {
            // TryToInt32, never a raw cast: a hostile /K entry of 1e20 would otherwise wrap
            // silently to int.MinValue (the read-side convention every document-supplied
            // number follows).
            if (!mcidNumber.TryToInt32(out var shorthandMcid) || shorthandMcid < 0)
            {
                ReportDeviation(ctx, "PLUME6068", $"A structure tree /K entry's shorthand MCID ({mcidNumber.Value}) is not a non-negative integer in range; skipping it.");
                return null;
            }

            return ResolveMcid(shorthandMcid, currentPageRef, ctx);
        }

        var cycleRef = rawValue as PdfReference;
        var resolved = Resolve(ctx.Document, rawValue);

        if (resolved is PdfDictionary dict)
        {
            var isMcr = dict.TryGetValue(PdfName.Type, out var typeValue) && typeValue is PdfName typeName && typeName == MCRName;
            if (isMcr)
            {
                var pageRef = dict.TryGetValue(PgName, out var pgValue) && pgValue is PdfReference pgReference ? pgReference.Target : currentPageRef;

                // TryToInt32 (lenient skip + diagnostic), never a raw cast — see the shorthand
                // MCID above for the same hostile-value reasoning.
                int? mcid = dict.TryGetValue(MCIDName, out var mcidValue) && mcidValue is PdfNumber n && n.TryToInt32(out var mcrMcid) && mcrMcid >= 0
                    ? mcrMcid
                    : null;
                if (pageRef is null || mcid is null)
                {
                    ReportDeviation(ctx, "PLUME6068", "A marked-content reference (/MCR) has no resolvable /Pg (directly or inherited) or no usable non-negative integer /MCID; skipping it.");
                    return null;
                }

                return ResolveMcid(mcid.Value, pageRef, ctx);
            }

            return ReadElement(dict, cycleRef?.Target, ctx, currentPageRef, parent, depth);
        }

        ReportDeviation(ctx, "PLUME6068", "A structure tree /K entry did not resolve to an integer MCID, an /MCR dictionary, or a structure element dictionary; skipping it.");
        return null;
    }

    private static MarkedContentReference? ResolveMcid(int mcid, IndirectReference? pageRef, ReadContext ctx)
    {
        if (pageRef is not { } pr || !ctx.PageIndexByRef.TryGetValue(pr, out var pageIndex))
        {
            ReportDeviation(ctx, "PLUME6068", $"A marked-content reference (MCID {mcid}) points at a page not present in this document's page tree; skipping it.");
            return null;
        }

        return new MarkedContentReference { PageIndex = pageIndex, Mcid = mcid };
    }

    private static StructureElement? ReadElement(PdfDictionary dict, IndirectReference? ownReference, ReadContext ctx, IndirectReference? currentPageRef, StructureElement? parent, int depth)
    {
        if (depth > ctx.MaxDepth)
        {
            ReportDeviation(ctx, "PLUME6069", $"The structure tree nests deeper than {ctx.MaxDepth} (PdfOptions.MaxStructureTreeDepth); truncating here.");
            return null;
        }

        if (ownReference is { } selfRef)
        {
            if (!ctx.Visiting.Add(selfRef))
            {
                ReportDeviation(ctx, "PLUME6069", $"The structure tree contains a cycle back to object {selfRef}; truncating here.");
                return null;
            }
        }

        if (ctx.NodeCount >= ctx.MaxNodes)
        {
            ReportDeviation(ctx, "PLUME6069", $"The structure tree exceeds {ctx.MaxNodes} elements (PdfOptions.MaxStructureElementCount); truncating here.");
            if (ownReference is { } r)
            {
                ctx.Visiting.Remove(r);
            }

            return null;
        }

        ctx.NodeCount++;

        var role = dict.TryGetValue(SName, out var sValue) && sValue is PdfName sName ? sName.Value : "Unknown";
        var language = dict.TryGetValue(LangName, out var langValue) && Resolve(ctx.Document, langValue) is PdfString langString ? langString.GetText() : null;
        var alt = dict.TryGetValue(AltName, out var altValue) && Resolve(ctx.Document, altValue) is PdfString altString ? altString.GetText() : null;
        var actualText = dict.TryGetValue(ActualTextName, out var actualValue) && Resolve(ctx.Document, actualValue) is PdfString actualString ? actualString.GetText() : null;
        var tableScope = ReadTableScope(dict, ctx);

        var element = new StructureElement
        {
            Role = role,
            Language = language,
            AlternateText = alt,
            ActualText = actualText,
            TableHeaderScope = tableScope,
            Parent = parent,
        };

        var ownPageRef = dict.TryGetValue(PgName, out var pgValue) && pgValue is PdfReference pgRef ? pgRef.Target : currentPageRef;

        if (dict.TryGetValue(KName, out var kValue))
        {
            foreach (var child in ReadChildren(kValue, ctx, ownPageRef, element, depth + 1))
            {
                child.Parent = element;
                element.Children.Add(child);
            }
        }

        if (ownReference is { } toRelease)
        {
            ctx.Visiting.Remove(toRelease);
        }

        return element;
    }

    private static string? ReadTableScope(PdfDictionary dict, ReadContext ctx)
    {
        if (!dict.TryGetValue(AttributeName, out var attrValue))
        {
            return null;
        }

        var resolved = Resolve(ctx.Document, attrValue);
        var attrDict = resolved switch
        {
            PdfDictionary d => d,
            PdfArray { Count: > 0 } arr when Resolve(ctx.Document, arr[0]) is PdfDictionary first => first,
            _ => null,
        };

        return attrDict is not null && attrDict.TryGetValue(ScopeName, out var scopeValue) && scopeValue is PdfName scopeName ? scopeName.Value : null;
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) => value is PdfReference reference ? document.Objects[reference.Target] : value;

    // The dedicated structure-tree caps (a document-supplied tree is its own hazard surface,
    // distinct from generic object-graph nesting or a Manuscript's element count).
    private static int MaxDepth(PdfOptions options) => options.MaxStructureTreeDepth;

    private static int MaxNodes(PdfOptions options) => options.MaxStructureElementCount;

    private static void ReportDeviation(ReadContext ctx, string code, string message)
    {
        if (ctx.Options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        ctx.Diagnostics.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message));
    }

    private sealed class ReadContext(PdfDocument document, PdfOptions options, DiagnosticCollection diagnostics, Dictionary<IndirectReference, int> pageIndexByRef, int maxDepth, int maxNodes)
    {
        public PdfDocument Document { get; } = document;
        public PdfOptions Options { get; } = options;
        public DiagnosticCollection Diagnostics { get; } = diagnostics;
        public Dictionary<IndirectReference, int> PageIndexByRef { get; } = pageIndexByRef;
        public int MaxDepth { get; } = maxDepth;
        public int MaxNodes { get; } = maxNodes;
        public int NodeCount { get; set; }
        public HashSet<IndirectReference> Visiting { get; } = [];
    }
}

/// <summary>The result of <see cref="StructureTreeReader.Read"/>.</summary>
/// <param name="IsMarked">The catalog's <c>/MarkInfo</c>/<c>/Marked</c> flag.</param>
/// <param name="Language">The catalog's <c>/Lang</c>, if present.</param>
/// <param name="Root">
/// The document's structure tree, or <see langword="null"/> when there is no catalog, no
/// resolvable <c>/StructTreeRoot</c>, or <c>/StructTreeRoot</c> has no children. When
/// <c>/StructTreeRoot/K</c> resolves to exactly one element, that element is returned directly
/// (the shape every PlumePDF-authored document has — a single <see cref="StructureRoles.Document"/>
/// root); when it has more than one top-level child (legal for a third-party structure tree),
/// they are wrapped under a synthetic <c>"StructTreeRoot"</c>-role container so callers always
/// see one root.
/// </param>
internal readonly record struct StructureTreeReadResult(bool IsMarked, string? Language, StructureElement? Root);
