# PLUME7500 — rasterize target surface is invalid or exceeds the size cap

**Cause:** `Raster.RasterSurface.Create` refuses to allocate the target BGRA surface for a
`Pdf.Rasterize`/`doc.Pages[i].Rasterize` call for one of two reasons sharing this one code: (1)
the requested `width`/`height` in pixels is not both positive, or (2) the surface's required
byte size (`width * height * 4`) exceeds `PdfOptions.MaxRasterSurfaceBytes` (default 512 MiB).
Case (2) is a resource-limit guard: a caller-chosen pixel size, or one derived from
a large `PdfRasterizeOptions.Dpi` applied to a large page, could otherwise drive an unbounded
allocation before a single pixel is painted.

**Example:**

```csharp
using var document = PdfDocument.Open(
    "input.pdf",
    PdfOptions.Default with { MaxRasterSurfaceBytes = 1024 }); // 1 KiB — trivially exceeded
var image = document.Pages[0].Rasterize(); // throws PLUME7500
```

**Fix:** For case (2), raise `PdfOptions.MaxRasterSurfaceBytes` if the target size is
legitimately large and expected, or request a smaller `PdfRasterizeOptions.PixelWidth`/
`PixelHeight`/`Dpi`. For case (1), pass a positive pixel width and height — this only happens
via `Raster.Rasterizer.Rasterize`'s lower-level entry point (a caller/programmer error, since
`Documents.PageRasterAdapter` always derives a positive size from `PdfRasterizeOptions.Validate`'s
already-validated fields).

**Recovery attempted:** None — deliberately. A surface this large is refused outright rather
than silently clamped to a smaller size, which would silently change the caller's requested
output resolution.
