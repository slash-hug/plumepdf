# PLUME2016 — unexpected keyword while parsing a value (diagnostic)

**Cause:** A bare keyword appeared where a value was expected, and it isn't `true`/`false`/`null` - PlumePDF doesn't know what it means, so it substitutes `PdfNull`.

**Example:** A stray, unrecognized bareword in a dictionary value position.

**Fix:** Inspect the source near the reported offset.

**Recovery attempted:** Substitutes `PdfNull.Instance` and continues parsing.
