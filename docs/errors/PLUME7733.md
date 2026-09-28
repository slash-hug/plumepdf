# PLUME7733 — optional-content layer suppressed page content (default-OFF OCG) (diagnostic)

**Cause:** The page's content stream contained a `BDC /OC <ocg-or-membership-ref> ... EMC`
marked-content sequence (ISO 32000-1 §8.11.4) whose optional-content group resolves to **OFF**
under the document's default `/OCProperties` `/D` configuration (`Raster.OptionalContent.
OptionalContentConfig`) — with the `/Print` usage-dictionary override applied when
rasterizing under `PrintIntent = true`. The content inside that marked-content span was
deliberately not painted, per the document author's own layer-visibility intent.

**Severity:** `DiagnosticSeverity.Info` — a hidden-by-default optional-content
group is **correct PDF**, not a deviation from a well-formed document; this diagnostic exists
purely so a caller comparing rendered output against an "everything visible" expectation has a
coded signal for *why* some content is missing, rather than a silent blank region. Fired **once
per affected page**, not once per suppressed marked-content span, so a page with many
default-OFF layers doesn't flood `doc.Diagnostics`.

**Example:**

```csharp
// /OCProperties << /OCGs [7 0 R] /D << /OFF [7 0 R] >> >>
// content stream: /OC /MC0 BDC ... (drawing ops for the OFF layer) ... EMC
var image = document.Pages[0].Rasterize(PdfRasterizeOptions.Default with { RenderAnnotations = true });
// diagnostics include PLUME7733 (Info), once for this page; the layer's content is not painted.
```

**Fix:** No caller-side workaround — this is expected behavior for a layered PDF (e.g. a CAD
export or a multi-language document using OCGs to toggle content). PlumePDF has no public
layer-toggling API yet; only the document
default configuration is honored.

**Recovery attempted:** None needed — this is correct behavior. The suppressed content is
skipped; the rest of the page (and any ON-by-default layers) renders normally.
