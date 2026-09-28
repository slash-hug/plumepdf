# PLUME2012 — unexpected token type while parsing a value (diagnostic)

**Cause:** The tokenizer produced a token type `ObjectParser` doesn't know how to start a value from (e.g. a stray `]` or `>>` where a value was expected). A `PdfNull` is substituted and parsing continues.

**Example:** A dictionary value slot containing a stray delimiter instead of a real value.

**Fix:** Inspect the source object near the reported offset; PlumePDF already substituted the most reasonable fallback (`null`).

**Recovery attempted:** Substitutes `PdfNull.Instance` and continues parsing the rest of the object.
