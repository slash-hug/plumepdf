# PLUME2011 — unexpected end of input while parsing a value

**Cause:** `ObjectParser.ParseValue` was asked to read a value and the tokenizer had nothing left - the buffer ended mid-object.

**Example:** A truncated file cut off in the middle of an object's value.

**Fix:** Nothing to fix on the caller's side beyond expecting truncated/damaged documents; `PdfDocument.Open`'s recovery ladder (brute-force scan) may still recover the document as a whole even though this specific object couldn't be parsed.

**Recovery attempted:** The object resolver (`PLUME2061`) catches this per-object and substitutes `PdfNull`, letting the rest of the document still open.
