using PlumePdf.Content;
using PlumePdf.Documents.Forms;
using PlumePdf.Documents.Forms.Appearances;
using PlumePdf.Raster;
using PlumePdf.Raster.Annotations;
using PlumePdf.Raster.OptionalContent;

namespace PlumePdf.Documents;

/// <summary>
/// The thin <c>PlumePdf.Documents</c>-layer adapter <c>Pdf.Rasterize</c>/<c>doc.Pages[i].Rasterize</c>
/// call into — the "verb" half of the split <see cref="Rasterizer"/>'s
/// own remarks describe: this type resolves <em>which</em> bytes belong to a page (a page's
/// effective <c>/Contents</c>, <c>/Resources</c>, <c>/MediaBox</c>, and <c>/Rotate</c> — the same
/// <see cref="PageSpace"/>/<see cref="TextExtractor.ReadContentBytes"/> machinery
/// <see cref="TextExtractor"/>/<see cref="ImageExtractor"/> already use, so a rasterized page's
/// geometry agrees with what extraction reports for the same page) and computes the target pixel
/// size from <see cref="PdfRasterizeOptions"/>; <see cref="Rasterizer.Rasterize"/> does the actual
/// painting, one layer down. Phase 9 additionally does the "which annotations/optional
/// content" resolution here: the page's own <c>/Annots</c> array, the document's <c>/OCProperties</c>
/// default configuration, and — the one production wiring point for the render-time
/// widget-appearance-synthesis seam — an <see cref="WidgetAppearanceSynthesizer.Resolver"/>
/// backed by a fresh <see cref="ScratchObjectRegistry"/> per call, reusing
/// <c>Forms.FormFiller</c>'s own field-value/effective-<c>/DA</c> resolution so both routes to
/// <see cref="AppearanceGenerator"/> agree.
/// </summary>
/// <remarks>
/// Text paints (glyph outlines resolved through <see cref="RasterInterpreter"/> and its
/// <c>RenderFontFactory</c>, embedded or substitute), alongside vector paths and one
/// representative color per axial/radial shading. <c>/Image</c> XObjects paint too
/// (closing a gap this remarks section used to
/// describe as "still-pending"): every call builds a fresh
/// <see cref="ImageXObjectResolver.BuildResolver"/>-backed <see cref="RasterInterpreter.ImageResolver"/>
/// and injects it below, mirroring <see cref="BuildWidgetAppearanceResolver"/>'s own shape —
/// decode/colorspace/<c>/Decode</c>/<c>/SMask</c>/<c>/Mask</c> resolution all live in
/// <see cref="ImageXObjectResolver"/>, one layer down, not here. An image that fails to decode
/// (an unsupported colorspace, a corrupt payload, a malformed JPEG 2000 codestream — JPEG 2000
/// decodes in-house by default) still
/// degrades to "not painted" plus a coded diagnostic rather than throwing or aborting the page —
/// the same lenient-by-default philosophy as the rest of the read/render path. This adapter's job
/// is only to make the pipeline reachable through the public API.
/// </remarks>
internal static class PageRasterAdapter
{
    private static readonly PdfName ResourcesName = PdfName.Get("Resources");
    private static readonly PdfName OcPropertiesName = PdfName.Get("OCProperties");

    /// <summary>
    /// Renders one page. <paramref name="rasterizeOptions"/> is validated (<see cref="PdfRasterizeOptions.Validate"/>)
    /// before any work starts; <see cref="PdfRasterizeOptions.PageIndices"/> is meaningless here
    /// (this method always renders exactly the one <paramref name="page"/> it was given) — that
    /// property is <c>Pdf.Rasterize(string,PdfRasterizeOptions?)</c>'s own concern.
    /// </summary>
    /// <param name="page">The page to render.</param>
    /// <param name="objects">Resolves indirect references reached while rendering.</param>
    /// <param name="options">Resource limits and <see cref="PdfOptions.Strict"/>, threaded to <see cref="Rasterizer.Rasterize"/>.</param>
    /// <param name="rasterizeOptions">Target size/DPI, background, annotation/print-intent knobs, and page-selection knobs (page selection ignored here).</param>
    /// <param name="diagnostics">Recoverable deviations encountered while rendering are appended here.</param>
    /// <param name="owner">
    /// The document <paramref name="page"/> was read from, or <see langword="null"/> for a
    /// page with no owning document (matches <c>PdfPage</c>'s own optional-owner shape —
    /// <c>TextExtractor.Extract</c>'s structure-tree parameter uses the same pattern). Needed
    /// only for the Phase 9 surface: <c>/OCProperties</c> lives on the catalog, and widget
    /// appearance synthesis needs <c>/AcroForm</c>'s field tree/<c>/DR</c>/<c>/DA</c> and
    /// <c>NeedAppearances</c> — both out of reach from <paramref name="page"/>/<paramref name="objects"/>
    /// alone. A <see langword="null"/> owner still renders the page and its <c>/AP</c>-driven
    /// annotations; it just can't resolve optional content or synthesize widget appearances.
    /// </param>
    public static RasterImageFrame RasterizePageFrame(PdfPage page, ObjectRegistry objects, PdfOptions options, PdfRasterizeOptions rasterizeOptions, DiagnosticCollection diagnostics, PdfDocument? owner = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rasterizeOptions);
        ArgumentNullException.ThrowIfNull(diagnostics);
        rasterizeOptions.Validate();

        var mediaBox = PageSpace.GetMediaBox(page.Dictionary);
        var rotate = PageSpace.GetRotation(page.Dictionary);
        var (displayWidth, displayHeight) = PageSpace.GetDisplaySize(mediaBox, rotate);
        var (pixelWidth, pixelHeight) = ComputePixelSize(displayWidth, displayHeight, rasterizeOptions);

        var contentBytes = TextExtractor.ReadContentBytes(page.Dictionary, objects, options, diagnostics);
        var resources = PageSpace.ResolveDictionary(page.Dictionary.TryGetValue(ResourcesName, out var resourcesValue) ? resourcesValue : null, objects);

        // Normalizes /Rotate into the same page-space PageSpace already defines for extraction,
        // then scales that normalized (y-up, bottom-left origin, displayWidth x displayHeight)
        // rectangle onto the device (y-down, top-left origin) pixel grid — Rasterizer.Rasterize
        // itself has no notion of rotation (its remarks document this), so this composed matrix
        // is threaded in as pageToDeviceOverride rather than letting it derive one from the raw,
        // pre-rotation MediaBox.
        var normalization = PageSpace.NormalizationMatrix(mediaBox, rotate);
        var deviceScale = Rasterizer.PageToDeviceCtm(displayWidth, displayHeight, pixelWidth, pixelHeight);
        var pageToDevice = PdfMatrix.Multiply(normalization, deviceScale);

        var background = rasterizeOptions.TransparentBackground
            ? ((byte)0, (byte)0, (byte)0, (byte)0)
            : (rasterizeOptions.Background.B, rasterizeOptions.Background.G, rasterizeOptions.Background.R, rasterizeOptions.Background.A);

        var ocPropertiesValue = owner?.Catalog?.Dictionary.TryGetValue(OcPropertiesName, out var ocValue) == true ? ocValue : null;
        var optionalContent = OptionalContentConfig.Parse(ocPropertiesValue, objects, diagnostics);

        var annotationOptions = rasterizeOptions.RenderAnnotations
            ? BuildAnnotationRenderOptions(page, objects, options, rasterizeOptions, diagnostics, owner)
            : null;

        var imageResolver = ImageXObjectResolver.BuildResolver(objects, options, diagnostics);

        return Rasterizer.Rasterize(
            contentBytes,
            resources,
            mediaBox.Urx - mediaBox.Llx,
            mediaBox.Ury - mediaBox.Lly,
            pixelWidth,
            pixelHeight,
            options,
            new RasterPaintContext(rasterizeOptions.ImageResampling, rasterizeOptions.AntiAlias),
            diagnostics,
            objects,
            imageResolver,
            backgroundColor: background,
            maxSurfaceBytes: null,
            pageToDeviceOverride: pageToDevice,
            annotationOptions: annotationOptions,
            optionalContent: optionalContent,
            printIntent: rasterizeOptions.PrintIntent);
    }

    /// <summary>Assembles the Phase 9 annotation-pass inputs: the page's own <c>/Annots</c> array, view/print intent, and — when <paramref name="owner"/> is available — the AcroForm-backed widget-appearance-synthesis resolver (<see cref="BuildWidgetAppearanceResolver"/>).</summary>
    private static AnnotationRenderOptions BuildAnnotationRenderOptions(PdfPage page, ObjectRegistry objects, PdfOptions options, PdfRasterizeOptions rasterizeOptions, DiagnosticCollection diagnostics, PdfDocument? owner)
    {
        var annotations = page.Dictionary.TryGetValue(PdfName.Annots, out var annotsValue) && Resolve(annotsValue, objects) is PdfArray annots
            ? annots
            : null;

        if (owner is null)
        {
            return new AnnotationRenderOptions(annotations, rasterizeOptions.PrintIntent, NeedAppearances: false, WidgetAppearanceResolver: null);
        }

        var form = AcroFormReader.Read(owner, diagnostics);
        var resolver = BuildWidgetAppearanceResolver(owner, form, objects, options);
        return new AnnotationRenderOptions(annotations, rasterizeOptions.PrintIntent, form.NeedAppearances, resolver);
    }

    /// <summary>
    /// The production <see cref="WidgetAppearanceSynthesizer.Resolver"/> (the single
    /// wiring point): indexes <paramref name="form"/>'s field tree by widget-dictionary identity
    /// (every <c>/Annots</c> entry the annotation walk hands the resolver was itself resolved
    /// through <paramref name="objects"/>, the same cache <see cref="AcroFormReader.Read"/> read
    /// the field tree's widgets through, so reference equality finds the owning field), then
    /// reuses <c>Forms.FormFiller</c>'s exact field-value/effective-<c>/DA</c> logic
    /// (<see cref="FormFiller.BuildFieldValue"/>/<see cref="FormFiller.IsWidgetOn"/>/
    /// <see cref="FormFiller.ReadDaString"/>) so the render-time-only path never drifts from the
    /// real fill path — the only difference is every allocation goes through a fresh
    /// <see cref="ScratchObjectRegistry"/> instead of <paramref name="objects"/>
    /// itself, so nothing durable is ever written.
    /// </summary>
    private static WidgetAppearanceSynthesizer.Resolver? BuildWidgetAppearanceResolver(PdfDocument owner, AcroFormReadResult form, ObjectRegistry objects, PdfOptions options)
    {
        if (form.Fields.Count == 0)
        {
            return null;
        }

        var widgetToField = new Dictionary<PdfDictionary, AcroFormField>(ReferenceEqualityComparer.Instance);
        foreach (var field in form.Fields)
        {
            foreach (var (_, widgetDictionary) in field.Widgets)
            {
                widgetToField[widgetDictionary] = field;
            }
        }

        if (widgetToField.Count == 0)
        {
            return null;
        }

        var acroFormDictionary = form.AcroFormDictionary;
        var resources = FormFiller.ResolveDictionary(owner, acroFormDictionary is not null && acroFormDictionary.TryGetValue(AcroFormNames.DR, out var drValue) ? drValue : null) ?? new PdfDictionary();
        var formDefaultAppearance = FormFiller.ReadDaString(owner, acroFormDictionary is not null && acroFormDictionary.TryGetValue(AcroFormNames.DA, out var formDa) ? formDa : null);

        return (widget, resolverDiagnostics) =>
        {
            if (!widgetToField.TryGetValue(widget, out var field))
            {
                return null; // Not a widget this document's own field tree recognizes — nothing to synthesize from.
            }

            var fieldValue = FormFiller.BuildFieldValue(field);
            if (fieldValue is null || (fieldValue.Kind == FormFieldKind.Button && !FormFiller.IsWidgetOn(widget)))
            {
                return null; // An off-state button widget, or a field kind with no paintable synthesized state.
            }

            var effectiveDa = FormFiller.ReadDaString(owner, widget.TryGetValue(AcroFormNames.DA, out var widgetDa) ? widgetDa : null)
                ?? FormFiller.ReadDaString(owner, field.Dictionary.TryGetValue(AcroFormNames.DA, out var fieldDa) ? fieldDa : null)
                ?? formDefaultAppearance
                ?? string.Empty;

            // The linchpin decision: a fresh scratch registry per widget, seeded
            // past every object number the real document is known to use, its writes discarded
            // the moment this delegate returns — the real document.Objects is never touched.
            var scratch = ScratchObjectRegistry.CreateFor(objects, objects.NextFreshObjectNumber);
            var generator = new AppearanceGenerator(scratch);
            try
            {
                var reference = generator.GenerateAppearance(fieldValue, widget, effectiveDa, resources, scratch, options, resolverDiagnostics);
                return scratch[reference] as PdfStream;
            }
            catch (PlumePdfException)
            {
                return null; // Degrades to "not painted" — the same lenient posture an unresolved ImageResolver uses.
            }
        };
    }

    private static PdfObject? Resolve(PdfObject? value, ObjectRegistry objects) =>
        value is PdfReference reference ? objects[reference.Target] : value;

    /// <summary>Derives the target raster size from either <see cref="PdfRasterizeOptions.PixelWidth"/>/<see cref="PdfRasterizeOptions.PixelHeight"/> directly, or <see cref="PdfRasterizeOptions.Dpi"/> applied to the page's own <paramref name="displayWidth"/>/<paramref name="displayHeight"/> (points, §7.7.3.3's 1/72-inch unit) — never below 1x1, since a fractional-point page at a low DPI would otherwise round to a zero-size (and thus refused) surface.</summary>
    private static (int Width, int Height) ComputePixelSize(double displayWidth, double displayHeight, PdfRasterizeOptions options)
    {
        if (options.PixelWidth is { } pixelWidth && options.PixelHeight is { } pixelHeight)
        {
            return (pixelWidth, pixelHeight);
        }

        var dpi = options.Dpi!.Value;
        var width = Math.Max(1, (int)Math.Round(displayWidth / 72.0 * dpi));
        var height = Math.Max(1, (int)Math.Round(displayHeight / 72.0 * dpi));
        return (width, height);
    }
}
