# Changelog

All notable behavior changes to PlumePDF are recorded here, in the style of
[Keep a Changelog](https://keepachangelog.com/). This file exists to make behavior changes
**loud** — rendering, registry, and output-format changes included — rather than discoverable
only by diffing output. Entries land in the same change as the behavior they describe.

## [Unreleased]

### Added

- **`Pdf.Compress` and `Pdf.CompressAsync` make a document smaller.** With no options the call
  is lossless: unfiltered streams are Flate-compressed, objects are packed into object streams
  with a cross-reference stream, and byte-identical streams are written once. Images change
  only when asked: `PdfCompressOptions.Images` with `MaxDpi` downsamples images whose effective
  DPI is above 1.5 × `MaxDpi` to `MaxDpi` (box filter, per axis, never upsampling), and
  `JpegQuality` re-encodes 8-bit gray and RGB images (ICC profiles kept) as baseline JPEG,
  colour with 4:2:0 chroma subsampling. With only `MaxDpi`, a JPEG keeps its own estimated
  quality (85 when it can't be estimated, `PLUME6091`) and its full-resolution chroma when it
  had it. A Flate image's `/Decode` array is applied to its samples when it becomes a
  JPEG, so it renders the same in every viewer. Every rewritten stream follows
  the never-larger rule. The returned `CompressionResult` gives input and output sizes and one
  `ImageCompressionDecision` per image (`Action`, `SkipReason`, sizes, effective DPI before and
  after, and the encoding family read and written, `EncodingBefore`/`EncodingAfter` as the new
  `ImageEncoding` enum, #53, so a caller tuning `JpegQuality`/`MaxDpi` sees which images were
  already JPEG), plus result-scoped summary diagnostics with counts (`PLUME6085`–`PLUME6091`), never
  one entry per image and nothing on `doc.Diagnostics`. Bilevel and stencil images, 2/4/16-bit
  images, colour spaces other than gray and RGB, JPEG 2000/JBIG2/CCITT, inline,
  tiling-pattern-only, colour-key masked and `/Matte` soft-mask images are skipped with a
  reason. An image that fails to decode is written unchanged with `PLUME6086`, never a
  refusal, even under `PdfOptions.Strict`. Memory is bounded by the largest image (about five
  times its decoded size), and output goes through the usual temp-file-then-move save, so a
  failure or cancellation leaves no partial file. Refused before any work: a signed source
  unless `AllowInvalidatingSignatures` strips it (new `PLUME6084`; the count is
  `CompressionResult.SignaturesStripped`), an encrypted source
  (`PLUME5001`), `PdfOptions.Linearize` (`PLUME5018`), and same-path output over 2 GiB
  (`PLUME1006`); under `PdfOptions.Strict`, a reference the document can't resolve refuses the
  save with `PLUME2060`, as `Save` does. A version below 1.5 is raised to 1.5 for packing (`PLUME6088`); a PDF/A-1
  source is not packed (`PLUME6089`) and keeps a 1.4 header, which PlumePDF's own PDF/A self-check requires, unless the caller sets
  a version other than the default 1.7 or a `PdfAConformance`. Progress reporting is not
  available. Recipe: `docs/cookbook/compress.md`.
- **`Pdf.Compress(Stream, Stream, ...)` and `Pdf.CompressAsync(Stream, Stream, ...)` compress
  from one stream to another** (#47), with the same options, levers, `CompressionResult` and
  refusals as the path overloads, and byte-identical output under `PdfOptions.Deterministic`.
  A seekable input is read in place from position 0; a non-seekable one (an HTTP response, a
  blob download) is copied to a temporary file in `Path.GetTempPath()`, never into memory, and
  deleted when the call ends, on success, failure or cancellation (asynchronously, with the
  token, in `CompressAsync`). The output need not be seekable: writing starts at its current
  position and the PDF's offsets count from there. It is flushed and, like the input, never
  disposed. `InputBytes` is the stream's length or the bytes copied; `OutputBytes` is the bytes
  written. Null streams throw `ArgumentNullException`; an unreadable input, an unwritable
  output, or the same stream for both throw `ArgumentException`. A caller's stream can't be
  written atomically: every refusal (`PLUME5018` before the input is read, `PLUME5001`,
  `PLUME6084`) comes before the first byte, but a failure or cancellation while writing can
  leave a partial output for the caller to discard. With this the compression work is
  complete; #48–#54 are optional enhancements.

### Fixed

- **Opening a PDF through a symbolic link reads the file it points to.** The default
  memory-mapped open took its length from the path's `FileInfo`, which for a link is the
  link's own few bytes, so the document failed to open or recover. The length now comes from
  the opened file. A link whose target is missing is now `PLUME1002`; it used to escape as an
  uncoded `FileNotFoundException`. `Pdf.Compress` also reported a link's own size as
  `InputBytes`; it now reports the opened file's size.

- **3-component JPEGs marked "no colour transform" decoded as YCbCr.** A JPEG whose Adobe
  `APP14` marker says transform 0, or whose PDF `/DecodeParms` say `/ColorTransform 0`, stores
  its samples as RGB, but PlumePDF read them as RGB only when the component ids were also
  `R`, `G`, `B`; with the usual ids 1, 2, 3 the colours came out wrong (a red of 220/30/40
  rendered as 97/255/46). An explicit transform 0 now means RGB whatever the ids say, as
  libjpeg, poppler and pdf.js read it; the id check is used only when neither the marker nor
  `/ColorTransform` says anything. This changes the pixels `Rasterize` and
  `RasterImage.Decode` give for such files, `ExtractImages`' decoded samples for one whose
  `DCTDecode` isn't the last filter (a JPEG that ends its filter chain is still returned as its
  own bytes, unchanged), and the colours `Pdf.Compress` writes when it re-encodes or
  downsamples them.
- **`Redact` with `AllowInvalidatingSignatures` left `/SigFlags` behind.** When the signed
  document's form had other fields, the stripped output kept its `/AcroForm` (correctly) along
  with `/SigFlags`, which still said the document held signatures, so viewers could treat the
  unsigned output as signed (append-only). `/SigFlags` is now removed once no signature field
  is left in the form. `Pdf.Compress`'s `AllowInvalidatingSignatures` strips the same way.
- **Streams whose `/Length` is an indirect reference read the rest of the file.** Below 2 GiB,
  resolving such a stream (common in Ghostscript, pdfTeX and scanner output) read from its
  offset to the end of the file — a 7.5 MB image in a 196 MB file allocated 642 MB; past 2 GiB
  it threw `OverflowException`, so `Save` and `ExtractImages` failed. A stream is now read once
  at its declared size, with an indirect `/Length` resolved through the cross-reference table
  first; the growing search for the stream's end is kept only for a missing or wrong `/Length`.
  When that search would need more than one array (~2 GiB), the object is reported as the coded
  `PLUME2061`, citing the limit, instead of an `OverflowException` failing the whole read. The
  same 7.5 MB image now allocates about 15 MB. Memory-mapped reads also copy through a fixed
  64 KiB buffer instead of renting a pooled buffer the size of each read.
- **`Save` kept every stream it wrote in memory.** A full-rewrite `Save` resolved every
  reachable object through the open document's cache, so each stream payload it wrote stayed
  in memory for the document's lifetime — saving a 2.67 GB file peaked at over 3 GB of managed
  heap and left 2.5 GB retained afterwards, with or without `PdfOptions.Optimize`. `Save` now
  finds a stream's references from its dictionary alone, reads its payload only to write it,
  and lets it go; streams the caller has already resolved, and may have edited in place, are
  still written from that same instance. The same save now peaks under 80 MB of heap and
  retains 2 MB, and its output is byte-for-byte the same as before. A stream's parse deviations
  are still reported once, however many times it is read.

### Changed

- **File-system failures in PlumePDF's own file IO now throw `PlumePdfException` with a code,
  not a bare `IOException` (#54).** Writing an output file to a path — `Save`, `SaveIncremental`,
  `Pdf.Compress` and the other path verbs, signing, LTV and document timestamping — used to let
  `DirectoryNotFoundException`, `IOException` (the path is a directory, the disk is full) and
  `UnauthorizedAccessException` (no write permission) escape uncoded; they are now
  `PLUME1007`, naming the output path, with the original exception as `InnerException`. As
  before, no temporary file is left behind and an existing destination is untouched. Spooling a
  non-seekable input for `Pdf.Compress(Stream, Stream)` to the temp directory now fails with
  `PLUME1008` instead (nothing is written to the output and no spool file is left). Opening a
  path with `PdfOptions.PreferStreamIo` (sync and `OpenAsync`), and the stream fallback after a
  file can't be memory-mapped, now report a missing file or directory as `PLUME1002` and any
  other file-system failure as `PLUME1001`, the codes the default memory-mapped open already
  used, instead of a raw `FileNotFoundException`/`UnauthorizedAccessException`.
  **`PlumePdfException` derives from `Exception`, not `IOException`, so a `catch (IOException)`
  around these calls no longer catches these failures** — catch `PlumePdfException` (and check
  `Code`, or `InnerException` for the operating-system error). Exceptions thrown by your own
  streams (the `Compress(Stream, Stream)` input and output, `PdfDocument.Open(Stream)`) and
  cancellation are unchanged.
- **An indirect `/Length` no longer reports `PLUME2019`.** Every stream with an indirect
  `/Length` used to be recovered by scanning for `endstream`, recording `PLUME2019` and, under
  `PdfOptions.Strict`, refusing the document. The resolved length is now used directly, so these
  streams parse without a diagnostic and are accepted under `Strict`, and a payload that happens
  to contain the bytes `endstream` is no longer cut short. `PLUME2019` now means the `/Length`
  was missing, unresolvable or wrong.

## [1.0.0-preview.8] — 2026-10-14

Version numbers `1.0.0-preview.3` to `1.0.0-preview.7` are skipped: builds labelled with them
circulated before the public repository existed, and NuGet would rank them above a lower number.

### Security

- **`System.Security.Cryptography.Pkcs` updated to `8.0.2` (`net8.0`) and `10.0.13` (`net10.0`).**
  The previous pins, `8.0.1` and `10.0.12`, are affected by two .NET denial-of-service
  advisories, [CVE-2026-84008](https://github.com/advisories/GHSA-846f-f764-76mx) (high) and
  [CVE-2026-70162](https://github.com/advisories/GHSA-2fmv-rv2c-59x7) (moderate). PlumePDF uses
  this package to parse the CMS signature data embedded in PDFs (`Pdf.Verify`,
  `doc.Signatures`), which comes from the document being read. Both new versions keep zero
  transitive dependencies on their target.

### Fixed

- **A linearized file larger than 2 GiB could not be opened.** `PdfDocument.Open` read each
  cross-reference section from its offset to the end of the file, and a linearized file's first
  section sits near the start, so this read the whole file: past 2 GiB it failed, and the
  fallback brute-force scan then threw a bare `OutOfMemoryException`. Sections are now read
  through a window that grows only as far as the section needs, so opening a linearized file
  no longer reads it whole at any size. A brute-force scan that cannot fit in one array is now
  the coded `PLUME1005`.

## [1.0.0-preview.2] — 2026-09-29

### Added

- **Targets `net8.0` and `net10.0`.** One package, one assembly per target, NativeAOT-safe on
  both (preview.1 targeted `net8.0` only). The single runtime dependency,
  `System.Security.Cryptography.Pkcs`, is matched to each runtime (`8.0.1` / `10.0.12`).
  `PdfOptions.Deterministic` output is byte-identical per runtime; across runtimes,
  Flate-compressed bytes can differ with the runtime's zlib.
- **`PLUME5021` (Info).** After `Pages.RemoveAt`, `Save` records in `doc.Diagnostics` what it left
  out that pointed at removed pages (bookmarks, fields, links, …); `Pdf.Split` and `Pdf.Merge`
  record the same for pages they were not given.

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
- **`Pdf.Merge` with the same document (or page) twice produced one copy of each page.** Each source object was copied once per document, so a repeated page reused the first copy and the page tree listed the same page twice (read back as 3 pages instead of 6). Each repeated occurrence is now copied on its own, and its form fields become independent copies renamed like any cross-document field-name collision (`name` → `name~2`). Issue #22.
- **`Linearize`: a bookmark whose action lists form fields gave an outline hint table qpdf rejects** ("incorrect object count"). The outline group now includes everything the outline reaches, form fields a submit/reset action names included, which is how qpdf counts it; the objects were already placed together. With `/PageMode /UseOutlines` (outline group in the first page's section), a later page that reaches those objects now lists them as shared, so its page hint entry is complete. Issue #19.
- **A malformed `/AcroForm` (or `/Info`) naming a removed page brought that page back on `Save`** (plain and `Optimize`), and made `Linearize` fail with an internal writer error (`PLUME5010`). The rule that keeps those dictionaries out of the removed set no longer applies to a page-tree node, and a full rewrite no longer writes a trailer `/Info` that is excluded, so the removed page stays out in every layout. As a safeguard, the linearizer also places any form-hierarchy object no other part claimed in part 9, instead of failing. Issue #20.

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

[Unreleased]: https://github.com/slash-hug/plumepdf/compare/v1.0.0-preview.8...HEAD
[1.0.0-preview.8]: https://github.com/slash-hug/plumepdf/releases/tag/v1.0.0-preview.8
[1.0.0-preview.2]: https://github.com/slash-hug/plumepdf/releases/tag/v1.0.0-preview.2
[1.0.0]: https://github.com/slash-hug/plumepdf/releases/tag/v1.0.0
