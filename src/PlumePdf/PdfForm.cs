using PlumePdf.Documents;

namespace PlumePdf;

/// <summary>
/// The public facade over a document's interactive AcroForm (ISO 32000-1 §12.7)
/// — field enumeration/lookup (<see cref="Fields"/>), filling
/// (<see cref="Fill(IEnumerable{KeyValuePair{string,string}})"/>), and flattening
/// (<see cref="Flatten"/>).
/// </summary>
/// <remarks>
/// Obtain an instance via <see cref="For"/>, or the equivalent <see cref="PdfDocument.Form"/>
/// hub property (<c>doc.Form.Fields[...]</c>).
/// </remarks>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("f1040.pdf");
/// var form = PdfForm.For(document);
/// form.Fields["c1_01"].Value = "1";
/// document.SaveIncremental("f1040-filled.pdf");
/// </code>
/// </example>
public sealed class PdfForm
{
    private readonly PdfDocument _document;
    private readonly Lazy<AcroFormReadResult> _read;
    private readonly Lazy<FormFieldCollection> _fields;

    private PdfForm(PdfDocument document)
    {
        _document = document;
        _read = new Lazy<AcroFormReadResult>(() => AcroFormReader.Read(_document, _document.Diagnostics));
        _fields = new Lazy<FormFieldCollection>(() => new FormFieldCollection(_read.Value.Fields, _document.Objects, _document, _read.Value));
    }

    /// <summary>Returns the form facade for <paramref name="document"/>. Reading the field tree is deferred until <see cref="Fields"/> (or an operation that needs it) is first used.</summary>
    public static PdfForm For(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new PdfForm(document);
    }

    /// <summary>Whether this document has an <c>/AcroForm</c> dictionary with at least one field.</summary>
    public bool HasFields => Fields.Count > 0;

    /// <summary>This document's interactive form fields.</summary>
    public FormFieldCollection Fields => _fields.Value;

    /// <summary>
    /// Sets each named field's value (exact match, else a unique trailing-segment match) and
    /// records the interim-policy diagnostics this calls for (dropped <c>/XFA</c>,
    /// invalidated usage rights, advisory permission bits). Mutates the live object graph in
    /// place — call <c>document.Save(...)</c> afterward to persist it.
    /// </summary>
    /// <param name="values">Field name → value pairs. Names use <see cref="FormFieldCollection"/>'s lookup.</param>
    /// <exception cref="PlumePdfException">A name doesn't resolve (<c>PLUME6030</c>/<c>PLUME6031</c>), or a value is invalid for its field (<c>PLUME6032</c>-<c>PLUME6034</c>).</exception>
    /// <example>
    /// <code>
    /// form.Fill(new Dictionary&lt;string, string&gt; { ["c1_01"] = "1", ["f1_02"] = "Jane Q. Public" });
    /// </code>
    /// </example>
    public void Fill(IEnumerable<KeyValuePair<string, string>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        FormFiller.Fill(_document, _read.Value, values, _document.Options.NeedAppearances);
    }

    /// <summary>
    /// Produces a new, independent document with every widget's normal appearance stamped
    /// into its page's content and every field/widget/<c>/AcroForm</c> removed. Returns a new
    /// <see cref="PdfDocument"/> rather than mutating this one in place —
    /// the same "structurally-transformative operation returns a new document" shape
    /// <see cref="Pdf.Merge(PdfDocument[])"/>/<see cref="Pdf.Split"/> already use, and the only
    /// shape available without a general object-number allocator on the source document (that
    /// machinery belongs to a lower layer; flatten needs genuinely new content-stream objects, which an
    /// already-open document's <c>ObjectRegistry</c> has no way to add today).
    /// </summary>
    /// <remarks>
    /// A widget with no <c>/AP</c> gets its normal appearance <b>synthesized</b> from its field
    /// value and effective <c>/DA</c> ("generated or pre-existing") — the same
    /// <c>AppearanceGenerator</c> Fill and Rasterize use, run against a
    /// scratch registry so this document is never mutated. A widget with no <c>/AP</c> AND
    /// nothing synthesizable (an unsigned signature field, an off-state button, a widget the
    /// field tree doesn't own) is left as a live annotation with a <c>PLUME6036</c> Warning
    /// diagnostic; the rest of the document still flattens.
    /// </remarks>
    /// <example>
    /// <code>
    /// using var flattened = form.Flatten();
    /// flattened.Save("f1040-flattened.pdf");
    /// </code>
    /// </example>
    public PdfDocument Flatten() => FormFlattener.Flatten(_document, _read.Value);
}
