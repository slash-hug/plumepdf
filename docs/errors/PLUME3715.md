# PLUME3715 — colr box unrecognised or inconsistent (diagnostic)

**Cause:** The JP2 `colr` box names a `METH`/`EnumCS` combination this decoder does not
recognise (only METH 1 with EnumCS 16 sRGB / 17 greyscale / 18 sYCC / 12 CMYK, METH 2
restricted ICC, and METH 3 any-ICC — the deliberate Part 2 exception — are recognised), an ICC-based
`colr` box's profile has a component count other than 1, 3, or 4, the `pclr`/`cmap`/`cdef`
boxes are mutually inconsistent (e.g. `cmap` references a palette column `pclr` doesn't have, a
direct-use `cmap` entry names a component other than 0, `cmap` has more than 255 entries or an
unknown `MTYP`, `pclr` declares zero entries or zero columns or a column deeper than 16 bits,
or `cdef` names an opacity channel index at or beyond the decoded plane count),
a `pclr` box's own declared entry/column counts (`NE`/`NPC`) do not fit inside its actual
body length (the box is ignored rather than indexed past its own bounds), or a second `colr`
box disagrees with (or is less well-formed than) the colour space an earlier `colr` box already
established — I.5.3.3 permits multiple `colr` boxes and the first recognised one wins; a later
box that names the *same* space is a genuine duplicate (real encoders emit them) and records
nothing, while a contradicting one is a deviation like any other (Warning; throws under
`PdfOptions.Strict`).

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx-bad-colr.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME3715 (Warning); the image still paints, with its colour
// space derived by colour-channel count instead. PLUME7744 also fires (marker parity).
```

**Fix:** Re-export the source image with a conformant `colr` box. A caller who needs different
handling can register a replacement `IPdfFilter` for `JPXDecode` via `PdfOptions.Filters`,
overriding PlumePDF's built-in JPEG 2000 decoder entirely.

**Recovery attempted:** This is a deviation, not a refusal: `FilterDiagnostics.ReportDeviation`
records it as a Warning; the unrecognised or inconsistent colour box is ignored and colour
space is derived by colour-channel count instead (1 Gray, 3 RGB, 4 CMYK, else the first 3
channels as RGB). Under `PdfOptions.Strict` it throws instead. Reached through the rasterizer,
`ImageXObjectResolver`'s marker-parity rule also records `PLUME7744` on the same image.
