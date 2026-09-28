using PlumePdf.Documents;

namespace PlumePdf;

/// <summary>The kind of interactive form field (ISO 32000-1 §12.7.4), surfaced on the public <see cref="FormField"/> facade.</summary>
public enum FormFieldType
{
    /// <summary>No recognizable field type was found; the field cannot be filled.</summary>
    Unknown,

    /// <summary>A single- or multi-line text field.</summary>
    Text,

    /// <summary>A single checkbox.</summary>
    CheckBox,

    /// <summary>One button of a radio group.</summary>
    Radio,

    /// <summary>A push button — carries no value.</summary>
    PushButton,

    /// <summary>A scrollable, non-editable list of choices.</summary>
    ListBox,

    /// <summary>A combo box, possibly editable.</summary>
    ComboBox,

    /// <summary>A signature field (Phase 5 owns signature semantics; this phase reads it but never sets a value on it).</summary>
    Signature,
}

/// <summary>
/// One interactive form field (ISO 32000-1 §12.7.3-4) — the public facade over the internal
/// field-tree model. Obtained from <see cref="FormFieldCollection"/>, never
/// constructed directly.
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("f1040.pdf");
/// var form = PdfForm.For(document);
/// FormField field = form.Fields["c1_01"]; // unique trailing-segment match
/// Console.WriteLine($"{field.FullName}: {field.FieldType}, allowed = [{string.Join(", ", field.AllowedValues)}]");
/// field.Value = "1";
/// </code>
/// </example>
public sealed class FormField
{
    private readonly AcroFormField _model;
    private readonly ObjectRegistry _objects;
    private readonly PdfDocument? _document;
    private readonly AcroFormReadResult? _form;

    internal FormField(AcroFormField model, ObjectRegistry objects, PdfDocument? document = null, AcroFormReadResult? form = null)
    {
        _model = model;
        _objects = objects;
        _document = document;
        _form = form;
    }

    /// <summary>This field's own name segment (the last component of <see cref="FullName"/>).</summary>
    public string Name => _model.ShortName;

    /// <summary>The field's full, dot-joined name (e.g. <c>topmostSubform[0].Page1[0].c1_01[0]</c>) — always resolves uniquely.</summary>
    public string FullName => _model.FullyQualifiedName;

    /// <summary>The field's kind.</summary>
    public FormFieldType FieldType => _model.Kind switch
    {
        AcroFieldKind.Text => FormFieldType.Text,
        AcroFieldKind.CheckBox => FormFieldType.CheckBox,
        AcroFieldKind.Radio => FormFieldType.Radio,
        AcroFieldKind.PushButton => FormFieldType.PushButton,
        AcroFieldKind.ListBox => FormFieldType.ListBox,
        AcroFieldKind.ComboBox => FormFieldType.ComboBox,
        AcroFieldKind.Signature => FormFieldType.Signature,
        _ => FormFieldType.Unknown,
    };

    /// <summary>
    /// The field's current value as text: the text itself for a text field, the selected
    /// on-state name (never assumed <c>/Yes</c>) for a checkbox/radio, or the selected
    /// option's export value for a choice field. <see langword="null"/> when unset.
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// On set: this field's <see cref="FieldType"/> cannot hold a value
    /// (<c>PLUME6032</c>), or the value is not one of <see cref="AllowedValues"/> where that
    /// applies (<c>PLUME6033</c>/<c>PLUME6034</c>).
    /// </exception>
    /// <example>
    /// <code>
    /// field.Value = "Jane Q. Public"; // text field
    /// checkboxField.Value = checkboxField.AllowedValues.First(v => v != "Off"); // checkbox on-state
    /// </code>
    /// </example>
    public string? Value
    {
        get => FieldValues.GetValue(_model);
        set
        {
            FieldValues.SetValue(_model, value, _objects);

            // Setting a value regenerates the widget appearance on THIS door
            // exactly as Pdf.FillForm/PdfForm.Fill does — a filled field with a stale
            // appearance was the review gap that motivated the shared path.
            if (_document is not null && _form is not null)
            {
                FormFiller.RegenerateAppearancesForModelDoor(_document, _form, _model);
            }
        }
    }

    /// <summary>
    /// Convenience accessor for a <see cref="FormFieldType.CheckBox"/> field: <see langword="true"/>
    /// when <see cref="Value"/> is one of this field's own discovered on-states
    /// (<see cref="AllowedValues"/>), <see langword="false"/> for <c>/Off</c>, unset, or a value
    /// that names no appearance state of this widget (a sibling's export value under a shared
    /// grouping node — the IRS W-9 shape). A field with no discovered on-state at all
    /// falls back to "any non-<c>/Off</c> value". Setting it writes the on-state or <c>/Off</c>.
    /// </summary>
    /// <exception cref="PlumePdfException">This field is not a <see cref="FormFieldType.CheckBox"/>, or (on set) it has no discovered on-state to write (<c>PLUME6033</c>).</exception>
    public bool Checked
    {
        get
        {
            RequireCheckBox();
            if (Value is not { } value || string.Equals(value, "Off", StringComparison.Ordinal))
            {
                return false;
            }

            // A checkbox is on only when /V names one of ITS OWN /AP /N appearance
            // states (ISO 32000-1 §12.7.4.2.3) — a viewer shows /Off for any other value. The
            // IRS W-9's "tax classification" boxes are independent checkbox fields under one
            // grouping node that all carry the group's selected export value, so comparing
            // against the literal "Off" reported every member checked. With no discovered
            // on-state at all (no /AP), the value is the only evidence and any non-Off wins.
            var onStates = AllowedValues;
            return onStates.Count == 0 || onStates.Contains(value, StringComparer.Ordinal);
        }

        set
        {
            RequireCheckBox();
            var onState = AllowedValues.FirstOrDefault(static v => !string.Equals(v, "Off", StringComparison.Ordinal))
                ?? throw new PlumePdfException("PLUME6033", $"Checkbox field '{FullName}' has no discovered on-state (/AP /N carries no non-Off key) — set Value to a known state name instead of using Checked.");
            Value = value ? onState : "Off";
        }
    }

    /// <summary>
    /// The field's allowed on-state (checkbox/radio) or option (choice) values, discovered
    /// from <c>/AP /N</c> sub-dictionary keys and <c>/Opt</c> entries respectively — never
    /// assumed to be <c>/Yes</c>. Empty for text/signature/push-button fields.
    /// </summary>
    public IReadOnlyList<string> AllowedValues => FieldValues.GetAllowedValues(_model);

    /// <inheritdoc/>
    public override string ToString() => $"{FullName} ({FieldType})";

    private void RequireCheckBox()
    {
        if (FieldType != FormFieldType.CheckBox)
        {
            throw new PlumePdfException("PLUME6032", $"Field '{FullName}' is a {FieldType} field, not a CheckBox — Checked does not apply. Use Value instead.");
        }
    }
}
