# PLUME8024 — shaping lookup-application budget exceeded

**Cause:** A single `ILineShaper.Shape` call (internal shaping seam) attempted more GSUB/GPOS
lookup applications than `PdfOptions.MaxShapingLookupApplications` allows (default 100,000),
or a contextual/chaining lookup nested deeper than the shaping engine's fixed
recursion guard. Every attempted lookup — matched or not — counts against the budget, so a
hostile or pathological font paired with adversarial input (e.g. a crafted contextual/chaining
lookup graph, a cyclic lookup reference, or a pathologically long single run against a font with
per-glyph `GSUB`/`GPOS` lookups) cannot drive unbounded shaping work. `SimpleShaper` (the Latin/
Cyrillic/Greek fast path) and the complex-script engine (`ComplexShaper`/`OpenTypeLayoutEngine`,
Arabic/Devanagari) share this one code — both are guarded by the same
`ShapingOptions.MaxLookupApplications` budget, constructed from `PdfOptions.MaxShapingLookupApplications`.

**Example:** `Manuscript.Render` (or `PdfFont.FromFile` embedding a font later used in a
`Text`) with `PdfOptions.MaxShapingLookupApplications` tightened low against a document whose
text genuinely needs more lookup applications than the configured budget — or, more likely in
practice, an attacker-supplied font/text combination engineered to maximize lookup count per
glyph, or a font with a cyclic contextual-lookup chain crafted to force deep recursion.

**Fix:** For a genuine large or heavily-shaped document, raise the budget:
`PdfOptions.Default with { MaxShapingLookupApplications = 1_000_000 }`. For an untrusted or
attacker-controlled font/text pairing, the refusal is working as intended — do not raise the
budget past what the real workload needs.

**Recovery attempted:** None — deliberately. PlumePDF does not silently truncate a shaped run
or fall back to unshaped glyphs when the budget is exhausted (the same no-silent-tofu doctrine
`PLUME8009` states); the caller must either supply a larger, deliberately-chosen budget or
reject the input.
