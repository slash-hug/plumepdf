# PLUME6058 — PDF/A metadata combined with `PdfOptions.Deterministic` requires caller-supplied dates

**Cause:** `Documents.Metadata.XmpWriter` serialized an `Documents.Metadata.XmpPacket` whose
`Conformance` is not `PdfAConformance.None` (i.e. the packet declares PDF/A identification)
while `PdfOptions.Deterministic` is set, but `CreateDate` and/or `ModifyDate` were left unset.
`PdfOptions.Deterministic` promises
byte-identical output across runs (`docs/spec.md` "public and permanent"), but PDF/A mandates
`xmp:CreateDate`/`xmp:ModifyDate` — a value PlumePDF cannot invent without breaking one of the
two guarantees: a fixed fake timestamp would silently misrepresent the document's real dates; the
current time would silently break determinism. Neither is acceptable, so PlumePDF refuses
instead, mirroring deterministic signing's identical treatment of signing time.

**Example:**

```csharp
var deterministic = PdfOptions.Default with { Deterministic = true };
using var document = PdfDocument.Compose(page => page.Content().Text("Hi"), deterministic);
document.SetXmpMetadata(new XmpPacket { Conformance = PdfAConformance.A2b }); // throws PLUME6058
```

**Fix:** Supply both `CreateDate` and `ModifyDate` explicitly on the `XmpPacket`:

```csharp
document.SetXmpMetadata(new XmpPacket
{
    Conformance = PdfAConformance.A2b,
    CreateDate = fixedTimestamp,
    ModifyDate = fixedTimestamp,
});
```

**Recovery attempted:** None — this is a caller-input contradiction (a PDF/A packet with no
dates, requested under a determinism guarantee that forbids inventing them) the API surface
refuses rather than papering over silently.
