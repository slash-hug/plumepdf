namespace PlumePdf;

/// <summary>
/// The PDF <c>null</c> object (ISO 32000-1 §7.3.9). A singleton — every occurrence of
/// <c>null</c> in a document resolves to the same instance, <see cref="Instance"/>.
/// </summary>
/// <example>
/// <code>
/// if (value == PdfNull.Instance)
/// {
///     // the key is present but explicitly null, distinct from being absent
/// }
/// </code>
/// </example>
public sealed class PdfNull : PdfObject
{
    /// <summary>The single, shared instance of the PDF <c>null</c> object.</summary>
    public static PdfNull Instance { get; } = new();

    private PdfNull()
    {
    }

    /// <inheritdoc/>
    public override string ToString() => "null";
}
