using System.Text;

namespace PlumePdf.Documents;

/// <summary>The kind of interactive form field, resolved from <c>/FT</c> plus the relevant <c>/Ff</c> bits (ISO 32000-1 §12.7.4).</summary>
internal enum AcroFieldKind
{
    /// <summary>No recognizable <c>/FT</c> was found (inherited or own); the field cannot be filled.</summary>
    Unknown,

    /// <summary><c>/FT /Tx</c> — a text field.</summary>
    Text,

    /// <summary><c>/FT /Btn</c> without the Radio or Pushbutton flag — a single checkbox.</summary>
    CheckBox,

    /// <summary><c>/FT /Btn</c> with the Radio flag (bit 16) set.</summary>
    Radio,

    /// <summary><c>/FT /Btn</c> with the Pushbutton flag (bit 17) set — carries no value.</summary>
    PushButton,

    /// <summary><c>/FT /Ch</c> without the Combo flag (bit 18) — a scrollable list box.</summary>
    ListBox,

    /// <summary><c>/FT /Ch</c> with the Combo flag (bit 18) set — a combo box, possibly editable.</summary>
    ComboBox,

    /// <summary><c>/FT /Sig</c> — a signature field. Read here (and surfaced via <c>doc.Signatures</c> once signed), but never assigned a plain string value — sign via <c>doc.Signatures.Add</c>/<c>SignAsync</c> or <c>Pdf.Sign</c>/<c>SignAsync</c> instead.</summary>
    Signature,
}

/// <summary>
/// One terminal node of the field tree (ISO 32000-1 §12.7.3): a field with no field-tree
/// children, carrying its own (possibly inherited) <c>/FT</c> and <c>/Ff</c>, and the widget
/// annotation(s) that render it. <see cref="Dictionary"/> is the merged field+widget
/// dictionary for the single-widget case (the norm — plan measurement: 136/136 on the f1040
/// probe), or the field-only dictionary when <see cref="Widgets"/> holds separate kid
/// widgets (e.g. a radio group's per-page buttons).
/// </summary>
internal sealed class AcroFormField
{
    internal AcroFormField(
        string fullyQualifiedName,
        string shortName,
        AcroFieldKind kind,
        PdfName? fieldTypeName,
        int flags,
        IndirectReference reference,
        PdfDictionary dictionary,
        IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> widgets)
    {
        FullyQualifiedName = fullyQualifiedName;
        ShortName = shortName;
        Kind = kind;
        FieldTypeName = fieldTypeName;
        Flags = flags;
        Reference = reference;
        Dictionary = dictionary;
        Widgets = widgets;
    }

    /// <summary>The field's full, dot-joined name (e.g. <c>topmostSubform[0].Page1[0].c1_01[0]</c>).</summary>
    public string FullyQualifiedName { get; }

    /// <summary>This field's own <c>/T</c> segment (the last component of <see cref="FullyQualifiedName"/>).</summary>
    public string ShortName { get; }

    /// <summary>The resolved field kind.</summary>
    public AcroFieldKind Kind { get; }

    /// <summary>The raw (own or inherited) <c>/FT</c> name, or <see langword="null"/> if none was found anywhere in the ancestor chain.</summary>
    public PdfName? FieldTypeName { get; }

    /// <summary>The resolved (own or inherited) <c>/Ff</c> field-flags value, or 0 if none was found.</summary>
    public int Flags { get; }

    /// <summary>This field's own indirect-object identity.</summary>
    public IndirectReference Reference { get; }

    /// <summary>The terminal node's own dictionary — <c>/V</c> is read from and written to here.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>
    /// The widget annotation(s) rendering this field: <c>[(Reference, Dictionary)]</c> where
    /// <c>Dictionary</c> is <see cref="Dictionary"/> itself for the merged single-widget case,
    /// or one entry per <c>/Kids</c> widget for a multi-widget field (e.g. a radio group).
    /// Empty for a field with no visual representation at all (rare, but not malformed).
    /// </summary>
    public IReadOnlyList<(IndirectReference Reference, PdfDictionary Dictionary)> Widgets { get; }
}

/// <summary>
/// Walks a document's <c>/Fields</c>/<c>/Kids</c> field tree into a flat list of terminal
/// fields. Cycle-safe and depth/count-capped: an explicit work queue (never
/// recursion — matching <c>PageImporter</c>/<c>FullRewriteWriter</c>'s "no unbounded call
/// stack on hostile input" convention) plus a visited-reference set catches a <c>/Kids</c>
/// graph that revisits a node, and two conservative caps (<see cref="PdfOptions.MaxFieldTreeDepth"/>,
/// <see cref="PdfOptions.MaxFormFields"/>) catch a merely very large or
/// very deep one.
/// </summary>
internal static class FieldTree
{
    private const int FlagRadio = 1 << 15; // bit 16
    private const int FlagPushbutton = 1 << 16; // bit 17
    private const int FlagCombo = 1 << 17; // bit 18 (Ch)

    private readonly record struct WorkItem(IndirectReference Reference, string? ParentFullyQualifiedName, PdfName? InheritedFieldType, int InheritedFlags, int Depth);

    /// <summary>Walks <paramref name="rootFields"/> (the <c>/AcroForm</c> dictionary's <c>/Fields</c> array) into terminal fields.</summary>
    public static IReadOnlyList<AcroFormField> Walk(ObjectRegistry objects, PdfArray rootFields, PdfOptions options, DiagnosticCollection? diagnostics)
    {
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(rootFields);
        ArgumentNullException.ThrowIfNull(options);

        var result = new List<AcroFormField>();
        var visited = new HashSet<int>();
        var queue = new Queue<WorkItem>();

        foreach (var entry in rootFields)
        {
            if (entry is PdfReference rootRef)
            {
                queue.Enqueue(new WorkItem(rootRef.Target, null, null, 0, 1));
            }
            else
            {
                Report(diagnostics, options, "A /Fields array entry was not an indirect reference; skipping it.");
            }
        }

        while (queue.Count > 0)
        {
            var item = queue.Dequeue();

            if (!visited.Add(item.Reference.Number))
            {
                Report(diagnostics, options, $"Field tree node {item.Reference} was reached more than once (a shared or cyclic /Kids graph); visiting it only the first time.");
                continue;
            }

            if (item.Depth > options.MaxFieldTreeDepth)
            {
                throw new PlumePdfException("PLUME6035", $"The field tree's depth exceeded the configured limit of {options.MaxFieldTreeDepth} at {item.Reference} — refusing to continue into a hostile or pathologically deep field hierarchy.");
            }

            if (AcroFormReader.Resolve(objects, new PdfReference(item.Reference)) is not PdfDictionary dict)
            {
                Report(diagnostics, options, $"Field tree node {item.Reference} did not resolve to a dictionary; skipping it.");
                continue;
            }

            var segment = ReadSegmentName(dict, options, diagnostics, item.Reference);
            var fullyQualifiedName = item.ParentFullyQualifiedName is null ? segment : $"{item.ParentFullyQualifiedName}.{segment}";

            var fieldType = dict.TryGetValue(AcroFormNames.FT, out var ftValue) && ftValue is PdfName ft ? ft : item.InheritedFieldType;
            var flags = dict.TryGetValue(AcroFormNames.Ff, out var ffValue) && ffValue is PdfNumber ffNum && ffNum.TryToInt32(out var ffInt) ? ffInt : item.InheritedFlags;

            var fieldKids = new List<IndirectReference>();
            var widgetKids = new List<(IndirectReference, PdfDictionary)>();

            if (dict.TryGetValue(AcroFormNames.Kids, out var kidsValue) && AcroFormReader.Resolve(objects, kidsValue) is PdfArray kidsArray)
            {
                foreach (var kidEntry in kidsArray)
                {
                    if (kidEntry is not PdfReference kidRef)
                    {
                        Report(diagnostics, options, $"A /Kids entry under {item.Reference} was not an indirect reference; skipping it.");
                        continue;
                    }

                    if (AcroFormReader.Resolve(objects, kidEntry) is not PdfDictionary kidDict)
                    {
                        Report(diagnostics, options, $"/Kids entry {kidRef.Target} under {item.Reference} did not resolve to a dictionary; skipping it.");
                        continue;
                    }

                    if (kidDict.ContainsKey(AcroFormNames.T))
                    {
                        fieldKids.Add(kidRef.Target);
                    }
                    else
                    {
                        widgetKids.Add((kidRef.Target, kidDict));
                    }
                }
            }

            if (fieldKids.Count > 0)
            {
                foreach (var kidRef in fieldKids)
                {
                    queue.Enqueue(new WorkItem(kidRef, fullyQualifiedName, fieldType, flags, item.Depth + 1));
                }

                continue;
            }

            // Terminal field: the merged field+widget dictionary is the primary case — a node
            // with its own /Rect is itself a widget.
            var widgets = new List<(IndirectReference, PdfDictionary)>();
            if (dict.ContainsKey(AcroFormNames.Rect))
            {
                widgets.Add((item.Reference, dict));
            }

            widgets.AddRange(widgetKids);

            if (result.Count >= options.MaxFormFields)
            {
                throw new PlumePdfException("PLUME6035", $"The field tree contains more than the configured limit of {options.MaxFormFields} fields — refusing to continue into a hostile or pathologically large form.");
            }

            result.Add(new AcroFormField(fullyQualifiedName, segment, Classify(fieldType, flags), fieldType, flags, item.Reference, dict, widgets));
        }

        return result;
    }

    /// <summary>Classifies a field kind from its (own or inherited) <c>/FT</c> and <c>/Ff</c>.</summary>
    public static AcroFieldKind Classify(PdfName? fieldType, int flags)
    {
        if (fieldType is null)
        {
            return AcroFieldKind.Unknown;
        }

        return fieldType.Value switch
        {
            "Tx" => AcroFieldKind.Text,
            "Sig" => AcroFieldKind.Signature,
            "Btn" => (flags & FlagPushbutton) != 0 ? AcroFieldKind.PushButton : (flags & FlagRadio) != 0 ? AcroFieldKind.Radio : AcroFieldKind.CheckBox,
            "Ch" => (flags & FlagCombo) != 0 ? AcroFieldKind.ComboBox : AcroFieldKind.ListBox,
            _ => AcroFieldKind.Unknown,
        };
    }

    private static string ReadSegmentName(PdfDictionary dict, PdfOptions options, DiagnosticCollection? diagnostics, IndirectReference reference)
    {
        if (dict.TryGetValue(AcroFormNames.T, out var tValue) && tValue is PdfString tString)
        {
            return tString.GetText();
        }

        Report(diagnostics, options, $"Field tree node {reference} has no /T (or it is not a string); synthesizing a fallback name so the field is still enumerable.");
        return $"Field{reference.Number}";
    }

    private static void Report(DiagnosticCollection? diagnostics, PdfOptions options, string message)
    {
        var code = "PLUME6040";
        if (options.Strict)
        {
            throw new PlumePdfException(code, message);
        }

        diagnostics?.Add(new PdfDiagnostic(code, DiagnosticSeverity.Warning, message));
    }
}

/// <summary>
/// Typed get/set/discovery operations over an <see cref="AcroFormField"/>'s <c>/V</c>/
/// <c>/AS</c>/<c>/Opt</c> data. Text values round-trip through UTF-16BE with
/// the §7.9.2.2 byte-order mark (matching <see cref="PdfString.GetText"/>'s own decode
/// convention); checkbox/radio/choice states are validated against
/// <see cref="GetAllowedValues"/> before being written — never accepted blind — per the
/// fail-fast coded-exception precedent (<c>PLUME8009</c>).
/// </summary>
internal static class FieldValues
{
    /// <summary>Reads the field's current value as text, or <see langword="null"/> when unset.</summary>
    public static string? GetValue(AcroFormField field)
    {
        if (!field.Dictionary.TryGetValue(AcroFormNames.V, out var value))
        {
            return null;
        }

        return value switch
        {
            PdfString s => s.GetText(),
            PdfName n => n.Value,
            PdfArray a when a.Count > 0 && a[0] is PdfString first => first.GetText(),
            _ => null,
        };
    }

    /// <summary>
    /// Sets the field's value, validating it against the field's kind and (for checkbox/
    /// radio/non-editable choice) its <see cref="GetAllowedValues"/>. Setting <c>/V</c> on a
    /// checkbox/radio also sets each widget's <c>/AS</c> to match (the selected widget gets
    /// the on-state name, every other widget in the same group gets <c>/Off</c>). Marks
    /// every mutated dictionary's own reference dirty on <paramref name="objects"/> (the
    /// field's, plus any widget whose <c>/AS</c> was touched) so <c>SaveIncremental</c>
    /// actually picks the edit up instead of silently dropping it — an in-place mutation with
    /// no matching <see cref="ObjectRegistry.MarkDirty"/> call is invisible to it by contract.
    /// </summary>
    /// <exception cref="PlumePdfException">
    /// <paramref name="field"/>'s kind cannot hold a value (<c>PLUME6032</c>), or
    /// <paramref name="value"/> is not one of the field's discovered on-states/choices
    /// (<c>PLUME6033</c>/<c>PLUME6034</c>).
    /// </exception>
    public static void SetValue(AcroFormField field, string? value, ObjectRegistry objects)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(objects);

        switch (field.Kind)
        {
            case AcroFieldKind.Text:
                SetTextValue(field, value ?? string.Empty, objects);
                return;

            case AcroFieldKind.CheckBox:
            case AcroFieldKind.Radio:
                SetButtonValue(field, value ?? "Off", objects);
                return;

            case AcroFieldKind.ComboBox:
            case AcroFieldKind.ListBox:
                SetChoiceValue(field, value ?? string.Empty, objects);
                return;

            default:
                throw new PlumePdfException("PLUME6032", $"Field '{field.FullyQualifiedName}' is a {field.Kind} field, which does not support setting a value (push buttons carry no state; a signature field's value is a signature dictionary, not a string — sign it via doc.Signatures.Add/SignAsync or Pdf.Sign/SignAsync instead).");
        }
    }

    /// <summary>
    /// The field's allowed on-state (checkbox/radio) or option (choice) values, discovered
    /// from <c>/AP /N</c> sub-dictionary keys for buttons and <c>/Opt</c> entries for choice
    /// fields — never assumed to be <c>/Yes</c> (real forms use arbitrary
    /// names like <c>/1</c>…<c>/5</c>). Empty for text/signature/push-button fields.
    /// </summary>
    public static IReadOnlyList<string> GetAllowedValues(AcroFormField field)
    {
        switch (field.Kind)
        {
            case AcroFieldKind.CheckBox:
            case AcroFieldKind.Radio:
                var states = new List<string>();
                foreach (var (_, widgetDict) in field.Widgets)
                {
                    if (widgetDict.TryGetValue(AcroFormNames.AP, out var apValue) && apValue is PdfDictionary apDict
                        && apDict.TryGetValue(AcroFormNames.N, out var nValue) && nValue is PdfDictionary stateDict)
                    {
                        foreach (var key in stateDict.Keys)
                        {
                            if (!states.Contains(key.Value, StringComparer.Ordinal))
                            {
                                states.Add(key.Value);
                            }
                        }
                    }
                }

                return states;

            case AcroFieldKind.ComboBox:
            case AcroFieldKind.ListBox:
                return ReadOptions(field.Dictionary);

            default:
                return [];
        }
    }

    private static List<string> ReadOptions(PdfDictionary dict)
    {
        var options = new List<string>();
        if (!dict.TryGetValue(AcroFormNames.Opt, out var optValue) || optValue is not PdfArray optArray)
        {
            return options;
        }

        foreach (var entry in optArray)
        {
            switch (entry)
            {
                case PdfString s:
                    options.Add(s.GetText());
                    break;

                case PdfArray pair when pair.Count > 0 && pair[0] is PdfString exportValue:
                    options.Add(exportValue.GetText());
                    break;
            }
        }

        return options;
    }

    private static void SetTextValue(AcroFormField field, string value, ObjectRegistry objects)
    {
        field.Dictionary.Set(AcroFormNames.V, EncodeUtf16BeString(value));
        objects.MarkDirty(field.Reference);
    }

    private static void SetButtonValue(AcroFormField field, string value, ObjectRegistry objects)
    {
        var allowed = GetAllowedValues(field);
        if (allowed.Count > 0 && !allowed.Contains(value, StringComparer.Ordinal) && !string.Equals(value, "Off", StringComparison.Ordinal))
        {
            throw new PlumePdfException("PLUME6033", $"'{value}' is not a recognized on-state for field '{field.FullyQualifiedName}'. Discovered states: {string.Join(", ", allowed)}, Off.");
        }

        var stateName = PdfName.Get(value);
        field.Dictionary.Set(AcroFormNames.V, stateName);
        objects.MarkDirty(field.Reference);

        foreach (var (widgetReference, widgetDict) in field.Widgets)
        {
            var hasState = widgetDict.TryGetValue(AcroFormNames.AP, out var apValue) && apValue is PdfDictionary apDict
                && apDict.TryGetValue(AcroFormNames.N, out var nValue) && nValue is PdfDictionary stateDict
                && stateDict.ContainsKey(stateName);

            widgetDict.Set(AcroFormNames.AS, hasState ? stateName : AcroFormNames.Off);
            objects.MarkDirty(widgetReference);
        }
    }

    private static void SetChoiceValue(AcroFormField field, string value, ObjectRegistry objects)
    {
        var allowed = GetAllowedValues(field);
        var isEditableCombo = field.Kind == AcroFieldKind.ComboBox && (field.Flags & (1 << 18)) != 0; // bit 19: Edit
        if (allowed.Count > 0 && !isEditableCombo && !allowed.Contains(value, StringComparer.Ordinal))
        {
            throw new PlumePdfException("PLUME6034", $"'{value}' is not one of field '{field.FullyQualifiedName}''s /Opt choices: {string.Join(", ", allowed)}.");
        }

        field.Dictionary.Set(AcroFormNames.V, EncodeUtf16BeString(value));
        objects.MarkDirty(field.Reference);
    }

    private static PdfString EncodeUtf16BeString(string text)
    {
        var encoded = Encoding.BigEndianUnicode.GetBytes(text);
        var withBom = new byte[encoded.Length + 2];
        withBom[0] = 0xFE;
        withBom[1] = 0xFF;
        encoded.CopyTo(withBom, 2);
        return PdfString.FromLiteral(withBom);
    }
}
