# PLUME5013 — signing pass missing its placeholder objects

**Cause:** `Objects.SigningWriteSession.Create` was called against an `ObjectRegistry` whose
dirty-object set does not contain exactly one `Objects.PdfContentsPlaceholder` and exactly one
`Objects.PdfByteRangePlaceholder` — an internal invariant every signing orchestration path
(`SigningOrchestrator`, `DocumentTimestamper`) upholds by registering the signature/document-
timestamp dictionary before calling `Create`.

**Example:** Not reachable through the public API — reported here as a defensive invariant
guarding a lower-level seam (`Objects.SigningWriteSession`) that only the signing orchestration
code calls directly.

**Fix:** File an issue if this surfaces through normal `Pdf.Sign`/`doc.Signatures` use — it
indicates a bug in PlumePDF's own orchestration code, not a malformed input document.

**Recovery attempted:** None — this is a defensive check against an internal bug, not a
recoverable document deviation.
