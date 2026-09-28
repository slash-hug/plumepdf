# PLUME2052 — object stream /Extends chain exceeded the configured depth limit

**Cause:** The `/Extends` chain walk exceeded `PdfOptions.MaxObjectStreamExtendsDepth` (default 32) - independent of the cycle guard, catches a chain that's merely very long.

**Example:** An unusually deep (or hostile) `/Extends` chain.

**Fix:** If the document legitimately has this many extended object streams, raise `PdfOptions.MaxObjectStreamExtendsDepth`.

**Recovery attempted:** None.
