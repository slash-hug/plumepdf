# PLUME6059 — cannot set XMP metadata: document has no resolvable catalog

**Cause:** `PdfDocument.SetXmpMetadata` was called on a document whose trailer's `/Root` entry is
missing, is not an indirect reference, or did not resolve to a dictionary — the same condition
`Documents.DocumentCatalog.Resolve` records as `PLUME6001` at open time. Without a resolvable
catalog there is nowhere to attach the `/Metadata` entry.

**Example:**

```csharp
// A document opened from a source whose /Root could not be resolved (recorded as PLUME6001
// in doc.Diagnostics at open time under default, lenient options):
using var document = PdfDocument.Open("malformed-no-root.pdf");
document.SetXmpMetadata(new XmpPacket { Title = "Report" }); // throws PLUME6059
```

**Fix:** Check `document.Diagnostics` for a prior `PLUME6001` entry before calling
`SetXmpMetadata` on a document opened from an untrusted or possibly-malformed source. A document
produced by `PdfDocument.Compose`/`Manuscript.Render` always has a resolvable catalog and never
trips this code.

**Recovery attempted:** None — there is no catalog to attach metadata to; this mirrors the
open-time `PLUME6001` diagnostic's own "no accessible catalog" condition, surfaced here as a
refusal because `SetXmpMetadata` cannot proceed without one.
