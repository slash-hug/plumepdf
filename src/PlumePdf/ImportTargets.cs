using PlumePdf.Objects;

namespace PlumePdf;

/// <summary>
/// Where <see cref="PageGraphCopier"/> puts the objects it copies: it asks for a fresh object
/// number before building each copy (so every reference to the original resolves to the same
/// number, whatever order the copies are built in) and stores the built value under it.
/// </summary>
internal interface IImportTarget
{
    /// <summary>A fresh, not-yet-used reference in the target.</summary>
    IndirectReference Allocate();

    /// <summary>Stores <paramref name="value"/> under <paramref name="reference"/>, which <see cref="Allocate"/> returned.</summary>
    void Store(IndirectReference reference, PdfObject value);
}

/// <summary>
/// A brand-new document's object table, numbered from 1 with generation 0: the target behind
/// <see cref="Pdf.Merge(PdfDocument[])"/> and <see cref="Pdf.Split(PdfDocument)"/>.
/// </summary>
internal sealed class FreshDocumentTarget : IImportTarget
{
    private int _nextNumber = 1;

    /// <summary>The objects stored so far, by object number.</summary>
    public Dictionary<int, PdfObject> Objects { get; } = [];

    /// <summary>One past the highest number allocated: the trailer's <c>/Size</c>.</summary>
    public int Size => _nextNumber;

    /// <inheritdoc/>
    public IndirectReference Allocate() => new(_nextNumber++, 0);

    /// <inheritdoc/>
    public void Store(IndirectReference reference, PdfObject value) => Objects[reference.Number] = value;
}

/// <summary>
/// Where page-add copies a page before it touches the destination: every copy gets a provisional
/// reference (a number from <see cref="ProvisionalBase"/> up, at generation
/// <see cref="ProvisionalGeneration"/>, which no real reference uses), so the copy can be built,
/// and fail, without allocating anything in the destination. <see cref="Commit"/> then allocates
/// real numbers, registers the copies and rewrites every provisional reference; a reference that
/// merely looks provisional (a malformed real one) is left as it is.
/// </summary>
internal sealed class StagingTarget : IImportTarget
{
    /// <summary>The first provisional object number.</summary>
    internal const int ProvisionalBase = 1 << 30;

    /// <summary>The generation of every provisional reference: above the 65,535 a real one can have.</summary>
    internal const int ProvisionalGeneration = int.MaxValue;

    private int _nextNumber = ProvisionalBase;
    private Dictionary<int, IndirectReference>? _committed;

    /// <summary>The staged objects, by provisional number.</summary>
    public Dictionary<int, PdfObject> Objects { get; } = [];

    /// <summary>Whether <paramref name="reference"/> is a provisional number handed out by a staging target.</summary>
    public static bool IsProvisional(IndirectReference reference) => reference.Number >= ProvisionalBase && reference.Generation == ProvisionalGeneration;

    /// <inheritdoc/>
    public IndirectReference Allocate() => new(_nextNumber++, ProvisionalGeneration);

    /// <inheritdoc/>
    public void Store(IndirectReference reference, PdfObject value) => Objects[reference.Number] = value;

    /// <summary>
    /// Allocates a real number in <paramref name="registry"/> for every staged object, in
    /// provisional order (so the result is deterministic), rewrites the provisional references
    /// inside the staged objects, and registers them. The caller holds the registry's
    /// <see cref="ObjectRegistry.EnterExternalMutation"/>; nothing here reads the registry.
    /// </summary>
    public void Commit(ObjectRegistry registry)
    {
        var numbers = Objects.Keys.Order().ToList();
        _committed = new Dictionary<int, IndirectReference>(numbers.Count);
        foreach (var number in numbers)
        {
            _committed[number] = registry.AllocateNumber();
        }

        foreach (var number in numbers)
        {
            var value = Rewrite(Objects[number]);
            registry.RegisterNew(_committed[number], value);
        }
    }

    /// <summary>The real reference <paramref name="provisional"/> was committed as.</summary>
    public IndirectReference RealReferenceOf(IndirectReference provisional) => _committed![provisional.Number];

    /// <summary>
    /// <paramref name="value"/> with every provisional reference replaced by its committed one:
    /// a reference is replaced, a container is rewritten in place (staged containers are owned
    /// copies), anything else is returned as it is.
    /// </summary>
    public PdfObject Rewrite(PdfObject value)
    {
        switch (value)
        {
            case PdfReference reference when IsProvisional(reference.Target) && _committed!.TryGetValue(reference.Target.Number, out var committed):
                return new PdfReference(committed);

            case PdfArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = Rewrite(array[i]);
                }

                return array;

            case PdfDictionary dictionary:
                foreach (var (key, item) in dictionary.ToList())
                {
                    var rewritten = Rewrite(item);
                    if (!ReferenceEquals(rewritten, item))
                    {
                        dictionary.Set(key, rewritten);
                    }
                }

                return dictionary;

            case PdfStream stream:
                Rewrite(stream.Dictionary);
                return stream;

            default:
                return value;
        }
    }
}
