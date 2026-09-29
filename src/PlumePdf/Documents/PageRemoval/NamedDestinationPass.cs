using PlumePdf.Objects;

namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Named destinations: entries of the catalog's /Dests dictionary and of the /Names /Dests name
/// tree that resolve to a removed page are dropped (name-tree /Limits recomputed); a container
/// left empty is dropped.
/// </summary>
/// <remarks>
/// <para>
/// A name-tree node that loses entries is written as a replacement copy under its own number
/// (a direct node is rebuilt in its parent's copy); a leaf or intermediate node left with nothing
/// leaves its parent's <c>/Kids</c>, and every surviving non-root node's <c>/Limits</c> is
/// recomputed from the keys actually under it. An emptied tree is dropped from <c>/Names</c>,
/// and <c>/Names</c> is dropped from the catalog when that leaves it empty; an emptied
/// <c>/Dests</c> dictionary is dropped from the catalog.
/// </para>
/// <para>
/// A name tree that loops, shares a node, holds a non-dictionary node, or exceeds the caps is
/// left exactly as it was (the excluded set still keeps removed pages out of it).
/// </para>
/// </remarks>
internal static class NamedDestinationPass
{
    private const int MaxNodes = 1_000_000;

    private static readonly PdfName DestsName = PdfName.Get("Dests");
    private static readonly PdfName NamesName = PdfName.Get("Names");
    private static readonly PdfName LimitsName = PdfName.Get("Limits");

    /// <summary>Applies this pass to <paramref name="context"/>.</summary>
    public static void Apply(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RemovedPages.Count == 0 || context.Catalog is not { } catalog)
        {
            return;
        }

        var resolver = DestinationResolver.ForOriginalNames(context);
        var protectedNumbers = new HashSet<int>(context.Pages.Select(static p => p.Reference.Number));
        if (context.CatalogReference is { } catalogReference)
        {
            protectedNumbers.Add(catalogReference.Number);
        }

        PdfDictionary? catalogCopy = null;
        PdfDictionary CatalogCopy() => catalogCopy ??= Copy(catalog);

        // PDF 1.1: the catalog's /Dests dictionary.
        if (catalog.TryGetValue(DestsName, out var destsValue) && DestinationResolver.Resolve(context.Objects, destsValue) is PdfDictionary dests)
        {
            var kept = new PdfDictionary();
            var dropped = 0;
            foreach (var (name, value) in dests)
            {
                if (IsDead(context, resolver, value))
                {
                    dropped++;
                    Exclude(context, protectedNumbers, value);
                }
                else
                {
                    kept.Set(name, value);
                }
            }

            if (dropped > 0)
            {
                context.Counts.NamedDestinations += dropped;
                if (kept.Count == 0)
                {
                    CatalogCopy().Remove(DestsName);
                    Exclude(context, protectedNumbers, destsValue);
                }
                else
                {
                    Place(context, protectedNumbers, destsValue, kept, direct => CatalogCopy().Set(DestsName, direct));
                }
            }
        }

        // PDF 1.2: the /Names /Dests name tree.
        if (catalog.TryGetValue(NamesName, out var namesValue) && DestinationResolver.Resolve(context.Objects, namesValue) is PdfDictionary names
            && names.TryGetValue(DestsName, out var treeRoot))
        {
            var rewriter = new TreeRewriter(context, resolver, protectedNumbers);
            if (rewriter.Rewrite(treeRoot, isRoot: true, depth: 0) is { Changed: true } outcome)
            {
                rewriter.Commit();
                if (outcome.Empty || outcome.Direct is not null)
                {
                    var namesCopy = Copy(names);
                    if (outcome.Empty)
                    {
                        namesCopy.Remove(DestsName);
                        Exclude(context, protectedNumbers, treeRoot);
                    }
                    else
                    {
                        namesCopy.Set(DestsName, outcome.Direct!);
                    }

                    if (namesCopy.Count == 0)
                    {
                        CatalogCopy().Remove(NamesName);
                        Exclude(context, protectedNumbers, namesValue);
                    }
                    else
                    {
                        Place(context, protectedNumbers, namesValue, namesCopy, direct => CatalogCopy().Set(NamesName, direct));
                    }
                }
            }
        }

        if (catalogCopy is not null)
        {
            context.Catalog = catalogCopy;
        }
    }

    private static bool IsDead(SaveCleanupContext context, DestinationResolver resolver, PdfObject value) =>
        resolver.Resolve(value) is int page && context.RemovedPages.Contains(page);

    private static void Exclude(SaveCleanupContext context, HashSet<int> protectedNumbers, PdfObject value)
    {
        if (value is PdfReference reference && !protectedNumbers.Contains(reference.Target.Number))
        {
            context.Excluded.Add(reference.Target.Number);
        }
    }

    // Writes `replacement` where `original` was: under its own number when it is an indirect
    // object, else through `setDirect` into the parent's copy.
    private static void Place(SaveCleanupContext context, HashSet<int> protectedNumbers, PdfObject original, PdfDictionary replacement, Action<PdfDictionary> setDirect)
    {
        if (original is PdfReference reference && context.Objects[reference.Target] is PdfDictionary && !protectedNumbers.Contains(reference.Target.Number))
        {
            context.Replacements[reference.Target.Number] = replacement;
        }
        else
        {
            setDirect(replacement);
        }
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

    private static int Compare(PdfString a, PdfString b) => a.Bytes.Span.SequenceCompareTo(b.Bytes.Span);

    // One node's rewrite. `Direct` is the rebuilt copy of a changed direct node (an indirect one
    // is replaced under its own number); `Least`/`Greatest` are the keys under it.
    private sealed record Outcome(bool Changed, bool Empty, PdfDictionary? Direct, PdfString? Least, PdfString? Greatest);

    // Rewrites the whole tree on the side and commits only once all of it was readable, so a
    // malformed tree is never half-rewritten.
    private sealed class TreeRewriter(SaveCleanupContext context, DestinationResolver resolver, HashSet<int> protectedNumbers)
    {
        private readonly HashSet<int> _visited = [];
        private readonly Dictionary<int, PdfObject> _replacements = [];
        private readonly List<PdfObject> _dropped = [];
        private int _nodes;
        private int _droppedEntries;

        public void Commit()
        {
            foreach (var (number, value) in _replacements)
            {
                context.Replacements[number] = value;
            }

            foreach (var value in _dropped)
            {
                Exclude(context, protectedNumbers, value);
            }

            context.Counts.NamedDestinations += _droppedEntries;
        }

        // The node's outcome, or null when the tree is not safe to rewrite.
        public Outcome? Rewrite(PdfObject value, bool isRoot, int depth)
        {
            if (depth > context.Options.MaxObjectNestingDepth || ++_nodes > MaxNodes)
            {
                return null;
            }

            if (value is PdfReference reference && (!_visited.Add(reference.Target.Number) || protectedNumbers.Contains(reference.Target.Number)))
            {
                return null;
            }

            if (DestinationResolver.Resolve(context.Objects, value) is not PdfDictionary node)
            {
                return null;
            }

            var changed = false;
            PdfString? least = null;
            PdfString? greatest = null;
            void Widen(PdfString? low, PdfString? high)
            {
                if (low is not null && (least is null || Compare(low, least) < 0))
                {
                    least = low;
                }

                if (high is not null && (greatest is null || Compare(high, greatest) > 0))
                {
                    greatest = high;
                }
            }

            PdfArray? pairs = null;
            if (node.TryGetValue(NamesName, out var namesValue) && DestinationResolver.Resolve(context.Objects, namesValue) is PdfArray names)
            {
                pairs = [];
                for (var i = 0; i < names.Count; i += 2)
                {
                    if (i + 1 < names.Count && IsDead(context, resolver, names[i + 1]))
                    {
                        changed = true;
                        _droppedEntries++;
                        _dropped.Add(names[i + 1]);
                        continue;
                    }

                    pairs.Add(names[i]);
                    if (i + 1 < names.Count)
                    {
                        pairs.Add(names[i + 1]);
                    }

                    if (DestinationResolver.Resolve(context.Objects, names[i]) is PdfString key)
                    {
                        Widen(key, key);
                    }
                }
            }

            PdfArray? kids = null;
            if (node.TryGetValue(PdfName.Kids, out var kidsValue) && DestinationResolver.Resolve(context.Objects, kidsValue) is PdfArray kidArray)
            {
                kids = [];
                foreach (var kid in kidArray)
                {
                    if (Rewrite(kid, isRoot: false, depth + 1) is not { } child)
                    {
                        return null;
                    }

                    changed |= child.Changed;
                    if (child.Empty)
                    {
                        _dropped.Add(kid);
                        continue;
                    }

                    kids.Add(child.Direct ?? kid);
                    Widen(child.Least, child.Greatest);
                }
            }

            if (!changed)
            {
                return new Outcome(false, false, null, least, greatest);
            }

            if ((pairs?.Count ?? 0) == 0 && (kids?.Count ?? 0) == 0)
            {
                return new Outcome(true, true, null, null, null);
            }

            var copy = Copy(node);
            if (pairs is not null)
            {
                SetOrRemove(copy, NamesName, pairs);
            }

            if (kids is not null)
            {
                SetOrRemove(copy, PdfName.Kids, kids);
            }

            if ((!isRoot || node.ContainsKey(LimitsName)) && least is not null && greatest is not null)
            {
                copy.Set(LimitsName, new PdfArray([least, greatest]));
            }
            else
            {
                copy.Remove(LimitsName);
            }

            if (value is PdfReference own)
            {
                _replacements[own.Target.Number] = copy;
                return new Outcome(true, false, null, least, greatest);
            }

            return new Outcome(true, false, copy, least, greatest);
        }

        private static void SetOrRemove(PdfDictionary dictionary, PdfName key, PdfArray value)
        {
            if (value.Count == 0)
            {
                dictionary.Remove(key);
            }
            else
            {
                dictionary.Set(key, value);
            }
        }
    }
}
