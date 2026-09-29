# Changelog

All notable behavior changes to PlumePDF are recorded here, in the style of
[Keep a Changelog](https://keepachangelog.com/). This file exists to make behavior changes
**loud** — rendering, registry, and output-format changes included — rather than discoverable
only by diffing output. Entries land in the same change as the behavior they describe.

## [Unreleased]

### Fixed

- **A page removed with `Pages.RemoveAt` could still be written by `Save`, in all three layouts.** A full-rewrite `Save` (plain, `Optimize`, `Linearize`) writes every object reachable from the catalog. A removed page stayed reachable through anything that still referenced it: a bookmark, a link, an open action, a form field or widget, a named destination, a structure element. Its `/Parent` then reached the original page tree, whose `/Kids` brought back **every** removed page. Affected `Linearize` output also failed `qpdf --check`, because the resurrected pages broke the hint tables. `Save` now leaves out:
  - the removed pages and the original page-tree nodes;
  - the annotations on removed pages, including form widgets placed on them;
  - form fields whose widgets were all on removed pages, with their values;
  - tagged-PDF structure elements whose content was all on removed pages.

  A reference to any of these is written as `null`. The open document is not changed by `Save`.
  - **`SaveIncremental` keeps a removed page's bytes, by design.** It appends to the original file, which is how a page is dropped from a signed PDF without invalidating the signature. Use `Save` when the page must not survive, or `Pdf.Redact` for content that must be unrecoverable.
  - **Known limitations:** destinations that name a page by integer index, and `/PageLabels` ranges, are not renumbered after a removal.

  - **The saved file is also tidied** of what pointed at a removed page:
    - bookmarks to a removed page are deleted, and their children move up;
    - kept form fields lose the widgets that were on removed pages; radio `/Opt` entries stay aligned with their widgets (when they were), and a `/V` or `/DV` naming a state only removed widgets had becomes `/Off`;
    - the form's `/XFA` is dropped when a field is removed; a `/Perms` entry goes when its signature went, and `/SigFlags` when the last signature field went;
    - a surviving bookmark drops a pruned structure element (`/SE`) and removed fields its submit/reset action listed;
    - links on kept pages, the open action and named destinations that target a removed page are removed;
    - tagged-PDF structure elements whose every content item was on a removed page are pruned, as is one left with no content; `/ParentTree` and `/IDTree` are rebuilt, and an emptied structure tree stays (empty) so the document stays tagged;
    - an emptied outline and emptied named-destination containers are dropped;
    - `PLUME5021` (new, Info) records in `doc.Diagnostics` what each such `Save` left out, once per save.

  Issue #16.
- **`Pdf.Split` and `Pdf.Merge` could copy pages that were not asked for.** Importing a page followed every reference from it, so a link, a pop-up or reply, a widget's `/P`, or a radio group spanning pages pulled another page in, and through it the source's whole page tree. A split part could then contain every page of the document, and merging a document after `RemoveAt` could bring the removed pages back with their form values. Each source is now prepared the way `Save` prepares a document after `RemoveAt`: the pages not imported, and what belonged only to them, are excluded (a reference to them becomes `null` and is not followed), and links, form fields and the other clean-up rules apply to the imported pages. Form merging now decides whether a field sits on a page from the pages the source was opened with, so a field whose page was removed no longer travels as an unplaced field. Each new document records what it left out in a `PLUME5021` entry. Issue #17.

## [1.0.0] — Unreleased (initial public release)

### Added

- **Targets `net8.0` and `net10.0`** — one package, one assembly per target, NativeAOT-safe on
  both. The single runtime dependency, `System.Security.Cryptography.Pkcs`, is matched to each
  runtime (`8.0.1` / `10.0.12`). `PdfOptions.Deterministic` output is byte-identical per runtime;
  across runtimes, Flate-compressed bytes can differ with the runtime's zlib.
- **Reading and manipulation** — `PdfDocument.Open`/`OpenAsync` (file, stream, memory), lenient
  recovery with `doc.Diagnostics`, encryption read, `Pdf.Merge`/`Pdf.Split`, page
  reorder/remove, full-rewrite `Save` and signature-preserving `SaveIncremental`,
  `PdfOptions.Deterministic` byte-identical output.
- **Creation** — `Manuscript` and `PdfDocument.Compose` over a real layout engine (text, rows,
  columns, tables, images, headers/footers, watermarks, stamps); Standard-14 fonts and embedded,
  subset TrueType/OpenType fonts; Arabic and Devanagari shaping (GSUB/GPOS) and UAX #9 bidi;
  the `PLMP0001` analyzer for a composed document that is never rendered.
- **Extraction** — text with positions, words, lines and reading order (structure-tree driven
  when present), image extraction, `/Info` and XMP metadata, advisory permissions.
- **AcroForms** — field reading, `Pdf.FillForm` / `doc.Form.Fill`, real `/AP` appearance
  generation, `Pdf.FlattenForm` / `doc.Form.Flatten`, form-aware merge and split.
- **Digital signatures** — PAdES B-B and B-T signing (`Pdf.Sign`, `doc.Signatures.Add`), B-LT and
  B-LTA maintenance, verification with `/ByteRange` coverage and chain/timestamp trust, and the
  `IPdfSigner` / `ITimestampAuthority` / `IRevocationFetcher` seams (offline by default).
- **Compliance** — PDF/A-1b/2b creation and the `Pdf.ValidatePdfA` self-check (veraPDF is the CI
  oracle), tagged PDF / PDF-UA authoring and read-back, true redaction (`Pdf.Redact`), object-stream
  optimization and linearization, opened-document stamping, typed metadata write.
- **Raster codecs** — PNG/JPEG/TIFF (multi-frame)/CCITT/JBIG2/JPEG 2000 decode and PNG/JPEG
  encode (`RasterImage`), `Pdf.FromImages`; every standard compression and image filter (Flate, LZW, RunLength, ASCIIHex, ASCII85,
  CCITT, JBIG2, DCT, JPX) decodes in the box,
  with `PdfOptions.Filters` as the override seam.
- **Page rasterization** — `Pdf.Rasterize` / `page.Rasterize` on an in-house managed engine: text
  (embedded programs or bundled Liberation/Foxit substitute faces), paths, axial/radial/mesh
  shadings, images and inline images, transparency groups and soft masks, optional content,
  annotation appearances (`RenderAnnotations`, with read-only appearance synthesis), print intent
  (`PrintIntent`), and per-call render intent (`ImageResampling`: `Auto`/`Point`/`Box`/`Bilinear`;
  `AntiAlias`). `Auto` follows PDFium's resampling rule; SSIM parity against PDFium is gated in CI.
- Coded errors throughout: every failure carries a stable `PLUME####` code and an
  `Exception.HelpLink` to its reference page under `docs/errors/`.

[Unreleased]: https://github.com/slash-hug/plumepdf/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/slash-hug/plumepdf/releases/tag/v1.0.0
