# PLUME2018 — expected a dictionary key name (diagnostic)

**Cause:** A token that isn't a `PdfName` appeared where a dictionary key was expected. The malformed entry is skipped.

**Example:** A dictionary with a stray non-name token in a key position, e.g. `<< 5 (value) >>`.

**Fix:** Inspect the source near the reported offset; the skipped entry is simply omitted from the resulting dictionary.

**Recovery attempted:** Skips the malformed key/value slot and continues parsing the rest of the dictionary.
