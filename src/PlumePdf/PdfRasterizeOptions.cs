namespace PlumePdf;

/// <summary>
/// How image XObjects and inline images are resampled onto the device raster
/// (<see cref="PdfRasterizeOptions.ImageResampling"/>). The filter is chosen per image and per axis from the placement's device-space size:
/// "minifying" means one device pixel covers more than one source pixel along that axis,
/// "magnifying" means at most one. Minification always area-averages the source footprint
/// (the box filter's footprint-exact fix) unless <see cref="Point"/> is selected; the
/// modes differ mainly in what happens when magnifying. Every mode is integer/fixed-point
/// arithmetic — output is byte-identical run to run.
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("scan.pdf");
/// var crisp = PdfRasterizeOptions.Default with { Dpi = 200, ImageResampling = ImageResamplingMode.Point, AntiAlias = false };
/// var image = document.Pages[0].Rasterize(crisp);
/// </code>
/// </example>
public enum ImageResamplingMode
{
    /// <summary>
    /// PDFium parity — the default. Minifying: footprint box. Magnifying: 2-tap bilinear
    /// interpolation when PDFium's own integer test holds — <c>deviceHeight / 8 &lt; sourceWidth ×
    /// sourceHeight / deviceWidth</c>, roughly a device area under eight times the source area
    /// (below about 2.83× linear) — or when the image dictionary sets <c>/Interpolate true</c>
    /// (ISO 32000-1 Table 89); nearest-neighbour beyond that, and always at an exact 1:1
    /// placement — the rule PDFium's <c>CStretchEngine</c> applies. Because the decision depends
    /// on the placement's device size, changing <see cref="PdfRasterizeOptions.Dpi"/> or the
    /// pixel size can change which filter an image gets, not just its resolution; use
    /// <see cref="Box"/>, <see cref="Point"/> or <see cref="Bilinear"/> for size-independent
    /// behaviour.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Nearest-neighbour in every regime — edge-preserving, never blends two source samples;
    /// PDFium's <c>FPDF_RENDER_NO_SMOOTHIMAGE</c>. When minifying, whole source rows and columns
    /// are dropped rather than averaged, so a 1 px stroke minified 3× becomes broken dashes;
    /// prefer <see cref="Auto"/> or <see cref="Box"/> for bilevel scans unless hard edges matter
    /// more than stroke continuity. Pairs with <see cref="PdfRasterizeOptions.AntiAlias"/>
    /// <see langword="false"/> for a fully hard-edged render.
    /// </summary>
    Point,

    /// <summary>
    /// Area-average (footprint box) when minifying; nearest-neighbour when magnifying. Never
    /// interpolates. This is exactly the output PlumePDF produced before the resampling mode
    /// existed, so it is the migration choice for a caller whose pipeline pins
    /// rasterized bytes.
    /// </summary>
    Box,

    /// <summary>
    /// Footprint box when minifying; 2-tap linear interpolation at every magnification, with no
    /// 2.83× cut-off and regardless of <c>/Interpolate</c>. Smoothest magnified output; magnified
    /// 1-bit stencils get soft edges.
    /// </summary>
    Bilinear,
}

/// <summary>
/// Options controlling <c>Pdf.Rasterize</c>/<c>doc.Pages[i].Rasterize</c> (Phase 8) — the
/// render-intent counterpart to <see cref="PdfOptions"/>'s document-wide resource caps:
/// target size/DPI, page selection,
/// and background live here; <c>MaxRasterSurfaceBytes</c>/<c>MaxDisplayListObjects</c>/
/// <c>MaxShadingSamples</c> live on <see cref="PdfOptions"/> instead. Named
/// <c>PdfRasterizeOptions</c> (not "RasterizeOptions") to match this
/// repo's own <c>Pdf&lt;Verb&gt;Options</c> convention (<see cref="PdfRedactOptions"/>,
/// <see cref="PdfSignOptions"/>).
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("input.pdf");
/// var options = PdfRasterizeOptions.Default with { Dpi = 150 };
/// var image = document.Pages[0].Rasterize(options);
/// image.Frames[0].EncodePng("page-0.png");
/// </code>
/// </example>
public sealed record PdfRasterizeOptions
{
    /// <summary>
    /// The default rasterize options: 96 DPI target size, first page only (for the
    /// whole-document verb), an opaque white background, no annotation/print rendering.
    /// </summary>
    public static PdfRasterizeOptions Default { get; } = new() { Dpi = 96 };

    /// <summary>
    /// The target raster width in device pixels. Must be set together with
    /// <see cref="PixelHeight"/>, and never together with <see cref="Dpi"/> — exactly one of
    /// {<see cref="PixelWidth"/>+<see cref="PixelHeight"/>, <see cref="Dpi"/>} must be supplied
    /// (<see cref="Validate"/>). Unset by default; construct from
    /// <see cref="Default"/> (which sets <see cref="Dpi"/>) rather than a bare <c>new()</c>
    /// unless you are supplying a pixel size instead.
    /// </summary>
    public int? PixelWidth { get; init; }

    /// <summary>
    /// The target raster height in device pixels. See <see cref="PixelWidth"/> — the two are
    /// set together or not at all.
    /// </summary>
    public int? PixelHeight { get; init; }

    /// <summary>
    /// The target resolution in pixels per inch, applied to the page's own point size
    /// (ISO 32000-1 <c>/MediaBox</c>) to derive the raster's pixel dimensions. Mutually
    /// exclusive with {<see cref="PixelWidth"/>, <see cref="PixelHeight"/>} — see
    /// <see cref="PixelWidth"/> and <see cref="Validate"/>. <see cref="Default"/> sets this to
    /// 96, matching <c>LayoutEngine.MeasureImage</c>'s existing 72/96 DPI convention
    /// (<c>src/PlumePdf/Layout/LayoutEngine.cs</c>).
    /// </summary>
    public double? Dpi { get; init; }

    /// <summary>
    /// Which pages a whole-document <c>Pdf.Rasterize(path, options)</c> call renders, as
    /// zero-based page indices in the order they should appear as the result
    /// <see cref="RasterImage"/>'s frames (mirrors multi-page TIFF decode,
    /// which <see cref="RasterImage"/> already models). <see langword="null"/> (the default)
    /// renders the first page only — <c>Pdf.Rasterize</c> never renders the whole document
    /// unless this is set explicitly. Ignored by the already page-scoped
    /// <c>doc.Pages[i].Rasterize(options)</c> verb, which always returns exactly one
    /// single-frame <see cref="RasterImage"/> for that one page regardless of this property.
    /// </summary>
    public IReadOnlyList<int>? PageIndices { get; init; }

    /// <summary>
    /// The color painted behind page content before rendering, used when
    /// <see cref="TransparentBackground"/> is <see langword="false"/> (the default). Defaults
    /// to opaque white, matching how PDF viewers render a page with no explicit background.
    /// A root-namespace byte-channel type (rather than <c>PlumePdf.Elements.MarkColor</c>'s
    /// 0–1 device-RGB, the writer/layout-side vocabulary) so the eventual <c>PlumePdf.Raster</c>
    /// surface allocator — a layer below <c>PlumePdf.Elements</c> — can read it directly
    /// without a forbidden upward layer dependency.
    /// </summary>
    public RasterColor Background { get; init; } = RasterColor.White;

    /// <summary>
    /// When <see langword="true"/>, the raster surface starts fully transparent (alpha 0)
    /// instead of painted with <see cref="Background"/>, so page content composites over
    /// nothing — useful when the result will itself be composited onto something else (e.g.
    /// re-encoded as a PNG with alpha). Defaults to <see langword="false"/> (opaque
    /// <see cref="Background"/>).
    /// </summary>
    public bool TransparentBackground { get; init; }

    /// <summary>
    /// When <see langword="true"/>, walks the page's <c>/Annots</c> array (ISO 32000-1 §12.5)
    /// and paints every visible annotation's appearance — universally, driven by each
    /// annotation's <c>/AP</c> <c>/N</c> normal-appearance stream (<c>/AP</c> <c>/BBox</c>+
    /// <c>/Matrix</c> mapped onto <c>/Rect</c> per §12.5.5), never bespoke per-subtype drawing
    /// (Phase 9). <c>/Widget</c> annotations with a <c>/V</c> value and no <c>/AP</c> (and any
    /// document with <c>/NeedAppearances</c> set) additionally get render-time appearance
    /// synthesis — the same generator <c>Pdf.FillForm</c>/<c>FlattenForm</c> use, run in-memory
    /// against a scratch, discarded-on-return object registry so the opened document is never
    /// mutated — see <c>docs/architecture.md</c>'s "Threading &amp; mutation" for the read-only
    /// guarantee this makes, proven mechanically by
    /// <c>tests/PlumePdf.Tests/Raster/RasterReadOnlyInvariantTests.cs</c>.
    /// <c>Hidden</c>/<c>NoView</c>-flagged annotations are never painted; a non-widget annotation
    /// with no <c>/AP</c> is skipped with an Info diagnostic (<c>PLUME7732</c>) rather than
    /// approximated. Defaults to <see langword="false"/> (Phase 8's original no-op default, kept
    /// for backward compatibility — annotation rendering is opt-in).
    /// </summary>
    public bool RenderAnnotations { get; init; }

    /// <summary>
    /// When <see langword="true"/>, rasterizes for a print target rather than an on-screen view:
    /// annotations flagged <c>Print</c> are painted and <c>Hidden</c> ones are honored the same
    /// as under view intent, but a <c>NoView</c>-only flag (visible on screen, suppressed on
    /// print) is honored the opposite way — see the annotation flag matrix in
    /// <c>Raster.AnnotationFlagMatrix</c> for the full Hidden/NoView/Print truth table (Phase 9).
    /// Also selects the
    /// <c>/Print</c> usage-dictionary override when resolving optional-content (OCG) visibility
    /// from the document's default <c>/OCProperties</c> <c>/D</c> configuration — a layer marked
    /// print-only becomes visible, one marked screen-only is suppressed. Has no effect unless
    /// <see cref="RenderAnnotations"/> is also <see langword="true"/> (there is nothing for
    /// print-intent to change if annotations aren't being rendered at all). Defaults to
    /// <see langword="false"/> (view intent).
    /// </summary>
    public bool PrintIntent { get; init; }

    /// <summary>
    /// How image XObjects and inline images are resampled onto the raster — the per-call
    /// counterpart of a native engine's image-smoothing flags.
    /// Defaults to <see cref="ImageResamplingMode.Auto"/> (PDFium parity). Every value is
    /// validated by <see cref="Validate"/>.
    /// </summary>
    public ImageResamplingMode ImageResampling { get; init; } = ImageResamplingMode.Auto;

    /// <summary>
    /// Whether path fills, strokes and glyphs are anti-aliased (the default, <see langword="true"/>)
    /// or thresholded to hard 0/255 coverage at 50 % — PDFium's
    /// <c>FPDF_RENDER_NO_SMOOTHPATH | FPDF_RENDER_NO_SMOOTHTEXT</c> as it actually renders.
    /// Clip-region edges stay anti-aliased either way (PDFium leaves clip masks
    /// untouched under those flags), and image resampling (<see cref="ImageResampling"/> governs
    /// that) and shadings are never affected. Vector content painted INSIDE a luminosity soft-mask
    /// group is thresholded like any other fill — the mask's compositing is unchanged, but a mask
    /// drawn from paths gets hard edges. <see langword="false"/> together with
    /// <see cref="ImageResamplingMode.Point"/> gives hard-edged vector and image content; clip
    /// edges and shadings remain smooth.
    /// </summary>
    public bool AntiAlias { get; init; } = true;

    /// <summary>
    /// Validates the target-size fields and <see cref="ImageResampling"/> and throws
    /// <see cref="ArgumentException"/> if they are malformed — programmer error, not a document defect, so a plain BCL exception rather
    /// than a <see cref="PlumePdfException"/>/<c>PLUME####</c> code (AGENTS.md exception
    /// policy). Called by the <c>Rasterize</c> verbs before doing any work;
    /// exposed publicly so a caller can validate options up front.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Neither {<see cref="PixelWidth"/>+<see cref="PixelHeight"/>} nor <see cref="Dpi"/> is
    /// set, or both are set, or exactly one of <see cref="PixelWidth"/>/<see cref="PixelHeight"/>
    /// is set without the other, or a set numeric value is not positive, or
    /// <see cref="ImageResampling"/> is not a defined <see cref="ImageResamplingMode"/> value.
    /// </exception>
    /// <example>
    /// <code>
    /// var badOptions = new PdfRasterizeOptions { PixelWidth = 800, Dpi = 96 }; // both set
    /// badOptions.Validate(); // throws ArgumentException
    /// </code>
    /// </example>
    public void Validate()
    {
        if (PixelWidth.HasValue != PixelHeight.HasValue)
        {
            throw new ArgumentException(
                $"{nameof(PixelWidth)} and {nameof(PixelHeight)} must both be set or both be unset " +
                "— a target pixel size needs both dimensions.",
                PixelWidth.HasValue ? nameof(PixelHeight) : nameof(PixelWidth));
        }

        var hasPixelSize = PixelWidth.HasValue;
        var hasDpi = Dpi.HasValue;

        if (hasPixelSize == hasDpi)
        {
            throw new ArgumentException(
                hasPixelSize
                    ? $"Exactly one of {{{nameof(PixelWidth)}+{nameof(PixelHeight)}, {nameof(Dpi)}}} " +
                      $"may be set, not both — {nameof(PixelWidth)}/{nameof(PixelHeight)} and " +
                      $"{nameof(Dpi)} are alternative ways to size the same raster."
                    : $"Exactly one of {{{nameof(PixelWidth)}+{nameof(PixelHeight)}, {nameof(Dpi)}}} " +
                      $"must be set — construct from {nameof(PdfRasterizeOptions)}.{nameof(Default)} " +
                      $"(which sets {nameof(Dpi)}) rather than a bare constructor if you don't need " +
                      "a specific pixel size or DPI.",
                nameof(Dpi));
        }

        if (hasPixelSize && (PixelWidth!.Value <= 0 || PixelHeight!.Value <= 0))
        {
            throw new ArgumentException(
                $"{nameof(PixelWidth)} and {nameof(PixelHeight)} must both be positive.",
                nameof(PixelWidth));
        }

        if (hasDpi && Dpi!.Value <= 0)
        {
            throw new ArgumentException($"{nameof(Dpi)} must be positive.", nameof(Dpi));
        }

        // An explicit range check rather than Enum.IsDefined (reflection — banned in src/ by
        // ReflectionBanTests / the NativeAOT policy; the TiffReader precedent).
        if (ImageResampling is < ImageResamplingMode.Auto or > ImageResamplingMode.Bilinear)
        {
            throw new ArgumentException(
                $"{nameof(ImageResampling)} must be a defined {nameof(ImageResamplingMode)} value, not {(int)ImageResampling}.",
                nameof(ImageResampling));
        }
    }
}

/// <summary>
/// An opaque or translucent color as four byte channels, matching the eventual
/// <c>PlumePdf.Raster</c> BGRA surface's own per-pixel representation (Phase 8). Used by
/// <see cref="PdfRasterizeOptions.Background"/>.
/// Deliberately distinct from <see cref="Elements.MarkColor"/> (0–1 device-RGB, the
/// writer/layout-side vocabulary for composed-document watermarking/stamping) — the two types
/// serve different layers and are not meant to be interchangeable.
/// </summary>
public readonly record struct RasterColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Opaque white — <see cref="PdfRasterizeOptions.Background"/>'s default.</summary>
    public static readonly RasterColor White = new(255, 255, 255);

    /// <summary>Opaque black.</summary>
    public static readonly RasterColor Black = new(0, 0, 0);

    /// <summary>Fully transparent black (all channels 0) — distinct from setting <see cref="PdfRasterizeOptions.TransparentBackground"/>, which this type does not itself control.</summary>
    public static readonly RasterColor Transparent = new(0, 0, 0, 0);
}
