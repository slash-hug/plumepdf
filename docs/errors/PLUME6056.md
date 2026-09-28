# PLUME6056 — serialized XMP packet exceeds `PdfOptions.MaxXmpPacketWriteBytes`

**Cause:** `Documents.Metadata.XmpWriter` serialized an `Documents.Metadata.XmpPacket` (via
`PdfDocument.SetXmpMetadata`) and the resulting RDF/XML packet's byte length exceeds
`PdfOptions.MaxXmpPacketWriteBytes`. Guards a caller-supplied packet carrying a pathologically
large text field (e.g. a multi-megabyte `Keywords` string) from producing an unbounded
`/Metadata` stream.

**Example:**

```csharp
using var document = PdfDocument.Compose(page => page.Content().Text("Hi"));
var hugeKeywords = new string('a', 5 * 1024 * 1024);
document.SetXmpMetadata(new XmpPacket { Keywords = hugeKeywords }); // throws PLUME6056
```

**Fix:** Trim the offending field(s), or raise the cap explicitly if the large packet is
genuinely intended: `PdfOptions.Default with { MaxXmpPacketWriteBytes = 8L * 1024 * 1024 }`.

**Recovery attempted:** None — this is a resource-limit refusal against caller-supplied input,
not a document deviation to repair.
