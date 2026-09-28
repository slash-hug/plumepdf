using PlumePdf.Content;
using PlumePdf.Objects;
using PlumePdf.Raster.OptionalContent;

namespace PlumePdf.Raster.Annotations;

/// <summary>
/// The universal <c>/Annots</c> walk (ISO 32000-1 §12.5.6): unlike
/// <c>Documents.Forms.WidgetAnnotationReader</c> (widgets only, for form fill/flatten), this reads
/// every annotation subtype so <c>Rasterizer.Rasterize</c>'s post-content-stream annotation pass
/// can paint each visible one's <c>/AP</c> normal appearance — the engine is
/// <c>/AP</c>-driven only (no bespoke per-subtype drawing, tracked for 1.x); a non-widget annotation with
/// no usable <c>/AP</c> skips with a <c>PLUME7732</c> diagnostic, while a widget with none falls
/// through to <see cref="WidgetAppearanceSynthesizer"/>.
/// </summary>
internal static class AnnotationReader
{
    private static readonly PdfName SubtypeName = PdfName.Subtype;
    private static readonly PdfName ApName = PdfName.AP;
    private static readonly PdfName NName = PdfName.N;
    private static readonly PdfName AsName = PdfName.AS;
    private static readonly PdfName OcName = PdfName.Get("OC");

    /// <summary>
    /// Renders <paramref name="annotationOptions"/>.<see cref="AnnotationRenderOptions.Annotations"/>
    /// onto <paramref name="surface"/>, in document order, after the page's own content stream has
    /// already been painted (annotations composite on top, matching every reader).
    /// </summary>
    public static void RenderAnnotations(
        RasterSurface surface,
        AnnotationRenderOptions annotationOptions,
        PdfMatrix pageToDevice,
        PdfOptions options,
        DiagnosticCollection? diagnostics,
        ObjectRegistry? objects,
        RasterInterpreter.ImageResolver? imageResolver,
        OptionalContentConfig? optionalContent,
        RasterPaintContext paintContext)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(annotationOptions);
        ArgumentNullException.ThrowIfNull(options);

        if (annotationOptions.Annotations is not { } annots)
        {
            return;
        }

        var processed = 0;
        foreach (var entry in annots)
        {
            if (processed >= options.MaxWidgetsPerPage)
            {
                // Reuses the same per-page /Annots-array-size cap Documents.Forms.WidgetAnnotationReader
                // (PLUME6035) applies to widgets only — a hostile/pathological /Annots array is the
                // same DoS shape regardless of subtype, so the guard extends to every annotation
                // (a resource-limit cap, thrown unconditionally like PLUME7500-7503, not a
                // per-annotation content degradation). Its own code (PLUME7743), distinct from the
                // recoverable per-annotation PLUME7731 that AnnotationAppearance records — a fatal
                // page-wide refusal and a skip-one-and-continue diagnostic must never share a code.
                throw new PlumePdfException("PLUME7743", $"Page's /Annots array contains more than the configured limit of {options.MaxWidgetsPerPage} annotations (PdfOptions.MaxWidgetsPerPage); refusing to continue into a hostile or pathologically large page.");
            }

            processed++;

            if (Resolve(entry, objects) is not PdfDictionary annotation)
            {
                continue; // Malformed /Annots entry — lenient skip, nothing to paint.
            }

            var flags = AnnotationFlagMatrix.ReadFlags(annotation);
            if (!AnnotationFlagMatrix.ShouldRender(flags, annotationOptions.PrintIntent))
            {
                continue; // Hidden/NoView honored — correct author intent, never a diagnostic.
            }

            if (optionalContent is not null
                && annotation.TryGetValue(OcName, out var ocValue)
                && !optionalContent.IsVisible(ocValue, objects, annotationOptions.PrintIntent))
            {
                if (optionalContent.TryClaimSuppressionNotice())
                {
                    diagnostics?.Add(new PdfDiagnostic("PLUME7733", DiagnosticSeverity.Info, "An annotation's /OC optional-content membership resolved to OFF for the current view/print configuration; its content was suppressed."));
                }

                continue;
            }

            var isWidget = annotation.TryGetValue(SubtypeName, out var subtype) && subtype is PdfName subtypeName && subtypeName.Value == "Widget";

            var appearanceStream = ResolveNormalAppearance(annotation, objects, out var authoredBlank);

            // ISO 32000-1 §12.7.3.3: NeedAppearances asks a consumer to regenerate EVERY widget's
            // appearance, not only ones lacking an /AP — so under it we offer even an
            // already-appearanced widget to the synthesizer and prefer the freshly-synthesized
            // result. If synthesis declines (an unhandled field kind, off-state button, missing
            // font — the resolver returns null/false), we keep the stored /AP: a widget the
            // generator can't reproduce still paints its existing appearance rather than vanishing,
            // and a document without NeedAppearances keeps the byte-identical /AP-first behavior.
            // An authored blank (the selected appearance state has no entry — an unchecked box
            // whose /N carries only its on-state) is a real appearance, not a missing one: it is
            // offered to synthesis only under NeedAppearances. Only widgets have an
            // off-state; a non-widget whose state resolves to nothing still records PLUME7732.
            if (isWidget && ((appearanceStream is null && !authoredBlank) || annotationOptions.NeedAppearances))
            {
                if (WidgetAppearanceSynthesizer.TryResolveAppearance(annotation, annotationOptions.NeedAppearances, annotationOptions.WidgetAppearanceResolver, diagnostics, out var synthesized))
                {
                    appearanceStream = synthesized;
                }
            }

            if (appearanceStream is null)
            {
                if (!isWidget)
                {
                    var subtypeText = subtype is PdfName sn ? sn.Value : "?";
                    diagnostics?.Add(new PdfDiagnostic("PLUME7732", DiagnosticSeverity.Info, $"A /{subtypeText} annotation has no usable /AP normal appearance; skipped (non-widget annotations are /AP-driven only, no bespoke per-subtype drawing)."));
                }

                continue; // A widget with no /AP and no synthesis available degrades silently (same posture as an unresolved ImageResolver).
            }

            if (!AnnotationAppearance.TryResolve(annotation, appearanceStream, objects, options, diagnostics, out var placement))
            {
                continue; // PLUME7731 already recorded inside TryResolve.
            }

            var annotationCtm = PdfMatrix.Multiply(placement.Ctm, pageToDevice);
            var displayList = RasterInterpreter.BuildDisplayList(placement.ContentBytes, placement.Resources, annotationCtm, options, diagnostics, objects, imageResolver, optionalContent: optionalContent, printIntent: annotationOptions.PrintIntent);
            RasterInterpreter.Paint(surface, displayList, paintContext, options, objects, diagnostics);
        }
    }

    // Resolves /AP /N (ISO 32000-1 §12.5.5): either a direct form-XObject stream, or an
    // appearance-state sub-dictionary selected by /AS. When the selected state has no entry the
    // appearance is an authored blank (authoredBlank = true, null returned) —
    // the everyday unchecked checkbox whose /N holds only its on-state — never some other entry
    // (the first-entry lenience applied to a PRESENT-but-unmatched /AS previously painted every
    // checkbox checked). A missing /AS is malformed for a states dictionary; the selection then
    // follows PDFium (CPDF_Annot GetAnnotAPInternal): the field value /V (own, else the parent's)
    // when it names a present state, otherwise /Off — never the first entry.
    private static PdfStream? ResolveNormalAppearance(PdfDictionary annotation, ObjectRegistry? objects, out bool authoredBlank)
    {
        authoredBlank = false;
        if (Resolve(annotation, ApName, objects) is not PdfDictionary ap
            || !ap.TryGetValue(NName, out var nValue))
        {
            return null;
        }

        var resolved = Resolve(nValue, objects);
        if (resolved is PdfStream direct)
        {
            return direct;
        }

        if (resolved is not PdfDictionary states)
        {
            return null;
        }

        // /AS present as a name selects the state. A /AS that is null or not a name is treated as
        // absent (ISO 32000-1 §7.3.9 for null; PDFium reads /AS as a byte string and routes an
        // empty one to the same /V-then-/Off recovery).
        var state = Resolve(annotation, AsName, objects) is PdfName asName && asName.Value.Length > 0
            ? asName
            : ReadFieldValueName(annotation, objects) is { } valueName && states.ContainsKey(valueName) ? valueName : PdfName.Off;

        var present = states.TryGetValue(state, out var byState);
        if (present && Resolve(byState, objects) is PdfStream stream)
        {
            return stream;
        }

        // Only a state with NO entry is an authored blank. A present entry that is not a stream
        // (a dangling reference, a mistyped object) is a malformed /AP — the caller keeps its
        // PLUME7732 / synthesis handling for that. Either way a non-widget has no off-state, so
        // the caller treats its blank as a defect.
        authoredBlank = !present;
        return null;
    }

    // The widget's /V as an appearance-state name — its own, else its immediate /Parent's (a kid
    // widget of a check-box/radio field carries the value on the field node). One level only, a
    // deliberate PDFium match (CPDF_Annot GetAnnotAPInternal reads just "Parent"), not the full
    // §12.7.3.2 inheritance walk — this path is already the malformed no-/AS lane. A /V written
    // as a string (a producer bug that travels with the missing /AS) is accepted like PDFium
    // does; an empty name/string means no value.
    private static PdfName? ReadFieldValueName(PdfDictionary annotation, ObjectRegistry? objects)
    {
        return AsStateName(Resolve(annotation, PdfName.V, objects))
            ?? (Resolve(annotation, PdfName.Parent, objects) is PdfDictionary parent ? AsStateName(Resolve(parent, PdfName.V, objects)) : null);

        static PdfName? AsStateName(PdfObject? value) => value switch
        {
            PdfName { Value.Length: > 0 } name => name,
            PdfString text when text.GetText() is { Length: > 0 } t => PdfName.Get(t),
            _ => null,
        };
    }

    private static PdfObject? Resolve(PdfDictionary dict, PdfName key, ObjectRegistry? objects) =>
        dict.TryGetValue(key, out var value) ? Resolve(value, objects) : null;

    private static PdfObject? Resolve(PdfObject? value, ObjectRegistry? objects) =>
        value is PdfReference reference && objects is not null ? objects[reference.Target] : value;
}

/// <summary>
/// Bundles one <c>Rasterizer.Rasterize</c> call's annotation-rendering inputs (the
/// page's own <c>/Annots</c> array, view/print intent, <c>NeedAppearances</c>, and the
/// Documents-layer-injected <see cref="WidgetAppearanceSynthesizer.Resolver"/> seam) so
/// <c>Rasterizer.Rasterize</c>'s already-long parameter list doesn't grow further. A
/// <see langword="null"/> <see cref="Annotations"/> — or passing no <see cref="AnnotationRenderOptions"/>
/// at all to <c>Rasterizer.Rasterize</c> — is the <c>RenderAnnotations</c>-off gate: the caller
/// (<c>PdfRasterizeOptions.RenderAnnotations</c>'s eventual consumer) constructs
/// one only when annotation rendering was requested.
/// </summary>
/// <param name="Annotations">The page's own <c>/Annots</c> array (unresolved entries are fine — each is resolved during the walk), or <see langword="null"/> for a page with none.</param>
/// <param name="PrintIntent">Whether to render for print (honors <c>/Print</c>, ignores <c>/NoView</c>) rather than view (the reverse) — <see cref="PdfRasterizeOptions.PrintIntent"/>.</param>
/// <param name="NeedAppearances">The AcroForm's own <c>NeedAppearances</c> flag — when set, every widget (not only ones with a <c>/V</c> and no <c>/AP</c>) is offered to <see cref="WidgetAppearanceResolver"/>.</param>
/// <param name="WidgetAppearanceResolver">Synthesizes a <c>/V</c>-no-<c>/AP</c> (or <see cref="NeedAppearances"/>) widget's appearance — see <see cref="WidgetAppearanceSynthesizer"/>'s remarks for why this is injected. <see langword="null"/> degrades every such widget to "not painted".</param>
internal sealed record AnnotationRenderOptions(
    PdfArray? Annotations,
    bool PrintIntent,
    bool NeedAppearances,
    WidgetAppearanceSynthesizer.Resolver? WidgetAppearanceResolver);
