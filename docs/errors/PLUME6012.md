# PLUME6012 — pages from an encrypted source can't go into unencrypted output

**Cause:** A page from a `PdfDocument` whose `HasEncryptedSource` is `true` was about to be
written into output that would not be encrypted, which would emit the source's *decrypted*
content unprotected. Two verbs refuse this:

- `Pdf.Merge`/`Pdf.Split` refuse any encrypted source: they always build a new, unencrypted
  document.
- `PageCollection.Add`/`Insert` refuse a page from an encrypted source when the document it is
  added to is not encrypted. Into an encrypted document it is allowed: the appended objects are
  encrypted with that document's own key when it is saved with `SaveIncremental`.

**Example:**

```csharp
using var source = PdfDocument.Open("protected.pdf"); // opens fine (empty user password)
using var merged = Pdf.Merge(source);                 // throws PlumePdfException PLUME6012
```

```csharp
using var protectedSource = PdfDocument.Open("protected.pdf");
using var plain = PdfDocument.Open("plain.pdf");
plain.Pages.Add(protectedSource.Pages[0]);            // throws PlumePdfException PLUME6012

using var alsoProtected = PdfDocument.Open("also-protected.pdf");
alsoProtected.Pages.Add(protectedSource.Pages[0]);    // allowed: the destination is encrypted
alsoProtected.SaveIncremental("also-protected.pdf");
```

**Fix:** To copy a protected page, add it to an encrypted document and save that with
`SaveIncremental`. If an unencrypted copy is genuinely intended and you are authorized to remove
the protection, decrypt the file with a tool you trust first, then merge or add from the
decrypted copy.

**Recovery attempted:** None. This is a deliberate refusal, checked before anything is copied,
so the destination is unchanged.
