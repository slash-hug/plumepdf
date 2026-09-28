# PLUME7750 — image suppressed by its XObject-level /OC membership

**Cause:** An image (or form) XObject carries an `/OC` optional-content membership whose
visibility — resolved against the document's default `/OCProperties` configuration, with the
`/Print` usage override under print intent (Phase 9's `OptionalContentConfig`) — is OFF for the
current rendering intent. The XObject is correctly not painted; this Info diagnostic mirrors
`PLUME7733`'s marked-content (`BDC /OC`) wording for the XObject-level form of the same
mechanism (ISO 32000-1 §8.11.3.3), so a hidden-by-default layer image is never mistaken for a
decode failure during SSIM/oracle comparison.

**Example:**

```csharp
// An image XObject with /OC pointing at an OCG whose default state is OFF.
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME7750 (Info); the image region shows the page background.
```

**Fix:** Nothing is wrong — a default-OFF layer is correct author intent (severity Info,
"no deviation from a well-formed document"). A layer-toggling public API is 1.x.

**Recovery attempted:** Not applicable — suppression IS the correct behavior; rendering
continues normally.
