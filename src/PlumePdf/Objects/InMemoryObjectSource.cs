namespace PlumePdf.Objects;

/// <summary>
/// An <see cref="IObjectSource"/> over a fully-materialized, in-memory object graph — backs
/// a synthetic <see cref="PdfDocument"/> with no source file, such as the result of
/// <c>Pdf.Merge</c>/<c>Pdf.Split</c>. Every value is already resolved (no lazy parsing, no
/// byte offsets), so <see cref="Resolve"/> is a plain dictionary lookup.
/// </summary>
internal sealed class InMemoryObjectSource : IObjectSource
{
    private readonly IReadOnlyDictionary<int, PdfObject> _objects;

    /// <summary>Creates a source over an already-built object graph.</summary>
    /// <param name="trailer">The synthetic trailer (<c>/Size</c>, <c>/Root</c>, <c>/ID</c>).</param>
    /// <param name="objects">Every object in the graph, keyed by its assigned object number.</param>
    public InMemoryObjectSource(PdfDictionary trailer, IReadOnlyDictionary<int, PdfObject> objects)
    {
        ArgumentNullException.ThrowIfNull(trailer);
        ArgumentNullException.ThrowIfNull(objects);
        Trailer = trailer;
        _objects = objects;
    }

    /// <inheritdoc/>
    public PdfDictionary Trailer { get; }

    /// <inheritdoc/>
    public PdfObject Resolve(IndirectReference reference) =>
        _objects.TryGetValue(reference.Number, out var value) ? value : PdfNull.Instance;
}
