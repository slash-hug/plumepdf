namespace PlumePdf.Documents.Forms;

/// <summary>
/// The internal seam between field/widget fill orchestration (<c>FormFiller</c>/
/// <c>FormFlattener</c>, the caller) and appearance-stream generation
/// (<c>AppearanceGenerator</c>, the implementer), so both sides can be built and tested against
/// a fixed contract independently. Deliberately takes only types that
/// already exist ahead of this phase (<see cref="PdfDictionary"/>, <see cref="ObjectRegistry"/>,
/// <see cref="PdfOptions"/>, <see cref="DiagnosticCollection"/>) plus the
/// <see cref="FieldValue"/> type — never the not-yet-built public <c>FormField</c>/
/// <c>PdfForm</c> facade, so this file compiles standalone ahead of that facade landing.
/// </summary>
internal interface IAppearanceGenerator
{
    /// <summary>
    /// Generates a normal appearance stream for one widget annotation's current value and
    /// registers it as a new indirect object via <paramref name="registry"/>, returning the
    /// reference the caller writes into the widget's <c>/AP /N</c> entry (or, for a
    /// checkbox/radio button, into the on-state sub-dictionary the caller selects with
    /// <see cref="FieldValue.OnStateName"/>).
    /// </summary>
    /// <param name="value">The field's fill-time value and type, already resolved by the caller.</param>
    /// <param name="widget">
    /// The field/widget dictionary — the merged field+widget shape is the norm (ISO 32000-1
    /// §12.5.6.19; 136/136 on the f1040 exit-demo fixture) and is what this
    /// method reads <c>/Rect</c>, <c>/MK</c>, and any widget-level <c>/DA</c>/<c>/Q</c>
    /// override from.
    /// </param>
    /// <param name="defaultAppearance">
    /// The effective <c>/DA</c> string — the widget's own when it declares one, otherwise the
    /// AcroForm dictionary's inherited default.
    /// </param>
    /// <param name="resources">The AcroForm's <c>/DR</c> resource dictionary the <c>/DA</c> font name resolves against.</param>
    /// <param name="registry">The document's object registry — used to allocate the new appearance stream's object number and mark it dirty.</param>
    /// <param name="options">Active options; <see cref="PdfOptions.MaxGeneratedAppearanceBytes"/> caps the generated stream's size.</param>
    /// <param name="diagnostics">Where recoverable generation deviations (e.g. an unencodable character, falling back to a substitute glyph) are recorded.</param>
    /// <returns>The reference to the newly-registered appearance stream object.</returns>
    IndirectReference GenerateAppearance(
        FieldValue value,
        PdfDictionary widget,
        string defaultAppearance,
        PdfDictionary resources,
        ObjectRegistry registry,
        PdfOptions options,
        DiagnosticCollection? diagnostics);
}
