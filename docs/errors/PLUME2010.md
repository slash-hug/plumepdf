# PLUME2010 — object nesting exceeded the configured limit

**Cause:** A value's arrays-within-arrays / dictionaries-within-dictionaries nesting exceeded `PdfOptions.MaxObjectNestingDepth` (default 64) - guards against a pathological `[[[[[...` input recursing until the stack overflows.

**Example:** A hand-crafted or corrupted object with hundreds of nested `[ [ [ ... ] ] ]`.

**Fix:** If the document is legitimately this deeply nested (very unusual), raise `PdfOptions.MaxObjectNestingDepth`. Otherwise this is a strong signal of a hostile or corrupt input.

**Recovery attempted:** None - nesting this deep can't be partially parsed safely; the object parse fails outright.
