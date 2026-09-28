# PLUME7734 — `/OCProperties` config malformed; best-effort visible (diagnostic)

**Cause:** `Raster.OptionalContent.OptionalContentConfig` could not make sense of the document
catalog's `/OCProperties` dictionary or its default `/D` configuration — a missing `/OCGs` array,
a `/D` that isn't a dictionary, or an `/ON`/`/OFF`/`/Print`-usage array entry that doesn't resolve
to an optional-content-group dictionary. Rather than refuse to rasterize the page at all over a
malformed layer-visibility hint, the affected group (or the whole config, if `/D` itself is
unusable) falls back to **visible** — the same "degrade, never crash the page" posture every
other adversarial-input cap in this phase follows.

**Example:**

```csharp
// /OCProperties << /OCGs [7 0 R] /D (not-a-dictionary) >>
var image = document.Pages[0].Rasterize(PdfRasterizeOptions.Default with { RenderAnnotations = true });
// diagnostics include PLUME7734; every BDC /OC ... EMC span on the page renders as if its
// group were ON, rather than the page silently losing content to an unparsed config.
```

**Fix:** Correct the source PDF's `/OCProperties` dictionary to ISO 32000-1 §8.11.4's grammar. No
caller-side workaround — PlumePDF always chooses the safer (visible) default over guessing which
groups the malformed config intended to hide.

**Recovery attempted:** Falls back to treating the unresolvable group(s) as ON (visible); every
other well-formed part of `/OCProperties` is still honored. Never a page-wide failure.
