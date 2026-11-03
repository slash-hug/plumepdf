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
