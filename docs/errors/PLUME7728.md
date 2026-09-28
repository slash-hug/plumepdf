# PLUME7728 — malformed ExtGState /SMask soft-mask dictionary

**Cause:** `Raster.Transparency.SoftMask`'s parser rejected an `ExtGState`'s `/SMask` soft-mask
dictionary for one of three structural reasons sharing this one code (ISO 32000-1 §11.6.5.2):
its required `/S` (subtype — `/Alpha` or `/Luminosity`) entry is missing; its `/S` value is
neither `/Alpha` nor `/Luminosity`; or its optional `/TR` (transfer function) entry, when
present, is not a 1-input/1-output function (a soft mask's transfer function maps one mask
value to one adjusted mask value — any other arity is meaningless here).

**Example:**

```csharp
// /SMask << /S /Luminosity /TR << /FunctionType 2 /Domain [0 1] /C0 [0 0] /C1 [1 1] /N 1 >> >>
// — /TR is 1-in/2-out, not 1-in/1-out.
SoftMask.Parse(smaskDict, resolve, options.Filters, options, diagnostics);
// throws PLUME7728
```

**Fix:** Correct the source PDF's `/SMask` dictionary to ISO 32000-1 §11.6.5.2's grammar — a
valid `/S`, and a 1-in/1-out `/TR` if present.

**Recovery attempted:** None — there is no principled default soft mask to substitute; the
transparency-group compositing this feeds degrades to its caller's own fallback (typically, no
masking applied) instead.
