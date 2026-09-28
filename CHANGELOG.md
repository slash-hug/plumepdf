# Changelog

All notable behavior changes to PlumePDF are recorded here, in the style of
[Keep a Changelog](https://keepachangelog.com/). This file exists to make behavior changes
**loud** — rendering, registry, and output-format changes included — rather than discoverable
only by diffing output. Entries land in the same change as the behavior they describe.

## [Unreleased]

## [1.0.0] — Unreleased (initial public release)

### Added

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
