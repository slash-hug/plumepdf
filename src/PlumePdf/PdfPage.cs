using PlumePdf.Documents;

namespace PlumePdf;

/// <summary>
/// One leaf of a document's page tree (CONTEXT.md "Page"; ISO 32000-1 §7.7.3.3) — carries
/// its own content streams, resources, and boxes via <see cref="Dictionary"/>, the same
/// "escape hatch, all the way down" philosophy as <c>doc.Objects</c>. Owned by a
/// <see cref="PageCollection"/>; reorder and removal go through the collection
/// (<see cref="PageCollection.Move"/>, <see cref="PageCollection.RemoveAt"/>), not through
/// the page itself. <see cref="ExtractText"/>/<see cref="ExtractImages"/>/<see cref="Rasterize"/>
/// are safe to call concurrently from multiple threads, including concurrently on different
/// pages of the same document (each call builds its own local extraction/render state — no
/// shared mutable cache is touched, matching <c>docs/architecture.md</c>'s "Threading &amp;
/// mutation" contract).
/// </summary>
/// <example>
/// <code>
/// using var document = PdfDocument.Open("input.pdf");
/// PdfPage firstPage = document.Pages[0];
/// Console.WriteLine(firstPage.Reference);
/// </code>
/// </example>
public sealed class PdfPage
{
    private readonly PdfDocument? _owner;

    internal PdfPage(IndirectReference reference, PdfDictionary dictionary, PdfDocument? owner = null)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        Reference = reference;
        Dictionary = dictionary;
        _owner = owner;
    }

    /// <summary>The page's own indirect-object identity in the document it was read from.</summary>
    public IndirectReference Reference { get; }

    /// <summary>
    /// The page's effective dictionary: its own entries, plus <c>/Resources</c>,
    /// <c>/MediaBox</c>, <c>/CropBox</c>, and <c>/Rotate</c> resolved onto it from the page
    /// tree when the page doesn't set them itself (ISO 32000-1 §7.7.3.4's inheritable
    /// attributes) — what both save paths and <c>Pdf.Merge</c>/<c>Split</c> serialize.
    /// <c>doc.Objects[Reference]</c> returns the page's untouched original dictionary instead.
    /// </summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>
    /// Extracts this page's positioned text — letters, assembled words and lines, and a
    /// reading-order <see cref="ExtractedText.Text"/> string (the <c>Extract*</c>-is-
    /// expensive naming). Reading order is structure-tree-driven for a tagged document:
    /// when the document carries a parseable <c>/StructTreeRoot</c>, lines
    /// and words order by the tag tree's own document order (resolved via each
    /// <see cref="Letter.Mcid"/>); otherwise the geometric heuristic applies and an
    /// informational <c>PLUME6070</c> entry in <see cref="ExtractedText.Diagnostics"/> records
    /// the fallback. Coordinates are in page space (points, bottom-left origin, y-up,
    /// post-<c>/Rotate</c> normalization). Malformed content streams and undecodable fonts
    /// never throw under default options — those deviations are recorded to
    /// <see cref="ExtractedText.Diagnostics"/> instead (the read-path recovery-ladder
    /// philosophy); pass <see cref="PdfOptions.Strict"/> when opening the
    /// document to turn them into thrown exceptions. Resource-limit guards are the exception
    /// and DO throw even under default options (a page exceeding
    /// <c>MaxContentStreamOperators</c>, <c>MaxLettersPerPage</c>,
    /// <c>MaxXObjectNestingDepth</c>, or the graphics-state nesting bound throws a coded
    /// <c>PLUME6020</c>/<c>PLUME6021</c>/<c>PLUME6028</c>/<c>PLUME7010</c>/<c>PLUME7013</c> —
    /// refusing a hostile document is not a recoverable deviation).
    /// </summary>
    /// <param name="options">Options controlling this extraction call's resource limits. Defaults to <see cref="PdfTextExtractionOptions.Default"/>.</param>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("input.pdf");
    /// ExtractedText text = document.Pages[0].ExtractText();
    /// Console.WriteLine(text.Text);
    /// </code>
    /// </example>
    public ExtractedText ExtractText(PdfTextExtractionOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_owner is { } owner && owner.IsDisposed, this);
        var result = TextExtractor.Extract(this, RequireObjects(), RequireOptions(), options, _owner, ResolveOwnPageIndex());

        if (_owner is not null && !_owner.Permissions.HasFlag(PdfPermissions.ExtractContent))
        {
            result.Diagnostics.Add(new PdfDiagnostic("PLUME6024", DiagnosticSeverity.Info, "This document's /Encrypt dictionary clears the 'extract content' permission bit (bit 5); PlumePDF treats /P as advisory metadata and does not refuse extraction on it — see PdfDocument.Permissions."));
        }

        SummarizeIntoDocumentDiagnostics(result.Diagnostics, "text extraction");
        return result;
    }

    /// <summary>
    /// Extracts every image XObject reachable from this page's <c>/Resources</c> (recursing
    /// into Form XObjects). See <see cref="ExtractedImage"/>'s remarks for exactly how deep
    /// "extract" goes (filter-decoded samples or intact JPEG bytes, never a
    /// colorspace-resolved pixel buffer). This overload discards this call's diagnostics
    /// (unsupported filters, skipped inline images) — call
    /// <see cref="ExtractImagesWithDiagnostics"/> to get them.
    /// </summary>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("input.pdf");
    /// foreach (var image in document.Pages[0].ExtractImages())
    /// {
    ///     File.WriteAllBytes($"image-{image.Reference?.Number}.bin", image.Data.ToArray());
    /// }
    /// </code>
    /// </example>
    public IReadOnlyList<ExtractedImage> ExtractImages() => ExtractImagesWithDiagnostics().Images;

    /// <summary>The same result as <see cref="ExtractImages"/>, paired with this call's diagnostics (result-scoped, not <c>doc.Diagnostics</c>).</summary>
    public (IReadOnlyList<ExtractedImage> Images, DiagnosticCollection Diagnostics) ExtractImagesWithDiagnostics()
    {
        ObjectDisposedException.ThrowIf(_owner is { } owner && owner.IsDisposed, this);
        var (images, diagnostics) = ImageExtractor.Extract(this, RequireObjects(), RequireOptions());
        SummarizeIntoDocumentDiagnostics(diagnostics, "image extraction");
        return (images, diagnostics);
    }

    /// <summary>
    /// Renders this page to pixels (Phase 8's rasterizer — the verb
    /// <c>Raster.Rasterizer</c>'s own remarks describe as its intended caller). Always renders
    /// exactly this one page regardless of <paramref name="options"/>'s
    /// <see cref="PdfRasterizeOptions.PageIndices"/> (that property is
    /// <see cref="Pdf.Rasterize(string,PdfRasterizeOptions?)"/>'s own, whole-document concern) —
    /// the returned <see cref="RasterImage"/> always carries exactly one frame,
    /// <c>Frames[0]</c>. Vector paths render; text glyphs and <c>/Image</c> XObjects do not yet
    /// (no glyph rasterizer or image-to-RGB decode path is wired into the paint pass yet — see
    /// <c>Documents.PageRasterAdapter</c>'s remarks) — a page that uses them still renders
    /// something recognizable (its vector content, plus a flat representative color per
    /// axial/radial shading) rather than a blank surface. Resource-limit guards
    /// (<see cref="PdfOptions.MaxRasterSurfaceBytes"/>/<see cref="PdfOptions.MaxDisplayListObjects"/>/
    /// <see cref="PdfOptions.MaxShadingSamples"/>) throw a coded <c>PLUME75xx</c> exception
    /// rather than silently degrading — a hostile or pathological page is refused, not
    /// truncated.
    /// </summary>
    /// <param name="options">Target size/DPI, background, and page-selection knobs (page selection ignored here). Defaults to <see cref="PdfRasterizeOptions.Default"/> (96 DPI, opaque white background).</param>
    /// <exception cref="ArgumentException"><paramref name="options"/>'s target-size fields are malformed — see <see cref="PdfRasterizeOptions.Validate"/>.</exception>
    /// <exception cref="PlumePdfException">A resource-limit guard was exceeded — see this member's remarks; or any exception <see cref="Documents.PageRasterAdapter.RasterizePageFrame"/>'s underlying content-stream read can throw (e.g. <c>PLUME7010</c>).</exception>
    /// <example>
    /// <code>
    /// using var document = PdfDocument.Open("input.pdf");
    /// var options = PdfRasterizeOptions.Default with { Dpi = 150 };
    /// var image = document.Pages[0].Rasterize(options);
    /// image.Frames[0].EncodePng("page-0.png");
    /// </code>
    /// </example>
    public RasterImage Rasterize(PdfRasterizeOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_owner is { } owner && owner.IsDisposed, this);
        var effective = options ?? PdfRasterizeOptions.Default;
        var diagnostics = new DiagnosticCollection();
        var frame = PageRasterAdapter.RasterizePageFrame(this, RequireObjects(), RequireOptions(), effective, diagnostics, _owner);
        var result = RasterImage.FromFrames([frame], diagnostics);
        SummarizeIntoDocumentDiagnostics(diagnostics, "rasterization");
        return result;
    }

    /// <inheritdoc/>
    public override string ToString() => $"Page {Reference}";

    // doc.Diagnostics is the open/parse-time surface, and extraction results
    // carry their own result-scoped Diagnostics instead of growing doc.Diagnostics per
    // deviation — but doc.Diagnostics still gets exactly one first-occurrence summary entry
    // per page per extraction call, so "check doc.Diagnostics once, you've seen everything"
    // stays true for a caller who never looks at a per-call result's own Diagnostics.
    private void SummarizeIntoDocumentDiagnostics(DiagnosticCollection callDiagnostics, string operationName)
    {
        if (_owner is null)
        {
            return;
        }

        PdfDiagnostic? first = null;
        var count = 0;
        var worst = DiagnosticSeverity.Info;

        foreach (var diagnostic in callDiagnostics)
        {
            first ??= diagnostic;
            count++;
            if (diagnostic.Severity > worst)
            {
                worst = diagnostic.Severity;
            }
        }

        if (first is null)
        {
            return;
        }

        _owner.Diagnostics.Add(new PdfDiagnostic(
            first.Code,
            worst,
            $"Page {Reference} {operationName} recorded {count} diagnostic(s); first: [{first.Severity}] {first.Code}: {first.Message} — see the extraction result's own Diagnostics for the complete list.",
            subject: Reference));
    }

    // The page's zero-based index in its owner's page list (or -1 without an owner) — what the
    // structure tree's marked-content references identify a page by. Resolved
    // per call rather than cached: PageCollection.Move/RemoveAt can change it at any time.
    private int ResolveOwnPageIndex()
    {
        if (_owner is null)
        {
            return -1;
        }

        var pages = _owner.Pages;
        for (var i = 0; i < pages.Count; i++)
        {
            if (ReferenceEquals(pages[i], this) || pages[i].Reference == Reference)
            {
                return i;
            }
        }

        return -1;
    }

    private ObjectRegistry RequireObjects() =>
        _owner?.Objects ?? throw new PlumePdfException("PLUME6029", "This PdfPage was not obtained from an open PdfDocument (e.g. it was constructed by a test in isolation) and has no object graph to extract from.");

    private PdfOptions RequireOptions() => _owner?.Options ?? PdfOptions.Default;
}
