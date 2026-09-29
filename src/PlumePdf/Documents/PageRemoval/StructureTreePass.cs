using PlumePdf.Documents.Structure;
using PlumePdf.Objects;

namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Tagged documents: a structure element whose every content target (/Pg, marked-content and
/// object references) is on a removed page or annotation is pruned, recursively; /ParentTree and
/// /IDTree drop what was pruned and /ParentTreeNextKey stays valid; a tree left empty stays, empty.
/// </summary>
/// <remarks>
/// Every <c>/K</c> is compacted: entries that resolve into the exclusion set, marked content on a
/// removed page (an MCID inherits its element's <c>/Pg</c>), object references to an excluded
/// object or placed on a removed page, and dangling entries all leave it. A kept element whose
/// own <c>/Pg</c> names a removed page loses that <c>/Pg</c>; an element left with no content
/// after having some is pruned and leaves its parent in turn. Changed elements, the root and the
/// rebuilt number and name trees are written as copies — the document is never modified. A root
/// whose <c>/K</c> empties keeps its dictionary with <c>/K []</c> and a valid <c>/ParentTree</c>,
/// so the document stays tagged.
/// </remarks>
internal static class StructureTreePass
{
    private static readonly PdfName StructTreeRootName = PdfName.Get("StructTreeRoot");
    private static readonly PdfName KName = PdfName.Get("K");
    private static readonly PdfName PgName = PdfName.Get("Pg");
    private static readonly PdfName ObjName = PdfName.Get("Obj");
    private static readonly PdfName McrName = PdfName.Get("MCR");
    private static readonly PdfName ObjrName = PdfName.Get("OBJR");
    private static readonly PdfName ParentTreeName = PdfName.Get("ParentTree");
    private static readonly PdfName ParentTreeNextKeyName = PdfName.Get("ParentTreeNextKey");
    private static readonly PdfName StructParentsName = PdfName.Get("StructParents");
    private static readonly PdfName IdTreeName = PdfName.Get("IDTree");
    private static readonly PdfName NumsName = PdfName.Get("Nums");
    private static readonly PdfName NamesName = PdfName.Get("Names");

    /// <summary>Applies this pass to <paramref name="context"/>.</summary>
    public static void Apply(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Catalog is not { } catalog || !catalog.TryGetValue(StructTreeRootName, out var rootValue)
            || FormObjects.Resolve(context.Objects, rootValue) is not PdfDictionary root)
        {
            return;
        }

        var rewrite = new ElementRewrite(context);
        PdfDictionary? rootCopy = null;
        if (root.TryGetValue(KName, out var rootKids))
        {
            var kids = rewrite.RewriteKids(rootKids, inheritedPage: null, depth: 0);
            if (kids.Changed)
            {
                rootCopy = FormObjects.Copy(root);
                rootCopy.Set(KName, kids.Value ?? new PdfArray());
            }
        }

        RewriteParentTree(context, root, ref rootCopy);
        RewriteIdTree(context, root, ref rootCopy);

        if (rootCopy is null)
        {
            return;
        }

        if (rootValue is PdfReference rootReference)
        {
            context.Replacements[rootReference.Target.Number] = rootCopy;
        }
        else
        {
            var catalogCopy = FormObjects.Copy(catalog);
            catalogCopy.Set(StructTreeRootName, rootCopy);
            context.Catalog = catalogCopy;
        }
    }

    // /ParentTree maps each page's /StructParents (and each annotation's /StructParent) to the
    // element(s) owning its content. Entries of removed pages, and entries whose value is now
    // excluded or holds nothing but excluded elements, go; the tree is rebuilt flat.
    private static void RewriteParentTree(SaveCleanupContext context, PdfDictionary root, ref PdfDictionary? rootCopy)
    {
        if (!root.TryGetValue(ParentTreeName, out var treeValue))
        {
            return;
        }

        var removedKeys = new HashSet<int>();
        foreach (var page in context.RemovedPages)
        {
            if (context.Objects[new IndirectReference(page, 0)] is PdfDictionary pageDictionary
                && pageDictionary.TryGetValue(StructParentsName, out var key) && key is PdfNumber keyNumber && keyNumber.TryToInt32(out var keyValue))
            {
                removedKeys.Add(keyValue);
            }
        }

        var nodes = new List<int>();
        var entries = new List<(int Key, PdfObject Value)>();
        var readable = CollectTree(context, treeValue, NumsName, nodes, (key, value) =>
        {
            if (key is PdfNumber number && number.TryToInt32(out var intKey))
            {
                entries.Add((intKey, value));
                return true;
            }

            return false;
        });
        if (!readable)
        {
            return;
        }

        var kept = new List<(int Key, PdfObject Value)>();
        var dropped = new List<PdfObject>();
        foreach (var (key, value) in entries)
        {
            if (removedKeys.Contains(key) || IsGone(context, value))
            {
                dropped.Add(value);
            }
            else
            {
                kept.Add((key, value));
            }
        }

        if (dropped.Count == 0)
        {
            return;
        }

        foreach (var value in dropped)
        {
            // A removed page's own array (by MCID) goes with it; a referenced element does not
            // — it may still be kept.
            if (value is PdfReference reference && context.Objects[reference.Target] is PdfArray)
            {
                context.Excluded.Add(reference.Target.Number);
            }
        }

        context.Excluded.UnionWith(nodes);
        rootCopy ??= FormObjects.Copy(root);
        rootCopy.Set(ParentTreeName, new PdfReference(NumberTreeBuilder.Build(kept, context.Allocate)));

        if (root.TryGetValue(ParentTreeNextKeyName, out var next) && next is PdfNumber nextNumber && nextNumber.TryToInt32(out var nextKey)
            && kept.Count > 0 && kept.Max(static e => e.Key) >= nextKey)
        {
            rootCopy.Set(ParentTreeNextKeyName, PdfNumber.Get(kept.Max(static e => e.Key) + 1));
        }
    }

    // /IDTree maps element IDs to elements; entries naming a pruned element go. An emptied tree
    // is dropped (the entry is optional).
    private static void RewriteIdTree(SaveCleanupContext context, PdfDictionary root, ref PdfDictionary? rootCopy)
    {
        if (!root.TryGetValue(IdTreeName, out var treeValue))
        {
            return;
        }

        var nodes = new List<int>();
        var entries = new List<(PdfObject Key, PdfObject Value)>();
        var readable = CollectTree(context, treeValue, NamesName, nodes, (key, value) =>
        {
            entries.Add((key, value));
            return true;
        });
        if (!readable)
        {
            return;
        }

        var kept = entries.Where(e => !IsGone(context, e.Value)).ToList();
        if (kept.Count == entries.Count)
        {
            return;
        }

        context.Excluded.UnionWith(nodes);
        rootCopy ??= FormObjects.Copy(root);
        if (kept.Count == 0)
        {
            rootCopy.Remove(IdTreeName);
            return;
        }

        var names = new PdfArray();
        foreach (var (key, value) in kept)
        {
            names.Add(key);
            names.Add(value);
        }

        var node = new PdfDictionary();
        node.Set(NamesName, names);
        rootCopy.Set(IdTreeName, new PdfReference(context.Allocate(node)));
    }

    // Whether a tree value names nothing that is still written: an excluded or dangling
    // reference, or an array of nothing but those.
    private static bool IsGone(SaveCleanupContext context, PdfObject value)
    {
        if (value is PdfNull)
        {
            return true;
        }

        if (value is PdfReference reference)
        {
            if (context.Excluded.Contains(reference.Target.Number))
            {
                return true;
            }

            var target = context.Objects[reference.Target];
            if (target is PdfNull)
            {
                return true;
            }

            value = target;
        }

        return value is PdfArray array && array.All(item => item is PdfNull
            || (item is PdfReference itemReference && (context.Excluded.Contains(itemReference.Target.Number) || context.Objects[itemReference.Target] is PdfNull)));
    }

    // Walks a number or name tree (/Kids, leaf /Nums or /Names pairs), capped and cycle-guarded,
    // collecting the indirect node numbers. Returns false when the tree cannot be read whole —
    // the pass then leaves it alone rather than rebuild it from a part.
    private static bool CollectTree(SaveCleanupContext context, PdfObject treeValue, PdfName leafKey, List<int> nodes, Func<PdfObject, PdfObject, bool> add)
    {
        var visited = new HashSet<int>();
        var pending = new Stack<(PdfObject Node, int Depth)>();
        pending.Push((treeValue, 0));
        var budget = context.Options.MaxStructureElementCount;
        while (pending.Count > 0)
        {
            var (value, depth) = pending.Pop();
            if (depth > context.Options.MaxStructureTreeDepth || --budget < 0)
            {
                return false;
            }

            if (value is PdfReference reference)
            {
                if (!visited.Add(reference.Target.Number))
                {
                    return false;
                }

                nodes.Add(reference.Target.Number);
            }

            if (FormObjects.Resolve(context.Objects, value) is not PdfDictionary node)
            {
                return false;
            }

            if (node.TryGetValue(leafKey, out var leafValue))
            {
                if (FormObjects.Resolve(context.Objects, leafValue) is not PdfArray pairs)
                {
                    return false;
                }

                if (leafValue is PdfReference leafReference)
                {
                    nodes.Add(leafReference.Target.Number);
                }

                for (var i = 0; i + 1 < pairs.Count; i += 2)
                {
                    if (!add(pairs[i], pairs[i + 1]))
                    {
                        return false;
                    }
                }
            }

            if (node.TryGetValue(PdfName.Kids, out var kidsValue))
            {
                if (FormObjects.Resolve(context.Objects, kidsValue) is not PdfArray kids)
                {
                    return false;
                }

                // Pushed in reverse so the leaves are read in order.
                for (var i = kids.Count - 1; i >= 0; i--)
                {
                    pending.Push((kids[i], depth + 1));
                }
            }
        }

        return true;
    }

    private sealed class ElementRewrite(SaveCleanupContext context)
    {
        private readonly HashSet<int> _visited = [];
        private int _nodes;

        // Rewrites one /K value (one item or an array). Value is the new /K (null when nothing is
        // left); HadContent/HasContent say whether it held content before and after.
        public (PdfObject? Value, bool Changed, bool HadContent, bool HasContent) RewriteKids(PdfObject kids, int? inheritedPage, int depth)
        {
            var resolved = FormObjects.Resolve(context.Objects, kids);
            if (resolved is PdfArray array)
            {
                var result = new PdfArray();
                var changed = false;
                foreach (var item in array)
                {
                    var (value, itemChanged) = RewriteItem(item, inheritedPage, depth);
                    changed |= itemChanged;
                    if (value is not null)
                    {
                        result.Add(value);
                    }
                }

                return (changed ? result : kids, changed, array.Count > 0, result.Count > 0);
            }

            var (single, singleChanged) = RewriteItem(kids, inheritedPage, depth);
            return (single, singleChanged, resolved is not null and not PdfNull, single is not null);
        }

        // Rewrites one /K item; returns the item to keep (itself or a copy) or null to drop it.
        private (PdfObject? Value, bool Changed) RewriteItem(PdfObject item, int? page, int depth)
        {
            if (item is PdfNull)
            {
                return (null, true);
            }

            if (item is PdfNumber)
            {
                return OnRemovedPage(page) ? (null, true) : (item, false);
            }

            int? number = null;
            if (item is PdfReference reference)
            {
                number = reference.Target.Number;
                if (context.Excluded.Contains(reference.Target.Number))
                {
                    CountExcludedElement(reference.Target.Number);
                    return (null, true);
                }

                if (!_visited.Add(reference.Target.Number))
                {
                    return (item, false);
                }
            }

            var resolved = FormObjects.Resolve(context.Objects, item);
            if (resolved is PdfNull)
            {
                return (null, true);
            }

            if (resolved is not PdfDictionary node || depth > context.Options.MaxStructureTreeDepth
                || ++_nodes > context.Options.MaxStructureElementCount)
            {
                return (item, false);
            }

            var ownPage = node.TryGetValue(PgName, out var pg) && pg is PdfReference pgReference ? pgReference.Target.Number : page;
            var type = node.TryGetValue(PdfName.Type, out var typeValue) ? typeValue : null;
            if (ReferenceEquals(type, McrName))
            {
                return OnRemovedPage(ownPage) ? (null, true) : (item, false);
            }

            if (ReferenceEquals(type, ObjrName))
            {
                var gone = OnRemovedPage(ownPage)
                    || (node.TryGetValue(ObjName, out var obj) && obj is PdfReference objReference
                        && (context.Excluded.Contains(objReference.Target.Number) || context.Objects[objReference.Target] is PdfNull));
                return gone ? (null, true) : (item, false);
            }

            // A structure element.
            var ownPageRemoved = pg is PdfReference explicitPage && context.RemovedPages.Contains(explicitPage.Target.Number);
            PdfObject? newKids = null;
            var kidsChanged = false;
            if (node.TryGetValue(KName, out var kids))
            {
                var rewritten = RewriteKids(kids, ownPage, depth + 1);
                if (rewritten.HadContent && !rewritten.HasContent)
                {
                    Prune(number);
                    return (null, true);
                }

                newKids = rewritten.Value;
                kidsChanged = rewritten.Changed;
            }

            if (!kidsChanged && !ownPageRemoved)
            {
                return (item, false);
            }

            var copy = FormObjects.Copy(node);
            if (kidsChanged)
            {
                copy.Set(KName, newKids!);
            }

            if (ownPageRemoved)
            {
                copy.Remove(PgName);
            }

            if (number is int own)
            {
                context.Replacements[own] = copy;
                return (item, false);
            }

            return (copy, true);
        }

        private void Prune(int? number)
        {
            context.Counts.StructureElements++;
            if (number is int own)
            {
                context.Excluded.Add(own);
                _visited.Add(own);
            }
        }

        // An element the removed set already excluded, met in a /K: counted once.
        private void CountExcludedElement(int number)
        {
            if (_visited.Add(number) && context.Objects[new IndirectReference(number, 0)] is PdfDictionary node
                && !(node.TryGetValue(PdfName.Type, out var type) && (ReferenceEquals(type, McrName) || ReferenceEquals(type, ObjrName)))
                && (node.ContainsKey(PdfName.Get("S")) || ReferenceEquals(type, PdfName.Get("StructElem"))))
            {
                context.Counts.StructureElements++;
                CountExcludedDescendants(node, depth: 1);
            }
        }

        private void CountExcludedDescendants(PdfDictionary element, int depth)
        {
            if (depth > context.Options.MaxStructureTreeDepth || !element.TryGetValue(KName, out var kids))
            {
                return;
            }

            var items = FormObjects.Resolve(context.Objects, kids) is PdfArray array ? (IEnumerable<PdfObject>)array : [kids];
            foreach (var item in items)
            {
                if (item is PdfReference reference && context.Excluded.Contains(reference.Target.Number)
                    && context.Objects[reference.Target] is PdfDictionary child && child.ContainsKey(PdfName.Get("S"))
                    && _visited.Add(reference.Target.Number))
                {
                    context.Counts.StructureElements++;
                    CountExcludedDescendants(child, depth + 1);
                }
            }
        }

        private bool OnRemovedPage(int? page) => page is int number && context.RemovedPages.Contains(number);
    }
}
