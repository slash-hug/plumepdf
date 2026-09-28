# PLUME6080 — XMP packet exceeds the read-size cap

**Cause:** the document's `/Metadata` XMP stream filter-decodes to more bytes than
`PdfOptions.MaxXmpPacketReadBytes` (default 8 MiB). This is the read-side sibling of
`PLUME6056`'s write cap (`PdfOptions.MaxXmpPacketWriteBytes`): a hostile packet can be tiny
compressed but enormous decoded, and every consumer that scans the packet's text —
`PdfDocument.GetXmpMetadataBytes()`/`GetXmpMetadataText()`, and through them
`Pdf.ValidatePdfA`'s identification/agreement scans — would otherwise do unbounded work over
it. The cap is enforced at the single read choke point before any scan runs, so the refusal
is immediate, never a multi-minute stall.

**Example:**

```csharp
using var document = PdfDocument.Open("hostile-metadata.pdf");
document.GetXmpMetadataText(); // throws PLUME6080 when the decoded packet exceeds the cap
Pdf.ValidatePdfA("hostile-metadata.pdf"); // same refusal, before any rule scan
```

**Fix:** For a trusted source whose XMP is legitimately enormous, raise the cap:

```csharp
var options = PdfOptions.Default with { MaxXmpPacketReadBytes = 64L * 1024 * 1024 };
using var document = PdfDocument.Open("big-but-trusted.pdf", options);
```

Real-world XMP packets run a few hundred bytes to a few KB; treat an over-cap packet from an
untrusted source as hostile.

**Recovery attempted:** None — this is a resource-limit guard, thrown even under
default (non-`Strict`) options; refusing a hostile document is not a recoverable deviation.
