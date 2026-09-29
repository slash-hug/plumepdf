---
name: plumepdf
description: Use when reading, merging, splitting, reordering, saving, composing, inspecting, extracting text/images/metadata, filling/flattening AcroForms, signing/verifying digital signatures, creating/validating PDF/A, authoring tagged PDF, redacting, optimizing/linearizing, or stamping with the PlumePdf library (namespace PlumePdf), or when a PlumePdfException / PLUME#### error code appears. Covers the Phase 1 (read/manipulate), Phase 2 (creation), Phase 3 (extraction), Phase 4 (AcroForms: fill, flatten, real appearance generation), Phase 5 (digital signatures: sign, verify, LTV), and Phase 6 (compliance: PDF/A create+validate, tagged PDF/PDF-UA, true redaction, optimize/linearize, opened-document stamping) surfaces.
---

# Using PlumePDF

Two names start most reading/manipulation tasks:

- **`PdfDocument`** — the hub. `PdfDocument.Open(path | Stream | ReadOnlyMemory<byte>, PdfOptions?)` (plus `OpenAsync`); instance surface: `Pages` (count/index/`Move`/`RemoveAt`), `Diagnostics`, `Objects` (raw object-graph escape hatch), `Save`/`SaveAsync`, `SaveIncremental`/`SaveIncrementalAsync`, `HasEncryptedSource`. `PdfDocument.Compose(page => ...)` builds a new document from a fluent lambda.
- **`Pdf`** — one-line task verbs: `Pdf.Merge(paths… | documents…)`, `Pdf.Split(document)` → `SplitResult.SaveAll("part-{n}.pdf")` (plus `MergeAsync`/`SplitAsync`).

A third name matters once you're *creating* a document as data rather than a one-shot lambda:

- **`Manuscript`** — the data-first route: build a `Section`/`Text`/`Row`/`Column`/`Table`/`Image` element tree directly (object initializers), then `manuscript.Render(PdfOptions?)`.

## Compose vs Manuscript (the second naming trap)

Mirrors the `Save`/`SaveIncremental` trap below: two ways to build a document, and picking wrong isn't wrong exactly, just needlessly awkward.

- **Use `PdfDocument.Compose`** for the common case — a page described once, rendered once, nothing held onto afterward. `Compose` builds a `Manuscript` internally; you never see it.
- **Use `Manuscript` directly** when you need to build, inspect, or transform the document's structure *as data* before rendering — assembling a tree programmatically from a loop, sharing one `Section` across documents, unit-testing the tree shape, or serializing a description of a document. `PdfFont.FromFile("x.ttf")` names a font either way; assign it via `Text.Font` (Manuscript) or `.Text("...").Font(font)` (Compose).

Both end the same way: `.Render()` (Manuscript) or the `PdfDocument` `Compose` returns must eventually be `.Save(...)`/`.SaveIncremental(...)`d — the `PLMP0001` analyzer (build-time warning) catches the common mistake of composing a document and never calling either.

Copy-paste recipes with verified expected output: `docs/cookbook/` (one task per file). Each is backed by a test — run `dotnet test PlumePdf.sln --filter CookbookTests` and diff `.received` vs `.verified` to prove usage.

## Save vs SaveIncremental (the naming trap)

- **`SaveIncremental` is the default choice**: appends changes after a copy of the original bytes; prior revisions stay intact (required for signed files); fastest.
- **`Save`** is a full rewrite: renumbers objects, garbage-collects unreferenced ones. Output is NOT byte-comparable to the input — only run-to-run reproducible under `Deterministic`.
- Saving a document whose source was encrypted throws `PLUME5001`/`PLUME5002` (no encryption write in Phase 1); `Pdf.Merge`/`Split` on such a source throws `PLUME6012`.
- **After `Redact`, only `Save` is legal** — `SaveIncremental` refuses (`PLUME5016`): an incremental update preserves the original bytes redaction exists to remove. **After a `Linearize`d save**, `SaveIncremental` de-linearizes and records a `PLUME5019` diagnostic (`Strict` refuses). **Stamping is the additive exception**: `doc.Stamp` works through `SaveIncremental` by design. **After `Pages.RemoveAt`**, only `Save` drops the removed pages: it leaves out everything that belonged only to them (annotations, widgets, fields left with no widget, structure elements) and tidies bookmarks, links, the open action, named destinations and the structure tree that pointed at them, recording a `PLUME5021` Info diagnostic; `SaveIncremental` keeps their bytes. Recipe: `docs/cookbook/reorder-pages.md`.

## Creation (Phase 2): elements, fonts, layout errors

`Text`/`Row`/`Column`/`Table`/`Image`/`PageBreak` (`PlumePdf.Elements`), plus `Section.Watermark`/`Section.Stamps`, build the tree either form composes. Fonts: `PdfFont.Helvetica` and its 13 Standard-14 siblings need no embedding; `PdfFont.FromFile("x.ttf")`/`FromBytes(bytes)` embeds and subsets a TrueType/OpenType font to only the glyphs actually drawn. Text with no glyph in the resolved font throws `PLUME8009` naming the codepoint and font — no silent tofu, no fallback font; embed a font that covers the character. A degenerate tree (empty `Compose` lambda, an element that can't fit the space it's given) throws `PdfLayoutException` (`PLUME9###`) naming the element path and measurements, not a generic message.

## Extraction (Phase 3): text, images, metadata

- **Text** — `Pdf.ExtractText(path)` is the quick door (one flattened string). `document.Pages[i].ExtractText(PdfTextExtractionOptions?)` is the rich door: `ExtractedText.Text`/`Lines`/`Words` (reading order — content-order baseline plus a two-column gutter heuristic) down to the raw `Letters` escape hatch (content-stream order, positioned in page space: points, bottom-left origin, y-up, post-`/Rotate`). An image-only/scanned page returns an empty result, not an error or a throw — PlumePDF does no OCR.
- **Images** — `document.Pages[i].ExtractImages()`/`ExtractImagesWithDiagnostics()` walk every image XObject reachable from the page (recursing into forms). DCT passes through as intact JPEG bytes; Flate/LZW/RunLength decode to raw samples; CCITT/JBIG2/JPX all decode too (in-house, registered by default); only a filter name PlumePDF has never registered a decoder for degrades to still-encoded bytes plus a `PLUME6023` diagnostic — register your own `IPdfFilter` via `PdfOptions.Filters` to decode it instead (the extension seam; see `docs/cookbook/extract-images.md`).
- **Metadata** — `document.GetInfo()` (typed `/Info` dictionary fields), `document.GetXmpMetadataBytes()`/`GetXmpMetadataText()` (raw XMP packet, no RDF parsing), `document.Permissions` (advisory `/Encrypt` `/P` flags only — never enforced).
- Extraction diagnostics are **result-scoped**, not `document.Diagnostics` — check `ExtractedText.Diagnostics`/the `ExtractImagesWithDiagnostics` tuple for what THIS call tolerated; `document.Diagnostics` still gets one first-occurrence summary entry per page per extraction call, so a caller who only ever checks `document.Diagnostics` still sees that something happened.
- Recipes: `docs/cookbook/extract-text.md`, `extract-images.md`, `read-metadata.md`.

## Forms (Phase 4): fill, flatten

- **`doc.Form.Fields`** — the read/discovery door. Enumerable (`Name` short, `FullName`
  fully-qualified, `FieldType`, `Value`, `AllowedValues` — a checkbox/radio field's on-state
  names, discovered from its `/AP /N` appearance dictionary, never assumed to be `/Yes`).
  Indexer lookup accepts either the exact fully-qualified name (real forms use UTF-16BE
  hierarchical paths like `topmostSubform[0].Page1[0].c1_01[0]`) or a short name — if the
  short form matches more than one field's trailing name segment, it throws a coded exception
  naming every candidate rather than silently filling the wrong field.
- **`Pdf.FillForm(path, values)`** — the one-line verb: `IEnumerable<KeyValuePair<string, string>>`
  or a `params (string Name, string Value)[]` overload, writing via `SaveIncremental` (the
  signature-preserving path). `doc.Form.Fields[name].Value = "..."` is the model-door
  equivalent when you want to inspect a field before deciding what to set. Anonymous-object
  field values (`FillForm(path, new { Name = "..." })`) were considered and dropped — anonymous
  types can't implement interfaces and the reflection shape that would require is exactly what
  `PlumePdf.ArchitectureTests.ReflectionBanTests` mechanically forbids anywhere in `src/`; only
  these explicit overloads exist (a `[FormModel]`-attribute source generator over named partial
  types is 1.x backlog).
- **Appearance generation** — `PdfForm.Fill`/`Pdf.FillForm` regenerates each filled field's
  `/AP /N` normal-appearance stream itself (through the Fonts/Content layers — `/DA` font,
  size, color, multiline/comb layout), so filled fields render correctly in any viewer without
  relying on `/NeedAppearances`. Set `PdfOptions.NeedAppearances = true` (the options a
  document is opened with) to opt back into the viewer-side-regeneration escape hatch instead
  — fill-time only, and `Flatten` ignores it since it always needs a real stream to bake in.
  When regeneration genuinely can't produce a stream for a field (e.g. an unencodable
  character in the fill value against the field's `/DA` font), that field's appearance is left
  stale and a `PLUME6041` diagnostic records it. `doc.Form.Flatten()` bakes each widget's
  *existing* `/AP` into page content; a widget with no `/AP` at all throws `PLUME6036` rather
  than silently producing an invisible field. See `docs/errors/PLUME6041.md` and
  `docs/errors/PLUME6036.md`.
- **`doc.Form.Flatten()`** — stamps each field's current appearance permanently into page
  content and removes the AcroForm entirely. Returns a **new, independent `PdfDocument`** —
  it never mutates the source document in place (capture the return value; the source still
  has its AcroForm afterward, and `Flatten()`'s own doc comment explains why: no general
  object-number allocator on an already-open document to do it in place with yet). The result
  is an ordinary document, its filled values are just extractable page content, for every
  widget that had something to bake in (see above for the no-`/AP` case). `NeedAppearances` is
  a fill-time-only escape hatch, never available on flatten.
- **XFA-hybrid documents** (both real-world IRS/USCIS-style samples carry `/XFA` alongside
  their AcroForm): filling drops the `/XFA` key with a recorded diagnostic, so XFA-aware
  viewers fall back to the filled AcroForm instead of silently rendering a stale, unfilled XFA
  stream. XFA content itself is never read, generated, or filled (permanently out of scope).
- **Usage-rights, signed, and permission-flagged documents**: filling and flattening both
  proceed on a `/Perms`/`/SigFlags`-bearing document with a recorded diagnostic that a
  third-party update invalidates those usage rights/signatures (real signature semantics are
  Phase 5's scope); `/P`'s `FillFormFields` bit is advisory like every other permission bit —
  a clear bit records a diagnostic and proceeds, never enforced.
- Recipes: `docs/cookbook/fill-form.md`, `flatten-form.md`.

## Digital signatures (Phase 5): sign, verify, LTV

- **`doc.Signatures`** (`SignatureCollection`) — the hub, indexable/enumerable over every existing signature/document-timestamp on the document. `Pdf.Sign`/`Pdf.SignAsync`/`Pdf.Verify` are the one-line verbs over a path.
- **Sign** — `doc.Signatures.Add(outputPath, PdfSignOptions)` (sync, PAdES B-B only) / `SignAsync(...)` (required for B-T or anything touching the network). `PdfSignOptions.Certificate` (an `X509Certificate2` with a private key) is the convenience door; `PdfSignOptions.Signer` (an `IPdfSigner`) is the HSM/KMS seam — implement it directly for a remote key, never extract the private key to call `Certificate`. `PdfSignOptions.Level` picks `PdfSignatureLevel.B` (baseline, offline) or `.T` (+ RFC 3161 timestamp via `TimestampAuthority`/`TimestampAuthorityUrl`).
- **Verify** — `signature.Verify(trustedRoots?)` → `SignatureVerificationResult`: `CryptographicStatus`, `CoversWholeDocument` (a structural `/ByteRange` check, independent of the crypto verdict — a shadow-attack guard), and — only when `trustedRoots` is supplied — `ChainStatus` and `IsTimestampTrusted`. PlumePDF never falls back to the OS trust store; no `trustedRoots` means chain/timestamp trust is simply not evaluated (`NotEvaluated`/`false`), never silently assumed. `IsValid` is the common-case shorthand (crypto-valid + whole-document coverage; does not require chain trust).
- **Timestamp trust is a two-legged check** — an RFC 3161 signature-timestamp sits in the CMS's *unsigned* attributes (outside the signature's own protection), so `HasTimestamp`/`TimestampTime` are present whenever a token decodes, but `IsTimestampTrusted` only turns `true` once the token's own signature verifies **and** its TSA certificate chain-builds to a supplied trust anchor. Never treat `TimestampTime` as proven time without checking `IsTimestampTrusted` first.
- **LTV maintenance** — `doc.Signatures.AddLtvAsync(outputPath, IRevocationFetcher, trustedRoots?)` embeds a `/DSS` of OCSP/CRL material (B-LT); `doc.Signatures.AddDocumentTimestampAsync(outputPath, ITimestampAuthority)` adds a standalone `/DocTimeStamp` revision (B-LTA). `IO.Http.HttpRevocationFetcher`/`HttpTimestampAuthority` are the in-box HTTP-backed implementations — restrict AIA/CRL-DP URIs to `http`/`https` only, since they come from a certificate embedded in the (untrusted) document being processed.
- **Determinism** — `PdfOptions.Deterministic` extends to signing, but only for RSA PKCS#1 v1.5 at `PdfSignatureLevel.B` with a caller-supplied `PdfSignOptions.SigningTime` and no TSA/LTV; every other combination (ECDSA, RSASSA-PSS, a TSA token, a wall-clock signing time) throws `PLUME6053` rather than silently producing non-reproducible output.
- A failed signing pass rolls back its own object-graph mutations — retrying `Add`/`SignAsync` on the same `PdfDocument` after a caught `PlumePdfException` is safe.

## Compliance & polish (Phase 6): PDF/A, tagged PDF, redaction, optimize/linearize, stamping

- **PDF/A create** — one switch: `manuscript.Render(PdfOptions.Default with { PdfAConformance = PdfAConformance.A2b })` (or `A1b`, which forces the `%PDF-1.4` header and forbids `Optimize`). Writes the `pdfaid` XMP identification (agreeing with `/Info`), attaches a `GTS_PDFA1` output intent (bundled CC0 sRGB profile; override via `PdfOptions.PdfAOutputIntentProfile`). Every font must be embedded: a Standard-14 font under PDF/A throws `PLUME8023` naming it — supply `PdfFont.FromFile/FromBytes` instead. `Deterministic` + PDF/A requires caller-supplied `Manuscript.CreateDate`/`ModifyDate` (`PLUME6058`). Recipe: `docs/cookbook/create-pdfa.md`.
- **PDF/A validate** — `Pdf.ValidatePdfA(path)` (or `PdfAValidator.Validate(doc)`) → `PdfAValidationResult`: per-rule `Pass`/`Fail`/`NotChecked` findings + `IsConformant`. Honestly bounded (an unchecked clause is `NotChecked`, never silently green) — the veraPDF CLI is the full-coverage oracle; the coverage table lives in `docs/cookbook/validate-pdfa.md`.
- **Tagged PDF / PDF-UA** — opt in by setting `Manuscript.Language`; that alone produces marked content + a real `/StructTreeRoot`. Element types carry structural roles for free (Text→`P`, Table→`Table/TR/TD`); only human semantics are caller-supplied: `Text.HeadingLevel`, `Image.AltText`, `Element.Role` (`"Artifact"` to exclude furniture), `Manuscript.Title`. A missing required semantic refuses (`PLUME9010`, naming the element path) only when UA output is requested — never best-effort auto-tagging. Read side: `PdfStructureInfo.For(doc)`; extraction reading order follows a parseable `/StructTreeRoot` automatically (geometric fallback + diagnostic otherwise). Recipe: `docs/cookbook/create-tagged-pdf.md`.
- **Redaction** — `doc.Redact(targets, PdfRedactOptions?)` or `Pdf.Redact(path, outputPath, targets)`; targets are `RedactionTarget.Text(...)`/`.Pattern(regex)`/`.Region(page, rect)` (`PlumePdf.Documents.Redaction`). True removal: operators deleted, intersecting images removed whole, DocInfo/XMP/annotation/outline/embedded-file-name text scrubbed. **Always check `RedactionResult.MatchCount`/`HadNoMatches`** — zero matches is loud in the result, not an exception, and the output still gets written. Signed source: refuses (`PLUME6062`) unless `AllowInvalidatingSignatures = true` (strips signature machinery — output is honestly unsigned). Encrypted source: refuses (`PLUME6061`, documented 1.x gap). Then `Save` only — see the trap above. Recipe: `docs/cookbook/redact.md`.
- **Optimize / Linearize** — `PdfOptions.Optimize` (ObjStm/XRefStm packing, smaller file, PDF 1.5+, `PLUME5017` below that) and `PdfOptions.Linearize` or `Pdf.Linearize(path, outputPath)` (fast web view). Mutually exclusive (`PLUME5018`). Recipe: `docs/cookbook/optimize-linearize.md`.
- **Stamp an opened document** — `doc.Stamp(new Stamp { Text = "CONFIDENTIAL" }, pageIndexes?)` or `Pdf.Stamp(path, outputPath, stampOrText)`; same `Stamp` descriptor as `Section.Stamps`. Additive (works through `SaveIncremental`; a signed source's signature stays cryptographically valid). Standard-14 Helvetica-Bold only — non-WinAnsi text throws `PLUME6072`; zero pages throws `PLUME6073`. Recipe: `docs/cookbook/stamp-document.md`.
- **Metadata write** — `doc.SetInfo(new DocInfoMetadata { ... })` / `doc.SetXmpMetadata(new XmpPacket { ... })` (`PlumePdf.Documents.Metadata`); the two must agree where they overlap (`PLUME6057`).

## Options that matter

`PdfOptions.Default with { … }`: `Deterministic = true` (byte-identical output — snapshot-test your PDFs), `UserPassword`/`OwnerPassword` (encrypted files; empty user password is tried automatically), `Strict = true` (every deviation throws instead of recovering — validators only).

## Diagnostics & errors

Reading is lenient by default: damaged files recover (up to a full-file scan) and every repair lands on `document.Diagnostics` as `{ Code, Severity, Message, ByteOffset?, Subject? }` — triage by `Code`, don't parse messages. Unrecoverable failures throw `PlumePdfException` with a stable `Code` (`PLUME####`) and `HelpLink`. Look any code up at `docs/errors/PLUME####.md` (index: `docs/errors/README.md`). Ranges: 1xxx IO, 2xxx parse/objects, 3xxx filters, 4xxx encryption, 5xxx writer, 6xxx documents/verbs, 7xxx content, 8xxx fonts, 9xxx layout.

## Verify a change end-to-end

```
dotnet build PlumePdf.sln -m:1     # warnings are errors; XML docs enforced
dotnet test PlumePdf.sln           # unit + architecture + corpus + cookbook
```

Never reference iText/Aspose/Syncfusion/QuestPDF source or add their packages to `PlumePdf.sln` (the clean-room policy in AGENTS.md; benchmarks solution only).
