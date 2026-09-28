# PLUME7752 — pattern paint degraded (diagnostic)

**Cause (diagnostic; the paint is skipped, the page still renders):** a fill or stroke used
the `/Pattern` colorspace, and the named pattern could not be painted: the `/Pattern`
resource is missing or malformed; it is a shading pattern (`/PatternType 2`) or an uncolored
tiling pattern (`/PaintType 2`), neither painted this phase; the pattern-to-device matrix is
rotated, skewed, or mirrored (the axis-aligned tiler's limit); the cell raster or tile count
would exceed a resource cap (`PdfOptions.MaxRasterSurfaceBytes`, the tile-instance bound);
the cell content stream failed to decode; or the paint is a pattern-colored **stroke**, which
is not supported this phase.

Previously, an unpaintable pattern fill fell through to the paint pass's black
fallback — real-world scans wrapped in a full-page pattern fill rendered as solid-black
pages with no diagnostic. The honest degradation is to paint **nothing** for that one fill
(the same posture an undecodable image takes, PLUME7744) and record this code naming the
reason.

Supported and painted (no diagnostic): colored (`/PaintType 1`) tiling patterns with an
axis-aligned pattern-to-device matrix — the cell's own content (vector marks, text, images)
renders through the standard pipeline and tiles across the fill region, masked by the fill
path.

**Example:**

```csharp
using var document = PdfDocument.Open("pattern-fill.pdf");
var image = document.Pages[0].Rasterize(); // completes; the unpaintable fill is skipped
// image.Diagnostics contains a PLUME7752 entry naming the reason
```

**Fix:** For a shading-pattern or uncolored-pattern document, no caller action renders it
this phase — the gap is recorded here rather than silently mis-painted. For cap refusals,
raise `PdfOptions.MaxRasterSurfaceBytes` if the pattern cell is legitimately enormous.

**Recovery attempted:** That one fill/stroke is skipped; every other page object still
paints.
