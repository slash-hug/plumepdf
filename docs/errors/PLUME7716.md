# PLUME7716 — `/Lab` color space out of scope (Phase 8) — **deprecated**

**Deprecated (2026-08-21):** `/Lab` support (colorimetric Lab→sRGB conversion) shipped in Phase 9
(`Raster.Color.ColorSpace`/`CieConversions`) and the Phase 8 refusal this code named was removed — `ColorSpace.Parse` now parses `/Lab`
for real. A malformed `/Lab` parameter dictionary (missing/invalid `/WhitePoint`, or a
malformed `/Range`) degrades to a `Device*` approximation with a **`PLUME7738`** diagnostic —
the same code `/CalRGB`/`/CalGray`'s own invalid-parameter case already used — rather than a
dedicated `/Lab`-only code, so no code in `src/` mints `PLUME7716` any more.

Per `docs/errors/README.md`: codes are never renumbered or reused once shipped; this page stays
to keep the code stable for anyone who saw it on an older build, rather than being deleted.

---

*Original page, preserved for history:*

**Cause:** `Raster.Color.ColorSpace.Parse` (or the equivalent Phase 8 shading/pattern color
resolution path) encountered a `/Lab` (CIE L\*a\*b\*, ISO 32000-1 §8.6.5.4) color space entry.
Colorimetric Lab→sRGB conversion was deliberately out of scope for Phase 8 — every `/Lab` color space was refused rather than
approximated, since there is no principled Device* fallback for an L\*a\*b\* triple the way
DeviceGray/DeviceRGB/DeviceCMYK values can be read directly.

**Example:**

```csharp
// /ColorSpace [/Lab << /WhitePoint [0.9505 1.0 1.089] >>] anywhere in a Phase 8 rasterized page.
var image = document.Pages[0].Rasterize();
// threw PLUME7716 through Phase 8.
```

**Fix (Phase 8):** None — `/Lab` color spaces were out of scope for Phase 8 rasterization
entirely. Phase 9 ships `/Lab` support; see `PLUME7738` for the parameter-validation case that
replaces this code going forward.

**Recovery attempted (Phase 8):** None — there was no principled default to substitute for an
unsupported colorimetric conversion.
