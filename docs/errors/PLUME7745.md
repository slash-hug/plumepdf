# PLUME7745 — image uses JPXDecode (JPEG 2000), not decoded in-house — **deprecated**

**Deprecated (2026-09):** JPEG 2000 (`/JPXDecode`) now decodes in-house by default — the
"register your own `IPdfFilter`" posture this page originally documented is no longer true.
A `/JPXDecode` image
decodes through PlumePDF's own clean-room JPEG 2000 Part 1 decoder (`Filters/Jpx`) automatically,
with no caller registration required; a malformed or unsupported-profile codestream is refused
with one of the coded `PLUME3700`–`PLUME3718` diagnostics instead (see those pages, and
`docs/errors/README.md`'s index), and a degraded-but-painted image still records `PLUME7744`
exactly like any other codec's recoverable deviation. A `/ColorSpace` that disagrees with the
codestream's own colour-channel count is `PLUME7753`. No code in `src/` mints this code any more.

Per `docs/errors/README.md`: codes are never renumbered or reused once shipped; this page stays
to keep the code stable for anyone who saw it on an older build, rather than being deleted. A
caller who monitors `doc.Diagnostics` for `PLUME7745` should scan for `PLUME3700`–`PLUME3718` and
`PLUME7744` instead.

The override seam this page's Fix section pointed to is unchanged in shape, only in default: a
caller who wants JPX *refused* rather than decoded now registers a replacement `IPdfFilter` for
`JPXDecode` via `PdfOptions.Filters` (see `docs/cookbook/rasterize-scanned-jpeg2000.md`'s
`refuse-jpx-decode` recipe) — the built-in decoder is an override-friendly default, not a hard
codec choice.

---

*Original page, preserved for history:*

**Cause:** A rasterized page contains an image XObject whose terminal filter is `/JPXDecode`
(JPEG 2000). In-house JPX decode was originally a ratified 1.x community-seam gap:
PlumePDF shipped no JPEG 2000 codec, so the image was skipped with this diagnostic.

**Example:**

```csharp
using var document = PdfDocument.Open("scan-with-jpx.pdf");
var image = document.Pages[0].Rasterize();
// image.Diagnostics contains PLUME7745; the JPX image region is blank, the page still renders.
```

**Fix:** Register a JPEG 2000 `IPdfFilter` via `PdfOptions.Filters` — the render-time resolver
decodes through the same registry `ExtractImages` honors, so a caller-registered
JPX filter paints automatically and this diagnostic never fires. This mirrors `PLUME6023`'s
extraction-side wording for the identical seam.

**Recovery attempted:** The image is skipped (painted as nothing) and the rest of the page
renders. Never a page-wide failure.
