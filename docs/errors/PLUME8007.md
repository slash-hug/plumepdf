# PLUME8007 — composite glyph cycles or nests too deep

**Cause:** While resolving a composite glyph's component references (either during subsetting or direct composite-closure resolution), either (a) a component chain revisits one of its own ancestors — a genuine reference cycle, not legitimate component reuse — or (b) the chain nests deeper than the configured `MaxCompositeGlyphDepth` (default 8). Detected by an explicit iterative work-queue with per-glyph coloring, never by recursion, so a hostile cyclic or very deep chain raises this coded exception instead of overflowing the call stack.

**Example:** A crafted font where composite glyph A references component glyph B, which in turn references component glyph A again; or a linear chain of composites nested far deeper than any real font's glyph set would need.

**Fix:** Use a well-formed font (real-world fonts never have composite cycles). If a legitimately deep — but non-cyclic — composite chain trips this, raise `MaxCompositeGlyphDepth` for the parsing/subsetting call.

**Recovery attempted:** None — the composite-glyph closure is abandoned at the point the cycle or depth cap is detected; the font cannot be safely embedded or subset.
