namespace PlumePdf.Documents.Compression;

/// <summary>How an image's placements could be measured.</summary>
internal enum PlacementKind
{
    /// <summary>Every placement is on a page, form, appearance or soft-mask group with a known device size.</summary>
    Measured = 0,

    /// <summary>Placed only inside tiling patterns.</summary>
    TilingPatternOnly,

    /// <summary>Placed in a Type 3 glyph, or in a tiling pattern as well as somewhere measured.</summary>
    Unmeasured,
}

/// <summary>
/// One image XObject's placements, folded: the lowest effective DPI per axis across every
/// measured placement, and the page where the lower of the two was seen.
/// </summary>
/// <param name="DpiX">Lowest horizontal DPI (CTM column magnitude × <c>/UserUnit</c>, per image pixel), or null when not measured.</param>
/// <param name="DpiY">Lowest vertical DPI, or null when not measured.</param>
/// <param name="PageIndex">The page of the placement with the lowest <c>min(DpiX, DpiY)</c>; for an unmeasured image, the first page it was met on.</param>
/// <param name="Kind">Whether the placements could be measured.</param>
internal sealed record ImagePlacementSummary(double? DpiX, double? DpiY, int PageIndex, PlacementKind Kind);

/// <summary>The placement scan's output for one document.</summary>
/// <param name="Images">Every image XObject reached by a <c>Do</c>, keyed by object number. An image never reached is absent.</param>
/// <param name="InlineImagePages">The page index of every inline image met, one entry per occurrence, in page order.</param>
/// <param name="Diagnostics">Cycles, depth or operator caps hit, and unreadable content met during the scan.</param>
internal sealed record PlacementScan(
    IReadOnlyDictionary<int, ImagePlacementSummary> Images,
    IReadOnlyList<int> InlineImagePages,
    DiagnosticCollection Diagnostics);
