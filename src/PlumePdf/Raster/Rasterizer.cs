using PlumePdf.Content;
using PlumePdf.Raster.Annotations;
using PlumePdf.Raster.OptionalContent;

namespace PlumePdf.Raster;

/// <summary>
/// The internal raster entry point <c>Pdf.Rasterize</c>/<c>doc.Pages[i].Rasterize</c>
/// (wired via <c>Documents.PageRasterAdapter</c>) call into. Deliberately expressed in
/// terms of primitives this layer already owns (decoded content bytes, a resolved resource
/// dictionary, a page size, a target pixel size) rather than <c>PdfPage</c> or
/// <c>PdfRasterizeOptions</c> directly — <c>docs/architecture.md</c>'s layer discipline puts
/// <c>PlumePdf.Raster</c> below <c>PlumePdf.Documents</c>, so resolving <em>which</em> bytes
/// belong to a page (walking <c>/Contents</c>, applying inheritable page-tree attributes and
/// <c>/Rotate</c>) stays the caller's job; painting them is this method's. The verbs' own
/// <c>Documents.PageRasterAdapter</c> is the thin adapter: it resolves the page's
/// content/resources/effective MediaBox/Rotate, computes the target pixel size from
/// <c>PdfRasterizeOptions</c>, and calls this.
/// </summary>
internal static class Rasterizer
{
    /// <summary>
    /// Renders one page's content into a <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/>
    /// BGRA surface (cap-checked before allocating) and returns it as a
    /// <see cref="RasterImageFrame"/>. Every allocation this call makes — the surface itself, and
    /// every intermediate buffer the display-list build/paint passes need — is bounded before it
    /// happens; a document/caller-controlled dimension that would exceed a cap is refused with a
    /// <c>PLUME75xx</c> exception rather than attempted.
    /// </summary>
    /// <param name="contentBytes">The page's (or, recursively, a Form XObject's) already filter-decoded content-stream bytes. An empty span paints nothing — the surface is returned as its plain background fill.</param>
    /// <param name="resources">The page's effective <c>/Resources</c> dictionary (already resolved from any indirect reference), or <see langword="null"/> for a page with none.</param>
    /// <param name="mediaBoxWidth">The page's width in PDF user-space units (points). Must be positive for any content to render — a non-positive value paints only the background.</param>
    /// <param name="mediaBoxHeight">The page's height in PDF user-space units (points). See <paramref name="mediaBoxWidth"/>.</param>
    /// <param name="pixelWidth">The target surface width in pixels. Must be positive.</param>
    /// <param name="pixelHeight">The target surface height in pixels. Must be positive.</param>
    /// <param name="options">Resource limits and <see cref="PdfOptions.Strict"/>, threaded to <see cref="RasterInterpreter.BuildDisplayList"/>.</param>
    /// <param name="paintContext">The per-call paint intent (<see cref="PdfRasterizeOptions.ImageResampling"/>, <see cref="PdfRasterizeOptions.AntiAlias"/>) — required, never defaulted inside the Raster layer.</param>
    /// <param name="diagnostics">Recoverable deviations encountered while building the display list are appended here.</param>
    /// <param name="objects">Resolves indirect references reached through <paramref name="resources"/> (Form XObject <c>/Resources</c>, <c>/ExtGState</c> entries, ...), or <see langword="null"/> when none exist.</param>
    /// <param name="imageResolver">Decodes an <c>/Image</c> XObject to pixels — see <see cref="RasterInterpreter.ImageResolver"/>'s remarks for why this is injected. Omitting it means image XObjects are skipped (paths still render).</param>
    /// <param name="backgroundColor">The BGRA color the surface starts filled with before painting, or <see langword="null"/> for opaque white — matching every reader's default page background.</param>
    /// <param name="maxSurfaceBytes">The byte-size ceiling <see cref="RasterSurface.Create"/> enforces before allocating. Defaults to <paramref name="options"/>'s own <see cref="PdfOptions.MaxRasterSurfaceBytes"/> (<see langword="null"/> means "use the options value"); pass an explicit value only to override it for this one call.</param>
    /// <param name="pageToDeviceOverride">The exact page-user-space-to-device-pixel-space matrix to use in place of <see cref="PageToDeviceCtm"/>'s own uniform-scale computation, or <see langword="null"/> to use it. A caller whose page has a non-zero effective <c>/Rotate</c> (ISO 32000-1 §14.11.2) — this method itself has no notion of rotation — composes its own normalization matrix first and passes the result here; <see langword="null"/>'s plain <paramref name="mediaBoxWidth"/>/<paramref name="mediaBoxHeight"/>-based scale is only correct for an unrotated page.</param>
    /// <param name="annotationOptions">
    /// Phase 9: the page's <c>/Annots</c> array plus view/print intent and the
    /// widget-appearance-synthesis seam, painted after the content stream. <see langword="null"/>
    /// (the default) is the <c>RenderAnnotations</c>-off gate — no annotation pass runs at all,
    /// byte-identical to Phase 8 behavior. Passing a non-<see langword="null"/> value with a
    /// <see langword="null"/> <see cref="AnnotationRenderOptions.Annotations"/> runs the pass but
    /// paints nothing (an empty/no <c>/Annots</c> page).
    /// </param>
    /// <param name="optionalContent">
    /// Phase 9: resolves <c>BDC /OC … EMC</c> marked-content and annotation <c>/OC</c> visibility
    /// against the document's default optional-content configuration — see
    /// <see cref="RasterInterpreter.BuildDisplayList"/>'s own remarks. <see langword="null"/> (the
    /// default) never suppresses anything, matching a document with no <c>/OCProperties</c>.
    /// </param>
    /// <param name="printIntent">
    /// Phase 9: whether the page's own content renders under <b>print</b> intent (<c>true</c>) or
    /// <b>view</b> intent (<c>false</c>, the default) for optional-content <c>/Print</c>-usage
    /// resolution — conceptually independent of whether annotations render, so it is threaded here
    /// rather than read off <paramref name="annotationOptions"/> (which is <see langword="null"/>
    /// whenever <c>RenderAnnotations</c> is off). When <paramref name="annotationOptions"/> is
    /// supplied its own <see cref="AnnotationRenderOptions.PrintIntent"/> still wins, so the two
    /// never disagree in practice (<c>PageRasterAdapter</c> derives both from the same
    /// <c>PdfRasterizeOptions.PrintIntent</c>).
    /// </param>
    /// <exception cref="PlumePdfException"><c>PLUME7500</c> — the requested surface size exceeds <paramref name="maxSurfaceBytes"/>; <c>PLUME7501</c>/<c>PLUME7502</c> — a hostile content stream's <c>q</c>-nesting or Form XObject recursion exceeded its guard; <c>PLUME7503</c> — the display-list build produced more objects than <see cref="PdfOptions.MaxDisplayListObjects"/>; <c>PLUME7743</c> — the page's <c>/Annots</c> array exceeds <see cref="PdfOptions.MaxWidgetsPerPage"/>; or any exception <see cref="Content.ContentStreamReader.Read"/> can throw (e.g. <c>PLUME7010</c>, the operator-count guard).</exception>
    public static RasterImageFrame Rasterize(
        ReadOnlyMemory<byte> contentBytes,
        PdfDictionary? resources,
        double mediaBoxWidth,
        double mediaBoxHeight,
        int pixelWidth,
        int pixelHeight,
        PdfOptions options,
        RasterPaintContext paintContext,
        DiagnosticCollection? diagnostics = null,
        ObjectRegistry? objects = null,
        RasterInterpreter.ImageResolver? imageResolver = null,
        (byte B, byte G, byte R, byte A)? backgroundColor = null,
        long? maxSurfaceBytes = null,
        PdfMatrix? pageToDeviceOverride = null,
        AnnotationRenderOptions? annotationOptions = null,
        OptionalContentConfig? optionalContent = null,
        bool printIntent = false)
    {
        ArgumentNullException.ThrowIfNull(options);

        var surface = RasterSurface.Create(pixelWidth, pixelHeight, maxSurfaceBytes ?? options.MaxRasterSurfaceBytes);
        var background = backgroundColor ?? ((byte)255, (byte)255, (byte)255, (byte)255);
        surface.Clear(background.B, background.G, background.R, background.A);

        if (mediaBoxWidth > 0 && mediaBoxHeight > 0)
        {
            var pageToDevice = pageToDeviceOverride ?? PageToDeviceCtm(mediaBoxWidth, mediaBoxHeight, pixelWidth, pixelHeight);

            if (contentBytes.Length > 0)
            {
                // Page-content OCG /Print-usage honors print intent independently of annotation
                // rendering; annotationOptions.PrintIntent still wins when supplied so the two
                // agree (PageRasterAdapter derives both from PdfRasterizeOptions.PrintIntent), but
                // a RenderAnnotations-off call still resolves page-content layers under print intent.
                var pageContentPrintIntent = annotationOptions?.PrintIntent ?? printIntent;
                var displayList = RasterInterpreter.BuildDisplayList(contentBytes, resources, pageToDevice, options, diagnostics, objects, imageResolver, optionalContent: optionalContent, printIntent: pageContentPrintIntent);
                RasterInterpreter.Paint(surface, displayList, paintContext, options, objects, diagnostics);
            }

            // Annotations composite on top of the page's own content, after it has already
            // painted — the same order every reader uses. Gated on mediaBox validity (a real
            // page-to-device transform), not on contentBytes: a page can carry annotations with
            // an otherwise-empty content stream.
            if (annotationOptions is not null)
            {
                AnnotationReader.RenderAnnotations(surface, annotationOptions, pageToDevice, options, diagnostics, objects, imageResolver, optionalContent, paintContext);
            }
        }

        return surface.ToRasterImageFrame();
    }

    /// <summary>
    /// The page-user-space-to-device-pixel-space transform: PDF user space has its origin at the
    /// page's bottom-left with Y increasing upward (§8.3.2.2); a raster surface has its origin at
    /// the top-left with Y increasing downward (the universal image convention <see cref="RasterSurface"/>
    /// and every codec in this repo already uses). Uniformly scales the <paramref name="mediaBoxWidth"/> ×
    /// <paramref name="mediaBoxHeight"/> page rectangle to fill <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/> exactly (non-uniform scale when the aspect ratios differ — page-selection/fit-mode letterboxing is <c>PdfRasterizeOptions</c>' concern).
    /// </summary>
    internal static PdfMatrix PageToDeviceCtm(double mediaBoxWidth, double mediaBoxHeight, int pixelWidth, int pixelHeight)
    {
        var scaleX = pixelWidth / mediaBoxWidth;
        var scaleY = pixelHeight / mediaBoxHeight;
        return new PdfMatrix(scaleX, 0, 0, -scaleY, 0, pixelHeight);
    }
}
