# PLUME7715 — malformed or unsupported /ColorSpace entry

**Cause:** `Raster.Color.ColorSpace.Parse` (the one entry point every raster color-space
consumer goes through) rejected a `/ColorSpace` entry for one of three reasons sharing this one
code: a `/ColorSpace` array's first element is not a family name; a resolved `/ColorSpace` entry
is neither a name nor a non-empty array; or a device/CIE-based family name that requires its
array form (e.g. `/ICCBased`, `/Indexed`) was given as a bare name instead, which carries no
parameters to resolve it with.

**Example:**

```csharp
// /ColorSpace /ICCBased — a bare name, but ICCBased needs its [/ICCBased <stream ref>] array
// form to know which ICC stream to fall back from.
ColorSpace.Parse(PdfName.Get("ICCBased"), resolve, options.Filters, options, diagnostics);
// throws PLUME7715
```

**Fix:** Correct the source PDF's `/ColorSpace` entry to ISO 32000-1 §8.6.3's grammar for its
family — either a valid device color-space name (`/DeviceGray`/`/DeviceRGB`/`/DeviceCMYK`/`/Pattern`)
or the array form the family requires.

**Recovery attempted:** None — deliberately. There is no safe default color space to substitute
without risking a visibly wrong color; the shading/fill this color space feeds degrades to its
caller's own fallback (e.g. black, or a flat placeholder) instead.
