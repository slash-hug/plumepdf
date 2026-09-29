using System.Text;
using PlumePdf.Objects;

namespace PlumePdf.Documents.PageRemoval;

/// <summary>
/// Resolves a destination or an action to the page object it targets in this document: explicit
/// destination arrays, destination dictionaries (<c>/D</c>), names through the catalog's
/// <c>/Dests</c>, strings through the <c>/Names /Dests</c> name tree, and <c>GoTo</c> actions
/// (following <c>/Next</c>). Remote, embedded, launch, URI, JavaScript and named actions are
/// never local.
/// </summary>
/// <remarks>
/// <para>
/// An action chain is walked in execution order (the action, then its <c>/Next</c> actions in
/// order, depth first), and the <em>first</em> <c>GoTo</c> it meets decides: a link or bookmark
/// is dead exactly when the first place it navigates to is a removed page. A <c>GoTo</c> later
/// in the chain is not what the reader sees first and does not make it dead.
/// </para>
/// <para>
/// Integer page indexes (legal only for remote destinations, but seen in local ones) are not
/// references and are never resolved. Every value is resolved before it is type-checked, and
/// every walk is cycle-guarded and capped; malformed input resolves to "not local", never an
/// exception.
/// </para>
/// </remarks>
internal sealed class DestinationResolver
{
    private const int MaxReferenceChain = 8;
    private const int MaxIndirection = 8; // name → value → /D → ... hops
    private const int MaxActions = 1024; // actions visited in one /Next walk
    private const int MaxNameTreeNodes = 1_000_000; // nodes plus entries read from the name tree

    private static readonly PdfName DestsName = PdfName.Get("Dests");
    private static readonly PdfName NamesName = PdfName.Get("Names");
    private static readonly PdfName DName = PdfName.Get("D");
    private static readonly PdfName SName = PdfName.Get("S");
    private static readonly PdfName GoToName = PdfName.Get("GoTo");
    private static readonly PdfName NextName = PdfName.Get("Next");
    private static readonly PdfName DestName = PdfName.Get("Dest");
    private static readonly PdfName AName = PdfName.Get("A");

    private readonly ObjectRegistry _objects;
    private readonly PdfDictionary? _dests;
    private readonly PdfObject? _nameTreeRoot;
    private readonly int _maxTreeDepth;
    private Dictionary<string, PdfObject>? _nameTree;

    /// <summary>Creates a resolver over <paramref name="catalog"/>'s named destinations.</summary>
    public DestinationResolver(ObjectRegistry objects, PdfDictionary? catalog, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(options);
        _objects = objects;
        _maxTreeDepth = options.MaxObjectNestingDepth;
        if (catalog is not null)
        {
            _dests = Resolve(objects, catalog.TryGetValue(DestsName, out var dests) ? dests : null) as PdfDictionary;
            if (Resolve(objects, catalog.TryGetValue(NamesName, out var names) ? names : null) is PdfDictionary namesDictionary
                && namesDictionary.TryGetValue(DestsName, out var tree))
            {
                _nameTreeRoot = tree;
            }
        }
    }

    /// <summary>
    /// A resolver over the document's own named destinations — never a clean-up pass's copy of
    /// the catalog, so what a destination resolves to does not depend on which passes ran first.
    /// </summary>
    public static DestinationResolver ForOriginalNames(SaveCleanupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var catalog = context.CatalogReference is { } reference && context.Objects[reference] is PdfDictionary original
            ? original
            : context.Catalog;
        return new DestinationResolver(context.Objects, catalog, context.Options);
    }

    /// <summary>The object number of the page <paramref name="destinationOrAction"/> targets in this document, or <see langword="null"/> when it is not a local page destination.</summary>
    public static int? ResolveLocalPage(ObjectRegistry objects, PdfDictionary? catalog, PdfObject destinationOrAction, PdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(destinationOrAction);
        return new DestinationResolver(objects, catalog, options).Resolve(destinationOrAction);
    }

    /// <summary>The page <paramref name="destinationOrAction"/> targets, as <see cref="ResolveLocalPage"/>.</summary>
    public int? Resolve(PdfObject destinationOrAction)
    {
        ArgumentNullException.ThrowIfNull(destinationOrAction);
        return Resolve(_objects, destinationOrAction) is PdfDictionary dictionary && dictionary.ContainsKey(SName)
            ? ResolveAction(destinationOrAction)
            : ResolveDestination(destinationOrAction, hops: 0);
    }

    /// <summary>The page an outline item or link annotation targets through its <c>/Dest</c> (preferred) or <c>/A</c>.</summary>
    public int? ResolveTarget(PdfDictionary item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.TryGetValue(DestName, out var destination))
        {
            return ResolveDestination(destination, hops: 0);
        }

        return item.TryGetValue(AName, out var action) ? ResolveAction(action) : null;
    }

    /// <summary>Resolves <paramref name="value"/> through a (capped) chain of references.</summary>
    internal static PdfObject? Resolve(ObjectRegistry objects, PdfObject? value)
    {
        var hops = 0;
        while (value is PdfReference reference && hops++ < MaxReferenceChain)
        {
            value = objects[reference.Target];
        }

        return value;
    }

    private int? ResolveDestination(PdfObject? value, int hops)
    {
        if (hops > MaxIndirection)
        {
            return null;
        }

        switch (Resolve(_objects, value))
        {
            case PdfArray array:
                // The page is the first element's reference itself; an integer is a page index.
                return array.Count > 0 && array[0] is PdfReference page ? page.Target.Number : null;
            case PdfName name:
                return _dests is not null && _dests.TryGetValue(name, out var named) ? ResolveDestination(named, hops + 1) : null;
            case PdfString text:
                return NameTree().TryGetValue(Key(text), out var entry) ? ResolveDestination(entry, hops + 1) : null;
            case PdfDictionary dictionary when !dictionary.ContainsKey(SName) && dictionary.TryGetValue(DName, out var inner):
                return ResolveDestination(inner, hops + 1);
            default:
                return null;
        }
    }

    // Walks the chain in execution order; the first GoTo decides.
    private int? ResolveAction(PdfObject action)
    {
        var pending = new Stack<PdfObject>();
        var visited = new HashSet<int>();
        var visits = 0;
        pending.Push(action);
        while (pending.Count > 0 && visits++ < MaxActions)
        {
            var next = pending.Pop();
            if (next is PdfReference reference && !visited.Add(reference.Target.Number))
            {
                continue;
            }

            if (Resolve(_objects, next) is not PdfDictionary dictionary)
            {
                continue;
            }

            if (dictionary.TryGetValue(SName, out var type) && ReferenceEquals(Resolve(_objects, type), GoToName))
            {
                return dictionary.TryGetValue(DName, out var destination) ? ResolveDestination(destination, hops: 0) : null;
            }

            if (dictionary.TryGetValue(NextName, out var nextValue))
            {
                if (Resolve(_objects, nextValue) is PdfArray chain)
                {
                    for (var i = chain.Count - 1; i >= 0; i--)
                    {
                        pending.Push(chain[i]);
                    }
                }
                else
                {
                    pending.Push(nextValue);
                }
            }
        }

        return null;
    }

    // The /Names /Dests tree flattened once, first entry for a key winning.
    private Dictionary<string, PdfObject> NameTree()
    {
        if (_nameTree is not null)
        {
            return _nameTree;
        }

        _nameTree = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        if (_nameTreeRoot is null)
        {
            return _nameTree;
        }

        var pending = new Stack<(PdfObject Node, int Depth)>();
        var visited = new HashSet<int>();
        var budget = MaxNameTreeNodes;
        pending.Push((_nameTreeRoot, 0));
        while (pending.Count > 0 && budget-- > 0)
        {
            var (next, depth) = pending.Pop();
            if (depth > _maxTreeDepth || (next is PdfReference reference && !visited.Add(reference.Target.Number))
                || Resolve(_objects, next) is not PdfDictionary node)
            {
                continue;
            }

            if (node.TryGetValue(NamesName, out var namesValue) && Resolve(_objects, namesValue) is PdfArray names)
            {
                for (var i = 0; i + 1 < names.Count && budget-- > 0; i += 2)
                {
                    if (Resolve(_objects, names[i]) is PdfString key)
                    {
                        _nameTree.TryAdd(Key(key), names[i + 1]);
                    }
                }
            }

            if (node.TryGetValue(PdfName.Kids, out var kidsValue) && Resolve(_objects, kidsValue) is PdfArray kids)
            {
                for (var i = kids.Count - 1; i >= 0; i--)
                {
                    pending.Push((kids[i], depth + 1));
                }
            }
        }

        return _nameTree;
    }

    /// <summary>A name-tree key as an ordinal string (one char per byte).</summary>
    internal static string Key(PdfString key) => Encoding.Latin1.GetString(key.Bytes.Span);
}
