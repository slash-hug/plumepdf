namespace PlumePdf.Documents.Forms;

/// <summary>
/// Which ISO 32000-1 §12.7.3/§12.7.4 field type a <see cref="FieldValue"/> carries — the value
/// half of the <see cref="IAppearanceGenerator"/> seam.
/// </summary>
internal enum FormFieldKind
{
    /// <summary>A text field (<c>/FT /Tx</c>, §12.7.4.3).</summary>
    Text,

    /// <summary>A checkbox or radio-button field (<c>/FT /Btn</c>, without the pushbutton flag, §12.7.4.2).</summary>
    Button,

    /// <summary>A choice field — list box or combo box (<c>/FT /Ch</c>, §12.7.4.4).</summary>
    Choice,
}

/// <summary>
/// The fill-time value half of the <see cref="IAppearanceGenerator"/> seam: what
/// <c>FormFiller</c> has already resolved about one field before asking the appearance
/// generator to paint it — deliberately decoupled from the public <c>FormField</c> facade so
/// the two sides can be developed and tested independently.
/// </summary>
/// <param name="Kind">Which field type this value fills.</param>
/// <param name="Text">
/// The field's text content for <see cref="FormFieldKind.Text"/> fields and, as the visible
/// label, for a <see cref="FormFieldKind.Choice"/> field's selected export value(s);
/// <see langword="null"/> for <see cref="FormFieldKind.Button"/> fields.
/// </param>
/// <param name="OnStateName">
/// For <see cref="FormFieldKind.Button"/> fields, the selected on-state name — the widget's own
/// <c>/AP /N</c> sub-dictionary key (e.g. <c>/1</c>, never assumed to be <c>/Yes</c>)
/// — or <c>"Off"</c> when unchecked; <see langword="null"/> for other kinds.
/// </param>
internal sealed record FieldValue(FormFieldKind Kind, string? Text, string? OnStateName);
