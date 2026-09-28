# PLUME2022 — expected 'endobj' after an object's value (diagnostic)

**Cause:** After parsing an indirect object's value, the next keyword wasn't `endobj`. Parsing continues anyway, using the value already parsed.

**Example:** A producer that omits `endobj`, or one whose value's own closing delimiter (e.g. a stream's `endstream`) is immediately followed by something else.

**Fix:** Usually harmless; inspect the source only if the object's value itself looks wrong.

**Recovery attempted:** Continues using the already-parsed value; `endobj` is not required for a successful parse.
