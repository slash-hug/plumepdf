# PlumePdf

A fully-featured, high-performance, agent-forward PDF library for .NET, under Apache 2.0.

**1.0.0-preview.** Every feature area below has shipped: reading and manipulation, creation with a real layout engine, extraction, AcroForms, digital signatures, compliance (PDF/A, tagged PDF, true redaction, optimize/linearize), complex-script shaping, raster image codecs, and page rasterization. The complex-script section documents the bidi bounds.

## Names to start with

- **`PdfDocument`** — the hub. `Open` / `OpenAsync` a file, stream, or in-memory buffer; inspect `Objects` (the indirect-object graph) and `Diagnostics` (recoverable deviations found while reading); mutate `Pages` (reorder, remove); `Save` / `SaveAsync` (full rewrite) or `SaveIncremental` / `SaveIncrementalAsync` (append-only update) back out; `PdfDocument.Compose(...)` builds a new document from a fluent lambda.
- **`Pdf`** — one-line task verbs: `Merge` (returns a `PdfDocument`), `Split` (returns a `SplitResult`).
- **`Manuscript`** — the data-first route to a composed document: build a `Section`/`Text`/`Row`/`Column`/`Table`/`Image` element tree directly, then `.Render()`. Use this instead of `PdfDocument.Compose` when you need to build, inspect, or transform the document's structure as data before rendering it.

## Reading and manipulation

| Area | Members |
|---|---|
| Open | `PdfDocument.Open(string)`, `Open(Stream)`, `Open(ReadOnlyMemory<byte>)`, and `OpenAsync` variants |
| Objects | `doc.Objects` — indexer by `IndirectReference`, `Trailer` |
| Diagnostics | `doc.Diagnostics` — recoverable read deviations, never silent, never a throw for what's recoverable |
| Save | `doc.Save(...)` / `SaveAsync(...)` — full rewrite; honors `PdfOptions.Deterministic` |
| SaveIncremental | `doc.SaveIncremental(...)` / `SaveIncrementalAsync(...)` — append-only update; the default recommendation for most edits |
| Merge | `Pdf.Merge(...)` |
| Split | `Pdf.Split(...)` |
| Pages | `doc.Pages` — reorder and remove |

## Creation

| Area | Members |
|---|---|
| Compose | `PdfDocument.Compose(page => ...)` — fluent lambda veneer (`Header()`/`Content()`/`Footer()`, `Row`/`Column`/`Text`/`Image`/`Table`, `Watermark`/`Stamp`) |
| Manuscript | `Manuscript { Sections = [...] }.Render(PdfOptions?)` — the same document tree as data |
| Elements | `Text`, `Row`, `Column`, `Table`, `Image`, `PageBreak`, `Watermark`, `Stamp` (`PlumePdf.Elements`) |
| Fonts | `PdfFont.Helvetica` and its Standard-14 siblings (no embedding); `PdfFont.FromFile(path)` / `FromBytes(bytes)` to embed and subset a TrueType/OpenType font, assigned via `Text.Font` |
| Analyzer | `PLMP0001` — a build-time Roslyn analyzer flagging a composed `Manuscript`/`PdfDocument.Compose` result that's never rendered/saved |

## Extraction

| Area | Members |
|---|---|
| Text | `Pdf.ExtractText(path)` (flattened string, the quick door); `doc.Pages[i].ExtractText(PdfTextExtractionOptions?)` — positions, reading-order `Lines`/`Words`, and the raw `Letters` escape hatch |
| Images | `doc.Pages[i].ExtractImages()` / `ExtractImagesWithDiagnostics()` — every image XObject reachable from the page, recursing into forms |
| Metadata | `doc.GetInfo()` (typed `/Info` dictionary), `doc.GetXmpMetadataBytes()` / `GetXmpMetadataText()` (raw XMP packet), `doc.Permissions` (advisory `/Encrypt` `/P` flags) |
| Extension seam | `PdfOptions.Filters` — register your own `IPdfFilter` for a filter PlumePDF doesn't ship a decoder for, or to override one it does (CCITT/JBIG2/DCT/JPX are all built in by default — see the raster codecs section below) |

## AcroForms

| Area | Members |
|---|---|
| Read | `doc.Form.Fields` — enumerable field model (`Name`, `FullName`, `FieldType`, `Value`, `AllowedValues`, `Checked`, `TryGetValue`); indexer accepts exact fully-qualified name or unique trailing-segment short name; `PdfForm.For(document)`; `doc.HasEncryptedSource` |
| Fill | `Pdf.FillForm(path, values)` / `Pdf.FillForm(path, params (name, value))` / `Pdf.FillFormAsync(...)`; `doc.Form.Fill(values)`; `doc.Form.Fields[name].Value = ...` — every route regenerates the widget's `/AP` appearance (`PdfOptions.NeedAppearances` is the escape hatch) and persists via signature-preserving `doc.SaveIncremental(...)` |
| Flatten | `doc.Form.Flatten()`; `Pdf.FlattenForm(path, outputPath, options)` — bakes each widget's appearance into page content and removes the AcroForm (a widget with no `/AP` gets one synthesized from its field value; one with nothing synthesizable stays live with a `PLUME6036` Warning diagnostic) |
| Merge | `Pdf.Merge`/`Pdf.Split` carry `/AcroForm` through with field union, deterministic collision renaming, and per-part field filtering |

## Digital signatures

| Area | Members |
|---|---|
| Sign | `Pdf.Sign(path, outputPath, PdfSignOptions)` / `SignAsync(...)`; `doc.Signatures.Add(outputPath, PdfSignOptions)` / `SignAsync(...)` — PAdES B-B (baseline) and B-T (+ RFC 3161 timestamp); `IPdfSigner` is the HSM/KMS seam, `Certificate` (an `X509Certificate2` with a private key) the local-key convenience door |
| Verify | `Pdf.Verify(path, trustedRoots?)`; `doc.Signatures[i].Verify(trustedRoots?)` → `SignatureVerificationResult` — cryptographic status, `/ByteRange` coverage, and (when trust anchors are supplied) certificate-chain trust and timestamp trust |
| LTV maintenance | `doc.Signatures.AddLtvAsync(outputPath, IRevocationFetcher, trustedRoots?)` (embeds a `/DSS` of OCSP/CRL revocation material, B-LT); `doc.Signatures.AddDocumentTimestampAsync(outputPath, ITimestampAuthority)` (a standalone `/DocTimeStamp` revision, B-LTA) |
| Network seams | `IPdfSigner`, `ITimestampAuthority`, `IRevocationFetcher` — every network/HSM call goes through a caller-supplied implementation; PlumePDF never calls out on its own (offline by default). `IO.Http.HttpTimestampAuthority`/`HttpRevocationFetcher` are the in-box HTTP-backed implementations |
| Determinism | `PdfOptions.Deterministic` extends to signing for RSA PKCS#1 v1.5 at PAdES B-B with a caller-supplied `PdfSignOptions.SigningTime` and no TSA/LTV — byte-identical output across runs; every other combination (ECDSA, a TSA token, a wall-clock signing time) is refused rather than silently non-reproducible |

## Compliance

| Area | Members |
|---|---|
| PDF/A create | `PdfOptions.PdfAConformance` (`A2b` primary, `A1b` via the PDF 1.4 version knob) on `Manuscript.Render`/`PdfDocument.Compose` — writes the `pdfaid` XMP identification (agreeing with `/Info`), attaches a `GTS_PDFA1` output intent (bundled CC0 sRGB profile, overridable via `PdfAOutputIntentProfile`), and refuses any non-embedded Standard-14 font (`PLUME8023`); `Manuscript.Title`/`CreateDate`/`ModifyDate` supply the metadata |
| PDF/A validate | `Pdf.ValidatePdfA(path)` / `PdfAValidator.Validate(doc)` → `PdfAValidationResult` — an honestly-bounded 1b/2b structural self-check (per-rule `Pass`/`Fail`/`NotChecked` findings, never a silent coverage gap); the veraPDF CLI is the full-coverage oracle |
| Tagged PDF / PDF-UA | Opt in by setting `Manuscript.Language` — emits marked content and a real `/StructTreeRoot`; element types carry structural roles for free, caller supplies semantics (`Text.HeadingLevel`, `Image.AltText`, `Element.Role`, `Manuscript.Title`); missing required semantics is a coded refusal (`PLUME9010`), never best-effort auto-tagging; `PdfStructureInfo.For(doc)` reads a structure tree back, and extraction reading order follows a parseable `/StructTreeRoot` automatically |
| Redact | `Pdf.Redact(path, outputPath, targets)` / `doc.Redact(targets, PdfRedactOptions?)` → `RedactionResult` — true removal (content-stream operators deleted, intersecting images — XObject and inline — removed whole, region-intersecting annotation appearances wiped, DocInfo/XMP/annotation-`/Contents`/outlines/embedded-file attachments/structure-tree `/ActualText`+`/Alt`/`/PieceInfo` scrubbed), proven unrecoverable by the exit-demo suite; zero matches is loud in the result; `SaveIncremental` after a redaction refuses (`PLUME5016`) — only `Save`'s full rewrite removes the bytes; signed sources refuse unless `AllowInvalidatingSignatures` removes the signature fields, their widgets, and `/Perms`/`/DSS` so the output is honestly unsigned |
| Optimize / Linearize | `PdfOptions.Optimize` (object streams + cross-reference stream, smaller files, PDF 1.5+) and `PdfOptions.Linearize` / `Pdf.Linearize(path, outputPath)` ("fast web view", ISO 32000-1 Annex F, qpdf-verified) — mutually exclusive in v1.0; a later `SaveIncremental` de-linearizes with a recorded `PLUME5019` diagnostic |
| Stamp (opened documents) | `Pdf.Stamp(path, outputPath, stampOrText)` / `doc.Stamp(Stamp, pageIndexes?)` — paints the same `Stamp` descriptor `Section.Stamps` uses onto an already-opened document; purely additive, so it works through signature-preserving `SaveIncremental` |
| Metadata write | `doc.SetInfo(DocInfoMetadata)` / `doc.SetXmpMetadata(XmpPacket)` — typed `/Info` and XMP write with cross-agreement enforced (`PLUME6057`) |

## Complex-script shaping surface

| Area | Members |
|---|---|
| Direction / alignment | `Text.Direction` (`Auto`/`LeftToRight`/`RightToLeft` — `Auto` applies UAX #9's first-strong heuristic); `HorizontalAlign.Start`/`End` resolve to the physical right/left edge from the paragraph's resolved direction (`Left`/`Right` stay physical forever) |
| Arabic / Devanagari shaping | Automatic once an Arabic- or Devanagari-capable embedded font (real `GSUB`/`GPOS`, e.g. Noto Naskh Arabic / Noto Sans Devanagari) is assigned via `Text.Font` — joining forms, ligatures, and GPOS mark/cursive placement (harakat, matras, anusvara) are shaped and painted with real per-glyph offsets; a font missing the required capability refuses (`PLUME8025`) rather than silently drawing unjoined isolated forms |
| Bidi | UAX #9 implicit resolution (paragraph direction, weak/neutral/implicit rules, visual reorder, bracket mirroring). Explicit embedding/override controls (LRE/RLE/LRO/RLO/PDF) are handled approximately: they get correct embedding levels (X1–X8), so reordering around them is correct, but weak/neutral resolution runs as one flat pass over the paragraph rather than per X10 isolating-run sequence, the control characters themselves are dropped before shaping, and the UCD conformance gate excludes cases containing them — full X10 isolating-run-sequence construction is 1.x. Explicit isolates (LRI/RLI/FSI/PDI) are a coded refusal (`PLUME9013`), never approximated |
| Shaping budget | `PdfOptions.MaxShapingLookupApplications` — a work budget guarding against a hostile or pathological caller-supplied font (`PLUME8024`) |
| Oracle parity | Every curated Arabic/Devanagari fixture — including Arabic's lam-alef ligature and Devanagari reph ("र्क")/anusvara-over-multi-letter-base positioning — matches the reference HarfBuzz shaper, gated in both the hermetic and the live `hb-shape` oracle lanes; one known RTL limitation (GPOS advance deltas applied on the kern slot) is documented at its implementation site |

## Raster codecs and image → PDF

| Area | Members |
|---|---|
| Decode | `RasterImage.Decode(bytes\|path, PdfOptions?)` — sniffs PNG/JPEG/TIFF/JPEG 2000 (`.jp2`/`.j2k`) and decodes to one or more `RasterImageFrame`s (`Frames`, result-scoped `Diagnostics`); a multi-frame TIFF degrades per-frame (a bad frame is skipped and diagnosed, not fatal) |
| Frame | `RasterImageFrame` — `Width`/`Height`/`Format` (`RasterPixelFormat.Gray8`/`Rgb24`/`Rgba32`, always 8 bits/channel), `XDpi`/`YDpi`, `Pixels`; `EncodePng()`/`EncodeJpeg(quality)` |
| Image → PDF | `Pdf.FromImages(paths\|bytes\|RasterImage, outputPath?, PdfOptions?)` — one page per source image, sized from its own DPI |
| Resource caps | `PdfOptions.MaxImagePixels`/`MaxImageFrames`/`MaxJbig2Segments`/`MaxJbig2Symbols` — the same per-hazard decompression-bomb-guard convention as every other `PdfOptions.Max*` cap |
| Built-in filters | `CCITTFaxDecode`/`CCF`, `JBIG2Decode`, `DCTDecode`/`DCT`, and `JPXDecode` (an in-house JPEG 2000 Part 1 decoder) are all registered by default on `PdfFilterRegistry.Default` — every ISO 32000-1 §7.4 compression and image filter ships in the box; `PdfOptions.Filters` remains the seam for a vendor filter PlumePDF has never heard of, or to override any of the built-ins |

## Page rasterization

An in-house, pure-managed rasterizer (NativeAOT-safe, no native dependency): text (embedded TrueType/CFF/Type 1 programs, or a bundled substitute face), vector paths, shadings (axial/radial and mesh types 4–7), images and inline images, transparency groups and soft masks, optional content, and annotation appearances — measured against PDFium by an SSIM oracle gate in CI.

| Area | Members |
|---|---|
| Rasterize | `Pdf.Rasterize(path, PdfRasterizeOptions?)` / `RasterizeAsync(...)` — whole-document verb, page selection via `PdfRasterizeOptions.PageIndices` (first page only by default); `doc.Pages[i].Rasterize(PdfRasterizeOptions?)` — single already-open page, always exactly one frame |
| Options | `PdfRasterizeOptions` — target size (`PixelWidth`+`PixelHeight` XOR `Dpi`, default 96 DPI), background (`RasterColor`/`TransparentBackground`), annotations and print intent: `RenderAnnotations` (paints every visible annotation's `/AP` appearance, synthesizing missing form-field appearances without mutating the document) and `PrintIntent` (the `Print`/`NoView` flag matrix and optional-content print usage); per-call render intent: `ImageResampling` (`ImageResamplingMode.Auto`/`Point`/`Box`/`Bilinear`, default `Auto` = PDFium parity) and `AntiAlias` (default `true`) |
| Resource caps | `PdfOptions.MaxRasterSurfaceBytes`/`MaxDisplayListObjects`/`MaxShadingSamples` — the same per-hazard resource-limit-guard convention as every other `PdfOptions.Max*` cap |
| Substitute fonts | Non-embedded text falls back to one of fourteen bundled substitute faces, compiled directly into this package as FieldRVA blobs (no separate asset download, no reflection-based loading, NativeAOT-safe by construction): twelve Liberation Sans/Serif/Mono faces matched by declared bold/italic/serif/fixed-pitch (`NOTICE`'s Liberation Fonts entry), plus PDFium's own Foxit Symbol/Dingbats faces for the Standard-14 `Symbol`/`SymbolMT`/`ZapfDingbats` fonts (`NOTICE`'s Foxit entry). CJK-named fonts have no bundled substitute yet (`PLUME7511`) — see `docs/cookbook/rasterize-substitute-fonts.md`. |

## Errors

Every failure carries a stable `Code` (`PLUME####`) and an `Exception.HelpLink` to that code's reference page. Start at the [error-code index](https://github.com/slash-hug/plumepdf/blob/main/docs/errors/README.md).

## Provenance

PlumePdf is implemented clean-room, from ISO 32000 and permissively-licensed prior art only — never from AGPL or commercial PDF library source. Every port from a permissive source is attributed in `NOTICE`.

## Docs

Full docs, cookbook, and agent guide (`AGENTS.md`): https://github.com/slash-hug/plumepdf
