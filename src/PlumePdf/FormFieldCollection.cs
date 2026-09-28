using System.Collections;
using PlumePdf.Documents;

namespace PlumePdf;

/// <summary>
/// A document's interactive form fields, obtained from <see cref="PdfForm.Fields"/>.
/// Enumerable in field-tree (document) order; indexable by position or by name using the
/// following lookup semantics (exact fully-qualified match, else a unique
/// trailing-segment match — an ambiguous partial name throws <c>PLUME6030</c> naming every
/// candidate rather than silently guessing).
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("f1040.pdf");
/// var form = PdfForm.For(document);
/// Console.WriteLine($"{form.Fields.Count} fields");
/// foreach (var field in form.Fields)
/// {
///     Console.WriteLine($"{field.FullName}: {field.FieldType}");
/// }
/// </code>
/// </example>
public sealed class FormFieldCollection : IReadOnlyList<FormField>
{
    private readonly List<FormField> _fields;
    private readonly FieldNameIndex _index;
    private readonly Dictionary<AcroFormField, FormField> _byModel;

    internal FormFieldCollection(IReadOnlyList<AcroFormField> models, ObjectRegistry objects, PdfDocument? document = null, AcroFormReadResult? form = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(objects);
        _byModel = new Dictionary<AcroFormField, FormField>(models.Count);
        _fields = new List<FormField>(models.Count);
        foreach (var model in models)
        {
            var field = new FormField(model, objects, document, form);
            _fields.Add(field);
            _byModel[model] = field;
        }

        _index = new FieldNameIndex(models);
    }

    /// <inheritdoc/>
    public int Count => _fields.Count;

    /// <inheritdoc/>
    public FormField this[int index] => _fields[index];

    /// <summary>
    /// Looks up a field by name: an exact <see cref="FormField.FullName"/> match first, else
    /// the unique field whose trailing name segment matches <paramref name="name"/> (a
    /// trailing array-index suffix like <c>[0]</c> is ignored on both sides).
    /// </summary>
    /// <exception cref="PlumePdfException"><paramref name="name"/> matches more than one field by trailing segment (<c>PLUME6030</c>), or no field at all (<c>PLUME6031</c>).</exception>
    public FormField this[string name] => _byModel[_index.Resolve(name)];

    /// <summary>Attempts the same lookup as the string indexer, returning <see langword="false"/> instead of throwing when nothing matches. An ambiguous partial match still throws — the whole point is never silently guessing.</summary>
    /// <exception cref="PlumePdfException"><paramref name="name"/> matches more than one field by trailing segment (<c>PLUME6030</c>).</exception>
    public bool TryGetValue(string name, out FormField? field)
    {
        if (_index.TryResolve(name, out var model) && model is not null)
        {
            field = _byModel[model];
            return true;
        }

        field = null;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerator<FormField> GetEnumerator() => _fields.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
