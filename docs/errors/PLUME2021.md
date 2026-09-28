# PLUME2021 — malformed indirect-object framing

**Cause:** `ObjectParser.ParseIndirectObject` expected `N G obj` at the very start of the buffer and didn't find a valid object number, generation number, or the `obj` keyword in that order.

**Example:** A cross-reference entry pointing at an offset that isn't actually the start of an `N G obj` header (e.g. a corrupted offset).

**Fix:** The offending offset is unreliable; PlumePDF's cross-reference reader and object resolver already treat this as a per-object failure (see `PLUME2061`) rather than failing the whole document.

**Recovery attempted:** Caught by `ObjectResolver` (`PLUME2061`) when resolving an individual object; the object resolves to `PdfNull` and the rest of the document still opens.
