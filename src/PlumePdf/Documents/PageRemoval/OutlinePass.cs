using PlumePdf.Objects;

namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Outlines: an item whose destination resolves to a removed page is deleted, its children
/// moving up to its parent in its place; /First, /Last, /Prev, /Next, /Parent and /Count are
/// rebuilt, and an outline left empty is dropped from the catalog.
/// </summary>
/// <remarks>
/// <para>
/// An item's destination is its <c>/Dest</c>, or else its <c>/A</c> action (the first
/// <c>GoTo</c> of the chain decides, see <see cref="DestinationResolver"/>). A surviving item
/// keeps its open or closed state; its <c>/Count</c> is recomputed from the rebuilt tree (ISO
/// 32000-1 §12.3.3): positive for an open item — the number of descendants visible under it —
/// negative for a closed one — the number that would be visible if it were opened — and absent
/// when it has no children. The root's <c>/Count</c> is the number of items visible at all levels.
/// </para>
/// <para>
/// A sibling chain that loops back ends where it first repeats. An outline deeper or larger
/// than the caps, or one that names the catalog or a kept page as an item, is left exactly as it
/// was (the excluded set still keeps removed pages out of it).
/// </para>
/// </remarks>
internal static class OutlinePass
{
    private const int MaxItems = 1_000_000;

    private static readonly PdfName OutlinesName = PdfName.Get("Outlines");
    private static readonly PdfName LastName = PdfName.Get("Last");
    private static readonly PdfName NextName = PdfName.Get("Next");
    private static readonly PdfName CountName = PdfName.Get("Count");

    /// <summary>Applies this pass to <paramref name="context"/>.</summary>
    public static void Apply(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.RemovedPages.Count == 0 || context.Catalog is not { } catalog
            || !catalog.TryGetValue(OutlinesName, out var outlines) || outlines is not PdfReference rootReference
            || context.Objects[rootReference.Target] is not PdfDictionary root)
        {
            return;
        }

        var protectedNumbers = new HashSet<int>(context.Pages.Select(static p => p.Reference.Number));
        if (context.CatalogReference is { } catalogReference)
        {
            protectedNumbers.Add(catalogReference.Number);
        }

        var reader = new Reader(context, DestinationResolver.ForOriginalNames(context), protectedNumbers, rootReference.Target.Number);
        var items = reader.ReadChain(root, depth: 0);
        if (items is null || !reader.AnyDeleted)
        {
            return;
        }

        var deleted = new List<Item>();
        var kept = Promote(items, deleted);
        foreach (var item in deleted)
        {
            context.Excluded.Add(item.Reference.Number);
        }

        context.Counts.Bookmarks += deleted.Count;

        if (kept.Count == 0)
        {
            var copy = new PdfDictionary();
            foreach (var (key, value) in catalog)
            {
                if (!ReferenceEquals(key, OutlinesName))
                {
                    copy.Set(key, value);
                }
            }

            context.Catalog = copy;
            context.Excluded.Add(rootReference.Target.Number);
            return;
        }

        var visible = 0;
        foreach (var item in kept)
        {
            visible += 1 + (item.Open ? Visible(item) : 0);
        }

        Rewrite(context, rootReference.Target, root, parent: null, previous: null, next: null, kept, visible);
        Link(context, rootReference.Target, kept);
    }

    // The surviving items in place of `items`: a deleted item is replaced by its own survivors.
    private static List<Item> Promote(List<Item> items, List<Item> deleted)
    {
        var result = new List<Item>();
        foreach (var item in items)
        {
            var children = Promote(item.Children, deleted);
            if (item.Deleted)
            {
                deleted.Add(item);
                result.AddRange(children);
            }
            else
            {
                item.Kept = children;
                result.Add(item);
            }
        }

        return result;
    }

    // Descendants visible under an item when it is open.
    private static int Visible(Item item)
    {
        var count = 0;
        foreach (var child in item.Kept)
        {
            count += 1 + (child.Open ? Visible(child) : 0);
        }

        return count;
    }

    private static void Link(SaveCleanupContext context, IndirectReference parent, List<Item> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var visible = Visible(item);
            Rewrite(
                context,
                item.Reference,
                item.Dictionary,
                parent,
                i > 0 ? items[i - 1].Reference : null,
                i < items.Count - 1 ? items[i + 1].Reference : null,
                item.Kept,
                item.Open ? visible : -visible);
            Link(context, item.Reference, item.Kept);
        }
    }

    // Writes a replacement for one node when its links differ from the rebuilt ones. `parent` is
    // null for the root, which has no /Parent, /Prev or /Next.
    private static void Rewrite(
        SaveCleanupContext context,
        IndirectReference reference,
        PdfDictionary node,
        IndirectReference? parent,
        IndirectReference? previous,
        IndirectReference? next,
        List<Item> children,
        int count)
    {
        var links = new (PdfName Key, PdfObject? Value)[]
        {
            (PdfName.Parent, parent is { } p ? new PdfReference(p) : null),
            (PdfName.Prev, previous is { } v ? new PdfReference(v) : null),
            (NextName, next is { } n ? new PdfReference(n) : null),
            (PdfName.First, children.Count > 0 ? new PdfReference(children[0].Reference) : null),
            (LastName, children.Count > 0 ? new PdfReference(children[^1].Reference) : null),
            (CountName, children.Count > 0 ? PdfNumber.Get(count) : null),
        };

        // The root's /Parent, /Prev and /Next are not links; leave whatever it has.
        var relevant = parent is null ? links[3..] : links;
        if (relevant.All(link => Same(context.Objects, node, link.Key, link.Value)))
        {
            return;
        }

        var copy = new PdfDictionary();
        foreach (var (key, value) in node)
        {
            copy.Set(key, value);
        }

        foreach (var (key, value) in relevant)
        {
            if (value is null)
            {
                copy.Remove(key);
            }
            else
            {
                copy.Set(key, value);
            }
        }

        context.Replacements[reference.Number] = copy;
    }

    private static bool Same(ObjectRegistry objects, PdfDictionary node, PdfName key, PdfObject? desired)
    {
        if (!node.TryGetValue(key, out var current))
        {
            return desired is null;
        }

        return desired switch
        {
            null => false,
            PdfReference reference => current is PdfReference existing && existing.Target == reference.Target,
            PdfNumber number => DestinationResolver.Resolve(objects, current) is PdfNumber existing && existing.IsInteger && existing.Value == number.Value,
            _ => false,
        };
    }

    private sealed class Item(IndirectReference reference, PdfDictionary dictionary, bool deleted, bool open)
    {
        public IndirectReference Reference { get; } = reference;

        public PdfDictionary Dictionary { get; } = dictionary;

        public bool Deleted { get; } = deleted;

        public bool Open { get; } = open;

        public List<Item> Children { get; set; } = [];

        public List<Item> Kept { get; set; } = [];
    }

    private sealed class Reader(SaveCleanupContext context, DestinationResolver resolver, HashSet<int> protectedNumbers, int rootNumber)
    {
        private readonly HashSet<int> _visited = [rootNumber];
        private int _items;

        public bool AnyDeleted { get; private set; }

        // The items of one sibling chain, or null when the outline is beyond the caps or names a
        // protected object (the whole pass then leaves it alone).
        public List<Item>? ReadChain(PdfDictionary parent, int depth)
        {
            var items = new List<Item>();
            if (!parent.TryGetValue(PdfName.First, out var value))
            {
                return items;
            }

            if (depth > context.Options.MaxObjectNestingDepth)
            {
                return null;
            }

            while (value is PdfReference reference)
            {
                var number = reference.Target.Number;
                if (protectedNumbers.Contains(number) || ++_items > MaxItems)
                {
                    return null;
                }

                // A repeat ends the chain (a loop, or an item already placed elsewhere).
                if (!_visited.Add(number) || context.Objects[reference.Target] is not PdfDictionary dictionary)
                {
                    break;
                }

                var deleted = context.Excluded.Contains(number)
                    || (resolver.ResolveTarget(dictionary) is int page && context.RemovedPages.Contains(page));
                AnyDeleted |= deleted;
                var open = dictionary.TryGetValue(CountName, out var count) && DestinationResolver.Resolve(context.Objects, count) is PdfNumber { Value: > 0 };
                var children = ReadChain(dictionary, depth + 1);
                if (children is null)
                {
                    return null;
                }

                items.Add(new Item(reference.Target, dictionary, deleted, open) { Children = children });
                if (!dictionary.TryGetValue(NextName, out value))
                {
                    break;
                }
            }

            return items;
        }
    }
}
