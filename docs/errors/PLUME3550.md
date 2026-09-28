# PLUME3550 — JBIG2: halftone/pattern-dictionary segments are not supported (per-segment fallback)

**Cause:** A segment is a pattern dictionary (type 16) or halftone region (types 20/22/23) - out of scope for Phase 7 (halftone regions are 1.x scope).

**Example:** A JBIG2 stream using halftone-coded regions for continuous-tone-like content (uncommon in scanned-text PDFs, more common in some fax/photo hybrids).

**Fix:** None available from this decoder; the region is simply omitted from the page.

**Recovery attempted:** The segment is skipped; every other segment in the stream still decodes and composes onto the page normally.
