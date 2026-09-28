# PLUME6052 — signature verification resource cap exceeded

**Cause:** A document-supplied value driving signature verification exceeded its configured
`PdfOptions` cap (the read-side decompression-bomb analog for signature
content): a signature's `/Contents` hex string longer than `MaxSignatureContentsBytes`, a
`/ByteRange` array with more entries than `MaxByteRangeSegments * 2`, a `/ByteRange` segment
that falls outside the document's actual length, or LTV material collection exceeding
`MaxDssRevocationEntries`.

**Example:**

```csharp
var limited = PdfOptions.Default with { MaxSignatureContentsBytes = 1024 };
using var document = PdfDocument.Open("hostile.pdf", limited);
document.Signatures[0].Verify(); // throws PlumePdfException, Code = "PLUME6052", if /Contents exceeds the cap
```

**Fix:** This is a resource guard against an adversarial or corrupt input, not a normal
deviation — raise the relevant `PdfOptions` cap only if you trust the source and know it
legitimately needs a larger value.

**Recovery attempted:** None — this is a deliberate resource-exhaustion guard, not a
recoverable parse deviation.
