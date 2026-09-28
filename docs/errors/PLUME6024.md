# PLUME6024 — extraction against a document whose /P clears the "extract content" bit (advisory)

**Cause:** `PdfPage.ExtractText` was called against a document whose `/Encrypt` dictionary's
`/P` entry clears bit 5 (`PdfPermissions.ExtractContent`, ISO 32000-1 Table 22) — the producer
did not intend for the document's text/graphics to be copied out.

**Example:** A PDF exported with "copying disabled" from an authoring tool.

**Fix:** None required. PlumePDF treats `/P` as advisory metadata and never refuses extraction
based on it — refusing would be security theater once the encryption key is
already derived, and would break the accessibility case bit 10 exists to protect (a screen
reader must still be able to extract text even when general copying is disallowed). Inspect
`document.Permissions` if your application needs to honor this signal itself.

**Recovery attempted:** Not applicable — this is an informational diagnostic, not a failure.
