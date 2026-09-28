# PLUME6082 — malformed marked-content /MCID tolerated (diagnostic)

**Cause (diagnostic by default; thrown under `PdfOptions.Strict`):** while tracking
marked-content provenance for extraction's structure-tree reading order, a
`BDC` operator's properties declared an `/MCID` that is not a non-negative integer in
`int` range — e.g. a hostile `1e20`, which a raw cast would have silently wrapped to
`int.MinValue`. Read leniently per the read-side convention (`PdfNumber.TryToInt32`): the
sequence is treated as carrying no MCID, so letters painted inside it get
`Letter.Mcid = null` and cannot participate in structure-tree ordering.

**Example:**

```csharp
using var document = PdfDocument.Open("hostile-mcid.pdf");
var extracted = document.Pages[0].ExtractText(); // completes; the deviation is recorded
// extracted.Diagnostics contains a PLUME6082 entry naming the offset and the bad value
```

**Fix:** No action needed for extraction to succeed — ordering falls back to geometry for
words the structure tree can no longer reach (see `PLUME6070`). Pass
`PdfOptions.Strict = true` to reject a document carrying malformed marked-content ids
instead of tolerating it.

**Recovery attempted:** The `BDC`…`EMC` sequence is still honored for nesting purposes; only
its unusable id is dropped, and the letters inside it carry the enclosing sequence's MCID
(or none).
