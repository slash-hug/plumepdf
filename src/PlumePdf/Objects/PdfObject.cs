namespace PlumePdf;

/// <summary>
/// Base type of the eight PDF object kinds defined by ISO 32000-1 §7.3: boolean, number,
/// string, name, array, dictionary, stream, and null. Every concrete subtype is sealed
/// — pattern-match with <c>is</c>/<c>as</c> to inspect a value, e.g.
/// <c>if (value is PdfDictionary dict)</c>. This is the type returned throughout
/// <see cref="ObjectRegistry"/> (<c>doc.Objects</c>), the public "escape hatch, all the
/// way down" to a document's raw object graph.
/// </summary>
/// <example>
/// <code>
/// PdfObject value = document.Objects[reference];
/// if (value is PdfDictionary dictionary &amp;&amp; dictionary.TryGetValue(PdfName.Type, out var type))
/// {
///     Console.WriteLine(type);
/// }
/// </code>
/// </example>
public abstract class PdfObject
{
    private protected PdfObject()
    {
    }
}
