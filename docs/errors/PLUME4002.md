# PLUME4002 — no supplied password authenticates the document

**Cause:** Neither `PdfOptions.UserPassword` nor `PdfOptions.OwnerPassword` (both default to empty, covering the common "encrypted but not password-protected" case) successfully authenticates against the document's `/O`/`/U` validation, for RC4/AES-128 (revisions 2-4) or AES-256 (revision 6).

**Example:** `PdfDocument.Open("protected.pdf")` against a document that actually requires a non-empty password the caller didn't supply.

**Fix:** Supply the correct password via `PdfOptions.UserPassword` or `PdfOptions.OwnerPassword`.

**Recovery attempted:** None - decryption never proceeds with an unauthenticated key.
