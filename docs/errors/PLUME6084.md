# PLUME6084 — compression would invalidate an existing signature

**Cause:** `Pdf.Compress` was called on a document that carries one or more signatures or
document timestamps, and `PdfCompressOptions.AllowInvalidatingSignatures` was not set.
Compression is a full rewrite: every object is renumbered and the bytes each signature's
`/ByteRange` covers move, so no signature would verify afterwards. PlumePDF refuses rather
than write a document that still *looks* signed.

**Example:**

```csharp
Pdf.Compress("signed-contract.pdf", "contract-small.pdf"); // throws PLUME6084
```

`Pdf.EstimateCompression` (the dry run of `Compress`) raises the same code for the same source, since the `Compress` call it estimates would refuse. With `AllowInvalidatingSignatures` it strips the signatures only from its own in-memory copy of the document, never from the file, and writes nothing.

**Fix:** Opt in explicitly. `Compress` then strips the signature fields and their widgets
and the catalog's `/Perms` and `/DSS` before writing, so the output is honestly unsigned:

```csharp
var options = new PdfCompressOptions { AllowInvalidatingSignatures = true };
Pdf.Compress("signed-contract.pdf", "contract-small.pdf", options); // the output has no signatures
```

To keep the signatures, don't compress a signed document: compress before signing.

**Recovery attempted:** None — this is a refusal before anything is planned or written. No
output file is created, and an existing file at the output path is left as it was.
