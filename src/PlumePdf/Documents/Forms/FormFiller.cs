using PlumePdf.Documents.Forms;

namespace PlumePdf.Documents;

/// <summary>
/// Orchestrates filling a document's form fields: sets each requested value
/// (<see cref="FieldValues"/>), requests appearance regeneration through
/// <see cref="IAppearanceGenerator"/> for fields whose visual state changed, and applies the
/// three interim-policy rulings every fill touches — <c>/XFA</c> dropped (with a diagnostic),
/// <c>/Perms /UR3</c> invalidated (with a diagnostic), and the advisory <c>FillFormFields</c>
/// permission bit (with a diagnostic). Mutates the live object graph in place; the caller saves
/// afterward (<c>PdfDocument.Save</c> today — a full rewrite already walks the live graph and
/// picks up these mutations correctly; <c>SaveIncremental</c> becomes safe for this once
/// a dirty-object-set/allocator redesign merges).
/// </summary>
internal static class FormFiller
{
    /// <summary>Fills <paramref name="values"/> into <paramref name="document"/>'s fields and applies the XFA/usage-rights/permission-bit diagnostics.</summary>
    /// <param name="document">The document to mutate; diagnostics are recorded onto <c>document.Diagnostics</c>.</param>
    /// <param name="form">The document's already-read AcroForm data (<see cref="AcroFormReader.Read"/>).</param>
    /// <param name="values">Field name → value pairs.</param>
    /// <param name="needAppearances">When <see langword="true"/> and any requested field's appearance could not be regenerated, sets <c>/AcroForm /NeedAppearances true</c> (the explicit fill-time escape hatch) instead of leaving a stale appearance in place.</param>
    /// <param name="appearanceGenerator">The appearance-regeneration seam to use. Defaults to the real <see cref="Forms.Appearances.AppearanceGenerator"/>.</param>
    public static void Fill(PdfDocument document, AcroFormReadResult form, IEnumerable<KeyValuePair<string, string>> values, bool needAppearances = false, Forms.IAppearanceGenerator? appearanceGenerator = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(values);

        var generator = appearanceGenerator ?? new Forms.Appearances.AppearanceGenerator(document.Objects);
        var index = new FieldNameIndex(form.Fields);
        var touched = new List<AcroFormField>();

        foreach (var (name, value) in values)
        {
            var field = index.Resolve(name);
            FieldValues.SetValue(field, value, document.Objects);
            touched.Add(field);
        }

        var acroFormDict = form.AcroFormDictionary;
        var resources = ResolveDictionary(document, acroFormDict is not null && acroFormDict.TryGetValue(AcroFormNames.DR, out var drValue) ? drValue : null) ?? new PdfDictionary();
        var formDefaultAppearance = ReadDaString(document, acroFormDict is not null && acroFormDict.TryGetValue(AcroFormNames.DA, out var formDa) ? formDa : null);

        var anyAppearanceMissing = false;
        foreach (var field in touched)
        {
            if (!RegenerateAppearances(document, field, generator, resources, formDefaultAppearance))
            {
                anyAppearanceMissing = true;
            }
        }

        if (anyAppearanceMissing)
        {
            if (needAppearances && form.AcroFormDictionary is not null)
            {
                form.AcroFormDictionary.Set(AcroFormNames.NeedAppearances, PdfBoolean.True);
                MarkAcroFormDirty(document, form);
            }
            else
            {
                document.Diagnostics.Add(new PdfDiagnostic(
                    "PLUME6041",
                    DiagnosticSeverity.Warning,
                    "One or more filled fields' appearance streams could not be regenerated (see the preceding per-field diagnostics) — a viewer that doesn't honor /NeedAppearances may show a stale or blank appearance for those fields. Set PdfOptions.NeedAppearances = true to write /AcroForm /NeedAppearances as the escape hatch."));
            }
        }

        ApplyXfaPolicy(document, form);
        ApplyUsageRightsDiagnostic(document);
        ApplyPermissionBitDiagnostic(document);
    }


    /// <summary>
    /// Regenerates every on-state widget appearance for <paramref name="field"/>'s current
    /// value through <paramref name="generator"/>. Shared by <see cref="Fill"/> and the
    /// public field-model door (<c>FormField.Value</c>'s setter) so both routes honor the same
    /// policy identically. Returns <see langword="false"/> when any widget's appearance could
    /// not be generated (a per-widget coded diagnostic is recorded).
    /// </summary>
    internal static bool RegenerateAppearances(PdfDocument document, AcroFormField field, Forms.IAppearanceGenerator generator, PdfDictionary resources, string? formDefaultAppearance)
    {
        var fieldValue = BuildFieldValue(field);
        if (fieldValue is null)
        {
            return true; // kinds with no paintable state
        }

        var allSucceeded = true;
        foreach (var (widgetReference, widgetDictionary) in field.Widgets)
        {
            if (fieldValue.Kind == FormFieldKind.Button && !IsWidgetOn(widgetDictionary))
            {
                continue; // an off-state widget keeps its existing (or absent) appearance
            }

            try
            {
                var effectiveDa = ReadDaString(document, widgetDictionary.TryGetValue(AcroFormNames.DA, out var widgetDa) ? widgetDa : null)
                    ?? ReadDaString(document, field.Dictionary.TryGetValue(AcroFormNames.DA, out var fieldDa) ? fieldDa : null)
                    ?? formDefaultAppearance
                    ?? string.Empty;

                var appearanceRef = generator.GenerateAppearance(fieldValue, widgetDictionary, effectiveDa, resources, document.Objects, document.Options, document.Diagnostics);
                WriteAppearance(widgetDictionary, fieldValue, appearanceRef);
                document.Objects.MarkDirty(widgetReference);
            }
            catch (PlumePdfException ex)
            {
                allSucceeded = false;
                document.Diagnostics.Add(new PdfDiagnostic(
                    ex.Code,
                    DiagnosticSeverity.Warning,
                    $"Appearance for field '{field.FullyQualifiedName}' was not regenerated: {ex.Message}"));
            }
        }

        return allSucceeded;
    }

    /// <summary>The model-door entry (<c>FormField.Value</c> setter): regenerates one field's appearances, honoring the document-level <see cref="PdfOptions.NeedAppearances"/> escape hatch on failure.</summary>
    internal static void RegenerateAppearancesForModelDoor(PdfDocument document, AcroFormReadResult form, AcroFormField field)
    {
        var acroFormDict = form.AcroFormDictionary;
        var resources = ResolveDictionary(document, acroFormDict is not null && acroFormDict.TryGetValue(AcroFormNames.DR, out var drValue) ? drValue : null) ?? new PdfDictionary();
        var formDefaultAppearance = ReadDaString(document, acroFormDict is not null && acroFormDict.TryGetValue(AcroFormNames.DA, out var formDa) ? formDa : null);

        if (!RegenerateAppearances(document, field, new Forms.Appearances.AppearanceGenerator(document.Objects), resources, formDefaultAppearance)
            && document.Options.NeedAppearances && acroFormDict is not null)
        {
            acroFormDict.Set(AcroFormNames.NeedAppearances, PdfBoolean.True);
            MarkAcroFormDirty(document, form);
        }
    }

    /// <summary>On fill, drop <c>/XFA</c> from the AcroForm dictionary (so XFA-aware viewers fall back to the just-filled AcroForm side) and record why.</summary>
    private static void ApplyXfaPolicy(PdfDocument document, AcroFormReadResult form)
    {
        if (form.AcroFormDictionary is { } acroForm && acroForm.Remove(AcroFormNames.XFA))
        {
            MarkAcroFormDirty(document, form);
            document.Diagnostics.Add(new PdfDiagnostic(
                "PLUME6037",
                DiagnosticSeverity.Warning,
                "This document's /AcroForm carried /XFA (an XFA-hybrid form). PlumePDF never reads or writes XFA content (permanently out of scope); filling only the AcroForm side and leaving /XFA in place would make an XFA-aware viewer render the unfilled XFA stream instead, so /XFA was dropped from the AcroForm dictionary — the standard remedy so viewers fall back to the filled AcroForm."));
        }
    }

    /// <summary>
    /// Marks the <c>/AcroForm</c> dictionary's own indirect object dirty after an in-place
    /// edit (<see cref="ApplyXfaPolicy"/>'s <c>/XFA</c> removal, or the <c>/NeedAppearances</c>
    /// escape hatch above) — the same "no <see cref="ObjectRegistry.MarkDirty"/>, no
    /// <c>SaveIncremental</c>" contract <see cref="FieldValues.SetValue"/> follows for field
    /// dictionaries. Falls back to marking the catalog dirty for the rare case of an inline
    /// (non-indirect) <c>/AcroForm</c>, since the edit then lives inside the catalog's own
    /// dictionary instead.
    /// </summary>
    private static void MarkAcroFormDirty(PdfDocument document, AcroFormReadResult form)
    {
        if (form.AcroFormReference is { } reference)
        {
            document.Objects.MarkDirty(reference);
        }
        else if (document.Catalog is { } catalog)
        {
            document.Objects.MarkDirty(catalog.Reference);
        }
    }

    /// <summary>
    /// This upgrades an earlier <c>/SigFlags</c>-presence heuristic now that
    /// Phase 5 owns real signature semantics: an update to a document carrying
    /// <c>/Perms /UR3</c> (Adobe usage rights) and/or an actual signature invalidates it —
    /// advisory-proceed stays the default, but the diagnostic now names the affected
    /// signature field(s) and, when one of them is a DocMDP certification, states its P-value
    /// and whether this operation is permitted under it. <paramref name="isFlatten"/> is
    /// <see langword="true"/> only for <c>Flatten</c>'s call (P=2's "form fill-in and signing
    /// only" still forbids flattening the form away entirely). <see cref="PdfOptions.Strict"/>
    /// refuses an operation that violates a certification's P-value; the advisory stance is
    /// otherwise unchanged. Shared by Fill and Flatten.
    /// </summary>
    internal static void ApplyUsageRightsDiagnostic(PdfDocument document, bool isFlatten = false)
    {
        var hasUsageRights = document.Catalog?.Dictionary.TryGetValue(AcroFormNames.Perms, out var permsValue) == true
            && AcroFormReader.Resolve(document.Objects, permsValue) is PdfDictionary permsDict
            && permsDict.ContainsKey(AcroFormNames.UR3);

        var signatures = Signing.SignatureDictionaryReader.ReadAll(document).Where(static s => !s.IsDocTimeStamp).ToList();
        if (!hasUsageRights && signatures.Count == 0)
        {
            return;
        }

        var (certifyingField, pValue) = FindDocMdpCertification(document.Objects, signatures);
        var violatesCertification = pValue switch
        {
            1 => true,
            2 => isFlatten,
            _ => false,
        };

        if (violatesCertification && document.Options.Strict)
        {
            throw new PlumePdfException("PLUME6038", $"'{certifyingField}' certifies this document at DocMDP P={pValue} ({DescribePValue(pValue!.Value)}), which this {(isFlatten ? "flatten" : "fill")} operation would violate — refused (PdfOptions.Strict).");
        }

        var parts = new List<string>();
        if (hasUsageRights)
        {
            parts.Add("This document carries /Perms /UR3 (Adobe usage rights).");
        }

        if (signatures.Count > 0)
        {
            parts.Add($"This document carries {signatures.Count} existing signature field(s): {string.Join(", ", signatures.Select(static s => s.FieldName))}.");
        }

        if (pValue is int certifiedP)
        {
            parts.Add($"'{certifyingField}' certifies this document at DocMDP P={certifiedP} ({DescribePValue(certifiedP)}) — this {(isFlatten ? "flatten" : "fill")} is {(violatesCertification ? "NOT permitted" : "permitted")} under it.");
        }

        parts.Add("A third-party update invalidates usage rights and any existing signature's validity regardless of PlumePDF's involvement; the operation proceeded anyway (advisory stance).");

        document.Diagnostics.Add(new PdfDiagnostic("PLUME6038", DiagnosticSeverity.Warning, string.Join(" ", parts)));
    }

    private static string DescribePValue(int pValue) => pValue switch
    {
        1 => "no changes allowed",
        2 => "form fill-in and signing only",
        3 => "form fill-in, signing, and annotations",
        _ => "an unrecognized permission level",
    };

    private static (string? FieldName, int? PValue) FindDocMdpCertification(ObjectRegistry objects, IReadOnlyList<Signing.SignatureDictionaryInfo> signatures)
    {
        var referenceName = PdfName.Reference;
        var transformMethodName = PdfName.TransformMethod;
        var transformParamsName = PdfName.TransformParams;
        var pName = PdfName.Get("P");

        foreach (var signature in signatures)
        {
            if (!signature.Dictionary.TryGetValue(referenceName, out var referenceValue) || AcroFormReader.Resolve(objects, referenceValue) is not PdfArray references)
            {
                continue;
            }

            // Every level here may legally be indirect (ISO 32000-1 §12.8.2.2) — a producer that
            // writes the sig-ref entries or /TransformParams as indirect objects must not make
            // the certification invisible, so each is resolved before the type test.
            foreach (var entry in references)
            {
                if (AcroFormReader.Resolve(objects, entry) is PdfDictionary sigRef
                    && sigRef.TryGetValue(transformMethodName, out var methodValue) && PdfName.DocMDP.Equals(AcroFormReader.Resolve(objects, methodValue))
                    && sigRef.TryGetValue(transformParamsName, out var transformParamsValue) && AcroFormReader.Resolve(objects, transformParamsValue) is PdfDictionary transformParams
                    && transformParams.TryGetValue(pName, out var pValueObject) && AcroFormReader.Resolve(objects, pValueObject) is PdfNumber { IsInteger: true } number
                    && number.TryToInt32(out var converted))
                {
                    return (signature.FieldName, converted);
                }
            }
        }

        return (null, null);
    }


    /// <summary>
    /// Maps a filled field to the seam's <see cref="FieldValue"/> (null for kinds with nothing to paint).
    /// Shared with <c>PageRasterAdapter</c>'s render-time widget-appearance-synthesis resolver
    /// so both routes to <see cref="Forms.Appearances.AppearanceGenerator"/> build the same <see cref="FieldValue"/>
    /// shape from a field's current state.
    /// </summary>
    internal static FieldValue? BuildFieldValue(AcroFormField field) => field.Kind switch
    {
        AcroFieldKind.Text => new FieldValue(FormFieldKind.Text, FieldValues.GetValue(field) ?? string.Empty, null),
        AcroFieldKind.CheckBox or AcroFieldKind.Radio => new FieldValue(FormFieldKind.Button, null, FieldValues.GetValue(field)),
        AcroFieldKind.ComboBox or AcroFieldKind.ListBox => new FieldValue(FormFieldKind.Choice, FieldValues.GetValue(field) ?? string.Empty, null),
        _ => null,
    };

    /// <summary>Whether this widget's <c>/AS</c> currently names an on-state (anything but <c>/Off</c> — <see cref="FieldValues.SetValue"/> already pointed it at the right state). Shared with <c>PageRasterAdapter</c>'s render-time resolver.</summary>
    internal static bool IsWidgetOn(PdfDictionary widget) =>
        widget.TryGetValue(AcroFormNames.AS, out var state) && state is PdfName name && !ReferenceEquals(name, AcroFormNames.Off);

    /// <summary>Writes the generated appearance into the widget's <c>/AP</c>: <c>/N</c> directly for variable text, or the <c>/N</c> state sub-dictionary entry named by the widget's <c>/AS</c> for a button.</summary>
    private static void WriteAppearance(PdfDictionary widget, FieldValue value, IndirectReference appearance)
    {
        if (widget.TryGetValue(AcroFormNames.AP, out var existing) && existing is not PdfDictionary)
        {
            existing = null; // an indirect or malformed /AP is replaced wholesale on the widget itself
        }

        var ap = existing as PdfDictionary ?? new PdfDictionary();
        if (value.Kind == FormFieldKind.Button)
        {
            var stateName = widget.TryGetValue(AcroFormNames.AS, out var asValue) && asValue is PdfName state
                ? state
                : PdfName.Get(value.OnStateName ?? "Yes");
            var states = ap.TryGetValue(AcroFormNames.N, out var nValue) && nValue is PdfDictionary stateDict
                ? stateDict
                : new PdfDictionary();
            states.Set(stateName, new PdfReference(appearance));
            ap.Set(AcroFormNames.N, states);
        }
        else
        {
            ap.Set(AcroFormNames.N, new PdfReference(appearance));
        }

        widget.Set(AcroFormNames.AP, ap);
    }

    /// <summary>Shared with <c>PageRasterAdapter</c>'s render-time resolver so both routes resolve <c>/DR</c>/<c>/DA</c> identically.</summary>
    internal static PdfDictionary? ResolveDictionary(PdfDocument document, PdfObject? value) =>
        value is null ? null : AcroFormReader.Resolve(document.Objects, value) as PdfDictionary;

    /// <summary>Shared with <c>PageRasterAdapter</c>'s render-time resolver so both routes resolve <c>/DR</c>/<c>/DA</c> identically.</summary>
    internal static string? ReadDaString(PdfDocument document, PdfObject? value) =>
        value is not null && AcroFormReader.Resolve(document.Objects, value) is PdfString da ? da.GetText() : null;

    /// <summary>The encrypted-source <c>/P</c> permission bits are advisory only — never enforced, always noted. Shared by Fill and Flatten.</summary>
    internal static void ApplyPermissionBitDiagnostic(PdfDocument document)
    {
        if (!document.Permissions.HasFlag(PdfPermissions.FillFormFields))
        {
            document.Diagnostics.Add(new PdfDiagnostic(
                "PLUME6039",
                DiagnosticSeverity.Info,
                "This document's /Encrypt dictionary clears the 'fill form fields' permission bit (bit 9); PlumePDF treats /P as advisory metadata and does not refuse filling on it — see PdfDocument.Permissions."));
        }
    }
}
