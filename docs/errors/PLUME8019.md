# PLUME8019 — MacExpertEncoding unsupported

**Cause:** A simple font's `/Encoding` names `MacExpertEncoding` (small caps, ligatures, and fraction glyphs under a distinct glyph-name set), which PlumePDF does not resolve — a documented, deferred gap for this phase, not a malformed document.

**Example:** `/Encoding /MacExpertEncoding` in a `/Type1` font dictionary.

**Fix:** No caller-side fix — this is a known Phase 3 gap (a fast-follow ticket tracks adding MacExpertEncoding's table). A `/ToUnicode` overlay, if the document supplies one, is unaffected and still consulted first.

**Recovery attempted:** Falls back to the font's default built-in encoding notion (StandardEncoding for a nonsymbolic font, an all-unassigned table for a symbolic one) rather than guessing at a StandardEncoding-shaped substitute for MacExpertEncoding's very different glyph set. `PdfOptions.Strict` throws this code instead of tolerating the fallback.
