namespace PlumePdf.Documents.Structure;

/// <summary>
/// The write side of tagged PDF authoring (ISO 32000-1 §14.7): turns the
/// <see cref="StructureElement"/> tree <see cref="Layout.ManuscriptRenderer"/> builds while
/// painting a <see cref="Manuscript"/> into a real <c>/StructTreeRoot</c>, a <c>/ParentTree</c>
/// number tree (<see cref="NumberTreeBuilder"/>) mapping each tagged page's <c>/StructParents</c>
/// key to the structure elements that own its marked content, and a <c>/MarkInfo</c> dictionary.
/// <see cref="StructureTreeReader"/> is this type's read-side mirror — the same
/// <see cref="StructureElement"/>/<see cref="MarkedContentReference"/> model round-trips through
/// both (<c>StructureTreeTests</c>).
/// </summary>
/// <example>
/// <code>
/// var result = StructureTreeBuilder.Build(documentStructureRoot, pageRefs, options, document.Reserve, document.Set);
/// catalogDict.Set(PdfName.Get("StructTreeRoot"), new PdfReference(result.StructTreeRootReference));
/// catalogDict.Set(PdfName.Get("MarkInfo"), result.MarkInfoDictionary);
/// foreach (var pageIndex in result.TaggedPageIndices)
/// {
///     pageDictionaries[pageIndex].Set(PdfName.Get("StructParents"), PdfNumber.Get(pageIndex));
/// }
/// </code>
/// </example>
internal static class StructureTreeBuilder
{
    /// <summary>
    /// The hard ceiling on the number of <see cref="StructureElement"/> nodes a tree built from
    /// one <see cref="Manuscript"/> may contain (<c>PLUME6065</c>) —
    /// <see cref="PdfOptions.MaxStructureElementCount"/>, the dedicated cap shared with the
    /// read side (<see cref="StructureTreeReader"/>): a runaway document-generating agent's
    /// tagged element tree is the same hazard class there as a hostile document's tree here,
    /// and one caller-visible option must actually govern both.
    /// </summary>
    private static int MaxNodes(PdfOptions options) => options.MaxStructureElementCount;

    /// <summary>
    /// The hard ceiling on structure-tree nesting depth (<c>PLUME6066</c>) —
    /// <see cref="PdfOptions.MaxStructureTreeDepth"/>, the dedicated cap shared with the read
    /// side, with <see cref="MaxNodes"/>'s reasoning.
    /// </summary>
    private static int MaxDepth(PdfOptions options) => options.MaxStructureTreeDepth;

    private static readonly PdfName StructTreeRootName = PdfName.Get("StructTreeRoot");
    private static readonly PdfName ParentTreeName = PdfName.Get("ParentTree");
    private static readonly PdfName ParentTreeNextKeyName = PdfName.Get("ParentTreeNextKey");
    private static readonly PdfName StructElemName = PdfName.Get("StructElem");
    private static readonly PdfName SName = PdfName.Get("S");
    private static readonly PdfName PName = PdfName.Get("P");
    private static readonly PdfName PgName = PdfName.Get("Pg");
    private static readonly PdfName KName = PdfName.Get("K");
    private static readonly PdfName AltName = PdfName.Get("Alt");
    private static readonly PdfName ActualTextName = PdfName.Get("ActualText");
    private static readonly PdfName LangName = PdfName.Get("Lang");
    private static readonly PdfName MCRName = PdfName.Get("MCR");
    private static readonly PdfName MCIDName = PdfName.Get("MCID");
    private static readonly PdfName MarkInfoMarkedName = PdfName.Get("Marked");
    private static readonly PdfName AttributeName = PdfName.Get("A");
    private static readonly PdfName AttributeOwnerName = PdfName.Get("O");
    private static readonly PdfName AttributeOwnerTableValue = PdfName.Get("Table");
    private static readonly PdfName ScopeName = PdfName.Get("Scope");

    /// <summary>Builds <paramref name="documentRoot"/>'s structure tree into real PDF objects.</summary>
    /// <param name="documentRoot">The tree's single top-level element (conventionally <see cref="StructureRoles.Document"/>).</param>
    /// <param name="pageRefs">Every page's <see cref="IndirectReference"/>, in final document order — a <see cref="MarkedContentReference.PageIndex"/> indexes into this.</param>
    /// <param name="options">Controls the node-count/depth safety caps.</param>
    /// <param name="reserve">Reserves a fresh <see cref="IndirectReference"/> without yet assigning it a value (e.g. <c>DocumentBuilder.Reserve</c>) — needed because a child's <c>/P</c> parent-back-reference must exist before the parent's own dictionary is written.</param>
    /// <param name="set">Assigns a reserved reference's value (e.g. <c>DocumentBuilder.Set</c>).</param>
    /// <exception cref="PlumePdfException">
    /// <c>PLUME6065</c>/<c>PLUME6066</c> — the tree exceeds the node-count/depth safety cap.
    /// <c>PLUME6067</c> — a <see cref="MarkedContentReference.PageIndex"/> is outside <paramref name="pageRefs"/>'s range.
    /// </exception>
    public static StructureTreeBuildResult Build(
        StructureElement documentRoot,
        IReadOnlyList<IndirectReference> pageRefs,
        PdfOptions options,
        Func<IndirectReference> reserve,
        Action<IndirectReference, PdfObject> set)
    {
        ArgumentNullException.ThrowIfNull(documentRoot);
        ArgumentNullException.ThrowIfNull(pageRefs);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reserve);
        ArgumentNullException.ThrowIfNull(set);

        var structTreeRootRef = reserve();
        var refsByNode = new Dictionary<StructureElement, IndirectReference>();
        AssignReferences(documentRoot, reserve, refsByNode, depth: 0, options);

        var parentTreeEntries = new Dictionary<int, List<IndirectReference?>>();
        BuildElement(documentRoot, structTreeRootRef, refsByNode, pageRefs, parentTreeEntries, set);

        var numberTreeEntries = new List<(int Key, PdfObject Value)>(parentTreeEntries.Count);
        foreach (var (pageIndex, owners) in parentTreeEntries.OrderBy(static kv => kv.Key))
        {
            var array = new PdfArray();
            foreach (var owner in owners)
            {
                array.Add(owner is { } r ? new PdfReference(r) : PdfNull.Instance);
            }

            numberTreeEntries.Add((pageIndex, array));
        }

        var parentTreeRef = NumberTreeBuilder.Build(numberTreeEntries, value =>
        {
            var r = reserve();
            set(r, value);
            return r;
        });

        var rootDictionary = new PdfDictionary();
        rootDictionary.Set(PdfName.Type, StructTreeRootName);
        rootDictionary.Set(KName, new PdfArray([new PdfReference(refsByNode[documentRoot])]));
        rootDictionary.Set(ParentTreeName, new PdfReference(parentTreeRef));
        if (numberTreeEntries.Count > 0)
        {
            rootDictionary.Set(ParentTreeNextKeyName, PdfNumber.Get(numberTreeEntries.Max(static e => e.Key) + 1));
        }

        set(structTreeRootRef, rootDictionary);

        var markInfo = new PdfDictionary();
        markInfo.Set(MarkInfoMarkedName, PdfBoolean.True);

        return new StructureTreeBuildResult(structTreeRootRef, markInfo, [.. parentTreeEntries.Keys.OrderBy(static k => k)]);
    }

    private static void AssignReferences(StructureElement node, Func<IndirectReference> reserve, Dictionary<StructureElement, IndirectReference> refsByNode, int depth, PdfOptions options)
    {
        if (depth > MaxDepth(options))
        {
            throw new PlumePdfException(
                "PLUME6066",
                $"The structure tree nests deeper than the configured limit of {MaxDepth(options)} (PdfOptions.MaxStructureTreeDepth) — this guards against a runaway or accidentally-cyclic Element.Role/child tree.");
        }

        if (refsByNode.Count + 1 > MaxNodes(options))
        {
            throw new PlumePdfException(
                "PLUME6065",
                $"The structure tree exceeds the configured limit of {MaxNodes(options)} elements (PdfOptions.MaxStructureElementCount).");
        }

        refsByNode[node] = reserve();
        foreach (var child in node.Children)
        {
            if (child is StructureElement childElement)
            {
                AssignReferences(childElement, reserve, refsByNode, depth + 1, options);
            }
        }
    }

    private static void BuildElement(
        StructureElement node,
        IndirectReference parentRef,
        Dictionary<StructureElement, IndirectReference> refsByNode,
        IReadOnlyList<IndirectReference> pageRefs,
        Dictionary<int, List<IndirectReference?>> parentTreeEntries,
        Action<IndirectReference, PdfObject> set)
    {
        var ownRef = refsByNode[node];

        var dictionary = new PdfDictionary();
        dictionary.Set(PdfName.Type, StructElemName);
        dictionary.Set(SName, PdfName.Get(node.Role));
        dictionary.Set(PName, new PdfReference(parentRef));

        if (node.Language is { Length: > 0 } language)
        {
            dictionary.Set(LangName, PdfString.FromLiteral(System.Text.Encoding.Latin1.GetBytes(language)));
        }

        if (node.AlternateText is { Length: > 0 } alt)
        {
            dictionary.Set(AltName, ToPdfDocString(alt));
        }

        if (node.ActualText is { Length: > 0 } actualText)
        {
            dictionary.Set(ActualTextName, ToPdfDocString(actualText));
        }

        if (node.TableHeaderScope is { Length: > 0 } scope)
        {
            var attribute = new PdfDictionary();
            attribute.Set(AttributeOwnerName, AttributeOwnerTableValue);
            attribute.Set(ScopeName, PdfName.Get(scope));
            dictionary.Set(AttributeName, attribute);
        }

        var kids = new PdfArray();
        foreach (var child in node.Children)
        {
            switch (child)
            {
                case StructureElement childElement:
                    kids.Add(new PdfReference(refsByNode[childElement]));
                    BuildElement(childElement, ownRef, refsByNode, pageRefs, parentTreeEntries, set);
                    break;

                case MarkedContentReference mcr:
                    if (mcr.PageIndex < 0 || mcr.PageIndex >= pageRefs.Count)
                    {
                        throw new PlumePdfException(
                            "PLUME6067",
                            $"A structure element with role '{node.Role}' has a marked-content reference to page index {mcr.PageIndex}, but the document only has {pageRefs.Count} page(s).");
                    }

                    var mcrDictionary = new PdfDictionary();
                    mcrDictionary.Set(PdfName.Type, MCRName);
                    mcrDictionary.Set(PgName, new PdfReference(pageRefs[mcr.PageIndex]));
                    mcrDictionary.Set(MCIDName, PdfNumber.Get(mcr.Mcid));
                    kids.Add(mcrDictionary);

                    RegisterParentTreeEntry(parentTreeEntries, mcr.PageIndex, mcr.Mcid, ownRef);
                    break;
            }
        }

        dictionary.Set(KName, kids);
        set(ownRef, dictionary);
    }

    private static void RegisterParentTreeEntry(Dictionary<int, List<IndirectReference?>> parentTreeEntries, int pageIndex, int mcid, IndirectReference owner)
    {
        if (!parentTreeEntries.TryGetValue(pageIndex, out var owners))
        {
            owners = [];
            parentTreeEntries[pageIndex] = owners;
        }

        while (owners.Count <= mcid)
        {
            owners.Add(null);
        }

        owners[mcid] = owner;
    }

    // UTF-16BE with the §7.9.2.2 byte-order mark — the same lexical form PdfString.GetText()
    // already recognizes, so /Alt and /ActualText round-trip exactly for any Unicode alt text,
    // not just Latin-1 (unlike /Lang, which ISO 32000-1 always specifies as an ASCII BCP 47 tag).
    private static PdfString ToPdfDocString(string text)
    {
        var utf16 = System.Text.Encoding.BigEndianUnicode.GetBytes(text);
        var withBom = new byte[utf16.Length + 2];
        withBom[0] = 0xFE;
        withBom[1] = 0xFF;
        utf16.CopyTo(withBom, 2);
        return PdfString.FromLiteral(withBom);
    }
}

/// <summary>
/// The finished structure tree's entry points, ready for the caller (<c>DocumentBuilder</c> in
/// <see cref="Layout.ManuscriptRenderer"/>) to wire into the document catalog and each tagged
/// page's dictionary.
/// </summary>
/// <param name="StructTreeRootReference">The catalog's <c>/StructTreeRoot</c> value.</param>
/// <param name="MarkInfoDictionary">The catalog's <c>/MarkInfo</c> value (<c>&lt;&lt; /Marked true &gt;&gt;</c>).</param>
/// <param name="TaggedPageIndices">Every page index that owns at least one marked-content span — each needs its page dictionary's <c>/StructParents</c> set to its own index (the key <see cref="StructureTreeBuilder.Build"/> used for that page's <c>/ParentTree</c> entry).</param>
internal sealed record StructureTreeBuildResult(IndirectReference StructTreeRootReference, PdfDictionary MarkInfoDictionary, IReadOnlyList<int> TaggedPageIndices);
