# Rasterizing text using substitute fonts

`Pdf.Rasterize`/`doc.Pages[i].Rasterize` (Phases 7–9) never fail a
page for using a font that isn't embedded in the source PDF. PlumePDF ships fourteen bundled
substitute faces, compiled directly into the package as FieldRVA blobs (no separate download, no
reflection-based loading, NativeAOT-safe by construction): twelve Liberation Sans/Serif/Mono
faces, regular/bold/italic/bold-italic each (metric-compatible with Arial/Times New
Roman/Courier New respectively; see `NOTICE`'s Liberation Fonts entry), plus PDFium's own Foxit
Symbol and Foxit Dingbats faces for the Standard-14 `Symbol`/`SymbolMT`/`ZapfDingbats` fonts (see
`NOTICE`'s Foxit entry).

A non-embedded font is matched to a substitute face and its glyphs are painted onto the raster
surface automatically — no caller action needed:

<!-- snippet: rasterize-substitute-fonts -->
<a id='snippet-rasterize-substitute-fonts'></a>
```cs
// A page whose text uses a font not embedded in the source PDF still rasterizes
// successfully, glyphs included — non-embedded text falls back to one of fourteen bundled
// substitute faces (matched automatically by the font's declared bold/italic/serif/fixed-pitch
// style, or to the Foxit Symbol/Dingbats face for a Standard-14 symbol font; no caller action
// needed). Every other kind of content — vector paths, axial/radial shadings — renders
// regardless of which fonts the page's text uses.
var image = Pdf.Rasterize("samples/classic-xref.pdf");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase8.cs#L118-L126' title='Snippet source file'>snippet source</a> | <a href='#snippet-rasterize-substitute-fonts' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Expected output (from the backing test `CookbookTests.RasterizeSubstituteFonts`):

<!-- snippet: CookbookTests.RasterizeSubstituteFonts.verified.txt -->
<a id='snippet-CookbookTests.RasterizeSubstituteFonts.verified.txt'></a>
```txt
Rendered 267x133 without needing any font embedded.
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.RasterizeSubstituteFonts.verified.txt#L1-L1' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.RasterizeSubstituteFonts.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Symbol and ZapfDingbats (checkbox marks)

The Standard-14 `Symbol` and `ZapfDingbats` fonts are almost never embedded, and every form
viewer draws a checked checkbox or selected radio button with ZapfDingbats (`/ZaDb … Tf (4) Tj`
— glyph `a20`, the heavy check mark). PlumePDF renders both with PDFium's own Foxit Symbol and
Foxit Dingbats faces, using each font's built-in encoding (a producer-stamped
`/Encoding /WinAnsiEncoding` on these two fonts is ignored, exactly as PDFium does — otherwise
`(4)` would resolve to the digit "four", which the face does not have). Widget appearances are
annotations, so they only paint when you ask for them:

<!-- snippet: rasterize-symbol-fonts -->
<a id='snippet-rasterize-symbol-fonts'></a>
```cs
// Checkbox and radio "on" marks are drawn with the Standard-14 ZapfDingbats font, which is
// never embedded; PlumePDF renders it (and Symbol) with PDFium's own Foxit faces. Widget
// appearances are annotations, so ask for them — a plain Rasterize() paints page text only.
var image = Pdf.Rasterize("samples/symbol-fonts.pdf", PdfRasterizeOptions.Default with { RenderAnnotations = true });
var frame = image.Frames[0];

// The sample's checkbox sits at [150 40 168 58] on a 200x100 pt page; look for dark pixels there.
var checkMarkPainted = HasInk(frame, x0: 150, y0: 40, x1: 168, y1: 58, pageWidth: 200, pageHeight: 100);

// Each substituted font records a PLUME7510 diagnostic naming the bundled face it used.
var substitutedFaces = image.Diagnostics
    .Where(d => d.Code == "PLUME7510")
    .Select(d => d.Message.Contains("FoxitDingbats") ? "FoxitDingbats" : d.Message.Contains("FoxitSymbol") ? "FoxitSymbol" : "Liberation")
    .Distinct()
    .OrderBy(f => f, StringComparer.Ordinal);
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase8.cs#L138-L154' title='Snippet source file'>snippet source</a> | <a href='#snippet-rasterize-symbol-fonts' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

`HasInk` above is a few lines of test-side pixel arithmetic (see the snippet source); the
recipe prints:

<!-- snippet: CookbookTests.RasterizeSymbolFonts.verified.txt -->
<a id='snippet-CookbookTests.RasterizeSymbolFonts.verified.txt'></a>
```txt
Rendered 267x133 with RenderAnnotations = true.
Check mark painted inside the checkbox: True
Substitute faces used: FoxitDingbats, FoxitSymbol, Liberation
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.RasterizeSymbolFonts.verified.txt#L1-L3' title='Snippet source file'>snippet source</a> | <a href='#snippet-CookbookTests.RasterizeSymbolFonts.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

> **A plain `Rasterize()` paints no widgets.** Page text set in Symbol/ZapfDingbats renders
> either way; the checkbox mark needs `RenderAnnotations = true` (see
> [rasterize-page](rasterize-page.md#annotations-forms-print-intent-and-optional-content-phase-9)).

## How face selection works

A non-embedded Latin-script font is matched to a substitute face by its font descriptor's
declared characteristics:

| Descriptor signal | Selects |
|---|---|
| Fixed-pitch (`/Flags` bit 1) | Liberation Mono |
| Serif (`/Flags` bit 2), not fixed-pitch | Liberation Serif |
| Neither | Liberation Sans |
| Bold (`/Flags` bit 19, `ForceBold`, or `/FontWeight` ≥ 600) | …-Bold / …-BoldItalic variant |
| Italic (`/Flags` bit 7, or a nonzero `/ItalicAngle`) | …-Italic / …-BoldItalic variant |

A non-embedded (or embedded-but-unparseable) Standard-14 `Symbol`/`SymbolMT`/`ZapfDingbats` font
bypasses that table entirely and selects the matching Foxit face instead — its declared
`/Encoding` is ignored in favor of the face's own built-in encoding, exactly as PDFium does.

A non-embedded font whose `/BaseFont` name matches a known CJK (Chinese/Japanese/Korean)
marker has no substitute — the Liberation set covers Latin/Cyrillic/Greek scripts only. That
gap is recorded as a `PLUME7511` diagnostic (a substituted Latin-script font records
`PLUME7510` instead — both informational, never a refusal) rather than throwing; affected
glyphs will render using the `.notdef` glyph. Embed a CJK font in the source PDF if exact CJK
rendering fidelity matters — there is no substitute-font workaround for that gap. See
[the error index](../errors/README.md#index) for both codes' full pages.

## See also

- [Rasterize a page to pixels](rasterize-page.md) — the base recipe this one builds on.
