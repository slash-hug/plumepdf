namespace PlumePdf.Documents;

/// <summary>
/// The <c>/Name</c> constants the forms subsystem reads and writes (ISO 32000-1 §12.7),
/// gathered in one place rather than re-declared per file — the same "each writer declares
/// the names it needs" convention <c>FullRewriteWriter</c> uses, just centralized here
/// because every file under <c>Documents/Forms</c> needs most of the same set. Every entry
/// with a matching well-known property on <see cref="PdfName"/> is a direct alias onto it
/// rather than a second <see cref="PdfName.Get(string)"/>
/// call — both resolve through the same intern table so they're the identical
/// instance either way, but aliasing keeps this from being a second, independently-maintained
/// name table and makes <see cref="PdfName"/>'s own forms constants actually referenced. Only
/// the handful of names <see cref="PdfName"/> doesn't carry a well-known property for
/// (<see cref="Rect"/>, <see cref="Matrix"/>, <see cref="BBox"/>, <see cref="Resources"/>,
/// <see cref="XObject"/>, <see cref="Contents"/>) still call <see cref="PdfName.Get(string)"/>
/// directly.
/// </summary>
internal static class AcroFormNames
{
    public static readonly PdfName AcroForm = PdfName.AcroForm;
    public static readonly PdfName Fields = PdfName.Fields;
    public static readonly PdfName Kids = PdfName.Kids;
    public static readonly PdfName T = PdfName.T;
    public static readonly PdfName FT = PdfName.FT;
    public static readonly PdfName Ff = PdfName.Ff;
    public static readonly PdfName V = PdfName.V;
    public static readonly PdfName Parent = PdfName.Parent;
    public static readonly PdfName Rect = PdfName.Get("Rect");
    public static readonly PdfName Subtype = PdfName.Subtype;
    public static readonly PdfName Widget = PdfName.Widget;
    public static readonly PdfName AP = PdfName.AP;
    public static readonly PdfName N = PdfName.N;
    public static readonly PdfName AS = PdfName.AS;
    public static readonly PdfName Opt = PdfName.Opt;
    public static readonly PdfName Annots = PdfName.Annots;
    public static readonly PdfName NeedAppearances = PdfName.NeedAppearances;
    public static readonly PdfName XFA = PdfName.XFA;
    public static readonly PdfName Perms = PdfName.Perms;
    public static readonly PdfName UR3 = PdfName.UR3;
    public static readonly PdfName Off = PdfName.Off;
    public static readonly PdfName SigFlags = PdfName.SigFlags;
    public static readonly PdfName DR = PdfName.DR;
    public static readonly PdfName DA = PdfName.DA;
    public static readonly PdfName Q = PdfName.Q;
    public static readonly PdfName CO = PdfName.CO;
    public static readonly PdfName Matrix = PdfName.Get("Matrix");
    public static readonly PdfName BBox = PdfName.Get("BBox");
    public static readonly PdfName Resources = PdfName.Get("Resources");
    public static readonly PdfName XObject = PdfName.Get("XObject");
    public static readonly PdfName Contents = PdfName.Get("Contents");
}

/// <summary>
/// The result of reading a document's interactive-form data (ISO 32000-1 §12.7.2): the
/// <c>/AcroForm</c> dictionary itself (when present and resolvable) plus every terminal
/// field discovered by walking <c>/Fields</c>/<c>/Kids</c> (<see cref="FieldTree"/>).
/// </summary>
internal sealed class AcroFormReadResult
{
    internal AcroFormReadResult(PdfDictionary? acroFormDictionary, IndirectReference? acroFormReference, IReadOnlyList<AcroFormField> fields)
    {
        AcroFormDictionary = acroFormDictionary;
        AcroFormReference = acroFormReference;
        Fields = fields;
    }

    /// <summary>The document's <c>/AcroForm</c> dictionary, or <see langword="null"/> when the document has none.</summary>
    public PdfDictionary? AcroFormDictionary { get; }

    /// <summary>
    /// <see cref="AcroFormDictionary"/>'s own indirect-object identity, or
    /// <see langword="null"/> when there is none (no <c>/AcroForm</c> at all, or — rare in
    /// practice — the catalog embeds it as a direct/inline dictionary rather than an indirect
    /// reference). A caller mutating <see cref="AcroFormDictionary"/> in place must
    /// <see cref="ObjectRegistry.MarkDirty"/> this reference for the edit to survive
    /// <c>SaveIncremental</c>.
    /// </summary>
    public IndirectReference? AcroFormReference { get; }

    /// <summary>Every terminal field discovered, in tree-walk (document) order.</summary>
    public IReadOnlyList<AcroFormField> Fields { get; }

    /// <summary>Whether the <c>/AcroForm</c> dictionary sets <c>/NeedAppearances true</c>.</summary>
    public bool NeedAppearances =>
        AcroFormDictionary is not null
        && AcroFormDictionary.TryGetValue(AcroFormNames.NeedAppearances, out var value)
        && value is PdfBoolean flag
        && flag.Value;
}

/// <summary>
/// Reads a document's interactive-form data: the <c>/AcroForm</c> dictionary hanging off the
/// catalog, and the field tree it roots (ISO 32000-1 §12.7.2-3). Lenient-with-diagnostics
/// (this is greenfield read-side work, nothing in
/// Phase 3's extraction reads <c>/AcroForm</c> or <c>/Annots</c>): a missing or malformed
/// <c>/AcroForm</c> is "no form fields" plus a recorded deviation, never a thrown exception,
/// except for the untrusted-input resource-limit guards (<see cref="FieldTree"/>'s depth/
/// count caps), which throw even under default options — the same posture
/// <c>MaxContentStreamOperators</c> and friends already use.
/// </summary>
internal static class AcroFormReader
{
    /// <summary>
    /// Reads <paramref name="document"/>'s <c>/AcroForm</c> dictionary and field tree.
    /// Deviations are recorded to <paramref name="diagnostics"/> (typically
    /// <c>document.Diagnostics</c>); a document with no <c>/AcroForm</c> at all returns an
    /// empty result rather than throwing.
    /// </summary>
    public static AcroFormReadResult Read(PdfDocument document, DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(document);

        var catalog = document.Catalog;
        if (catalog is null)
        {
            return new AcroFormReadResult(null, null, []);
        }

        if (!catalog.Dictionary.TryGetValue(AcroFormNames.AcroForm, out var acroFormValue))
        {
            return new AcroFormReadResult(null, null, []);
        }

        var acroFormReference = acroFormValue is PdfReference acroFormRef ? acroFormRef.Target : (IndirectReference?)null;
        var acroFormDictionary = Resolve(document.Objects, acroFormValue) as PdfDictionary;
        if (acroFormDictionary is null)
        {
            diagnostics?.Add(new PdfDiagnostic("PLUME6040", DiagnosticSeverity.Warning, "The catalog's /AcroForm entry did not resolve to a dictionary; treating the document as having no form fields."));
            return new AcroFormReadResult(null, null, []);
        }

        if (!acroFormDictionary.TryGetValue(AcroFormNames.Fields, out var fieldsValue) || Resolve(document.Objects, fieldsValue) is not PdfArray fieldsArray)
        {
            return new AcroFormReadResult(acroFormDictionary, acroFormReference, []);
        }

        var fields = FieldTree.Walk(document.Objects, fieldsArray, document.Options, diagnostics);
        return new AcroFormReadResult(acroFormDictionary, acroFormReference, fields);
    }

    /// <summary>Resolves <paramref name="value"/> one hop if it is an indirect reference, otherwise returns it unchanged.</summary>
    internal static PdfObject Resolve(ObjectRegistry objects, PdfObject value) => value is PdfReference reference ? objects[reference.Target] : value;
}
