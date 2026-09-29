# PlumePDF Architecture Blueprint

This document describes the internal architecture; the public surface is documented in
`docs/spec.md`.

## Layers

One *runtime* assembly (`PlumePdf.dll`), one NuGet package — the package additionally carries a
build-time-only Roslyn analyzer assembly under `analyzers/dotnet/cs/`; the
analyzer never loads into a consuming application's process. Layering is enforced by
namespace-dependency architecture tests in CI — a lower layer never references a higher one:

```
8  PlumePdf            static Pdf task verbs (thin, built on 6/7)
7  PlumePdf.Layout     Manuscript element tree, layout engine, Compose veneer
6  PlumePdf.Documents  PdfDocument hub: pages, forms, extraction, stamping
5  PlumePdf.Raster     page rasterizer: display list, scan converter, glyph/shading paint
4  PlumePdf.Content    content-stream operators, graphics state, text runs
3  PlumePdf.Fonts      SFNT/OpenType parsing, embedding, subsetting, shaping; render-path outline interpreters (TrueType glyf, bare CFF by name or CID, Type 1) and the bundled substitute-font store (12 Liberation TrueType + 2 Foxit CFF faces, two generated manifests)
2  PlumePdf.Objects    object model, tokenizer, parser, xref, writer
1  PlumePdf.Filters    filter registry + codecs (public extension seam)
0  PlumePdf.IO         byte sources (mmap/stream), pooling, scanning primitives
```

Filters sits **below** Objects: cross-reference streams and object streams (`/ObjStm`)
are themselves Flate-compressed, so the Objects layer cannot parse them without Filters already
being available underneath it — the reverse order is unimplementable, not just undesirable.

**Fonts sits above Objects and below Content:** a subsetted font's `/FontFile2` stream
needs Flate encode (Filters) before it becomes a PDF object, so Fonts depends on Objects/Filters;
both Content (glyph IDs in `TJ` operators) and Layout (glyph metrics for measurement) consume
Fonts' output, so Fonts sits strictly beneath both.

**Raster sits above Content and below Documents (Phase 8):** the rasterizer's display-list
builder reuses the existing `Content.ContentStreamReader` operator stream rather than
re-tokenizing it, so Raster depends on
Content (and, transitively, Fonts/Filters/Objects for glyph outlines and image/shading data); the
`doc.Pages[i].Rasterize`/`Pdf.Rasterize` verbs are thin wrappers a Documents-layer/root-namespace
caller invokes, so nothing in Documents or above may be a dependency Raster itself needs — Raster
sits strictly beneath Documents, mechanically identical to the Fonts insertion above.

**Namespace policy:** every *public* type — the whole `doc.Objects` object-model surface
(`PdfObject` and its subtypes, `IndirectReference`, `ObjectRegistry`), `PdfOptions`,
`PdfDiagnostic`/`DiagnosticCollection`, `PdfFilterRegistry`/`IPdfFilter`, `PdfFont`,
`PdfRasterizeOptions`/`RasterColor` (Phase 8), and the hub types (`PdfDocument`,
`Manuscript`, `Pdf`, `PdfPage`, `PageCollection`, `SplitResult`) — lives in the root `PlumePdf`
namespace, regardless of which layer's folder its source file sits in. The eight layer namespaces
above (`PlumePdf.IO` … `PlumePdf.Layout`) hold internals only: tokenizer, parser, cross-reference
reader, filter codecs, font parsers/subsetters/shapers, content-stream operators, the rasterizer's
display list/scan converter, and the rest of each layer's implementation, all `internal`.
**Two deliberate exceptions** (Phase 2): the element vocabulary lives in `PlumePdf.Elements`
(`Element`, `Section`, `Text`, `Row`, `Column`, `Table`, `Image`, `PageBreak`, `Watermark`,
`Stamp`) and the Compose descriptors in `PlumePdf.Compose` — both public. Their names are
deliberately generic (a root-namespace `Text` or `Image` would collide with BCL and consumer
types and pollute the hub's IntelliSense), they are consumed through `Manuscript`/`Compose`
rather than discovered as entry points, and the ratified element-tree prototype already
used them via a namespace import. Both namespaces sit in the layering test's order (Elements
between Documents and Layout — pure data, no dependency on either direction's internals;
Compose above Layout as the veneer) so the dependency rules cover them.
A type's *folder* reflects which layer owns and maintains it; its *namespace* reflects whether it
is part of the public contract. This is why a plain `"PlumePdf"` namespace-prefix layering rule is
unusable — it would match every public type in the codebase — and why
`tests/PlumePdf.ArchitectureTests/LayeringTests.cs` also carries an enumerated façade rule naming
the specific higher-layer hub types (`PdfDocument`, `PdfPage`, `PageCollection`, `Manuscript`,
`Pdf`, `SplitResult`, `PdfFont`) that no lower internal layer namespace may depend on, alongside
the namespace-to-namespace rule above.

Cross-cutting internal services with seams: **Crypto** (encryption handlers, signing) is real
from Phase 1 for encryption (an internal `ISecurityHandler` seam) and from Phase 5 for signing —
CMS/byte-range/DER core lives in `PlumePdf.Objects.Signing`, sign/verify/DSS orchestration in
`PlumePdf.Documents.Signing`, and the external-signer/timestamp/revocation seams
(`IPdfSigner`, `ITimestampAuthority`, `IRevocationFetcher`) are public, root-namespace, top-level
types — genuinely cross-cutting contracts any layer may implement or consume, the same
"shared contract every layer legitimately depends on" treatment `PdfOptions`/`PdfFilterRegistry`
already get, so they are not enumerated in `LayeringTests.FacadeTypes`. **Fonts** (parsing,
embedding, subsetting, shaping) is real from Phase 2 — its own layer (above), with the internal
`IFontMetrics`/`ILineShaper` seams and the public `PdfFont` facade; the
extension seam (a third party plugging in their own font source) stays internal until a
1.x backlog item makes it public. The **filter registry is public in v1** — it is the
seam where a community codec can plug into a filter PlumePDF doesn't ship itself; Phase 2 adds a
parallel public *encode*-side seam, `IPdfEncodingFilter`, leaving the existing decode-only
`IPdfFilter` unbroken. CCITT and JBIG2 were community-seam examples through Phase 6;
from Phase 7 they are PlumePDF's own in-house managed codecs, registered by default.
**`JPXDecode` (JPEG
2000) followed the same path**: charted as the seam's standing example, and — unlike
CCITT/JBIG2/DCT — deferred longest (patent/complexity),
it is now a fourth in-house managed codec — an internal, dependency-free
JPEG 2000 Part 1 decoder (`Filters/Jpx`, layer index 1, the JBIG2 shape) registered by default
on `PdfFilterRegistry.Default`. The seam itself does not
retire — it still exists for a vendor filter PlumePDF has never heard of at all, and a caller can
still register their own decoder over any of the four in-house codecs (JBIG2, CCITT, DCT, JPX
included) to override PlumePDF's default. Phase 7
also adds a second, PDF-free imaging facade sitting in the Filters layer itself
(`PlumePdf.RasterImage`, folder `src/PlumePdf/Filters/`, layer index 1) — it decodes/encodes
PNG/JPEG/TIFF bytes independent of any `PdfDocument`, which is exactly why it lives at the
Filters layer rather than the Documents layer alongside `ExtractedImage`.

## Reading pipeline

- **Byte source:** a path opens a `MemoryMappedFile`; a `Stream` uses buffered random access behind the same internal abstraction. Scratch buffers come from `ArrayPool<T>`; delimiter/whitespace scanning uses `SearchValues<byte>`.
- **Tokenizer:** a forward-only, allocation-free `ref struct` reader modeled on `Utf8JsonReader` (resumable via consumed-count + value-type state).
- **Object access is lazy:** opening a file parses only header, trailer, and cross-reference data. Indirect objects resolve on first access and cache; object streams and cross-reference streams are handled uniformly with classic tables (hybrid files included).
- **Recovery ladder** (lenient by default): well-formed xref → repairable deviations (logged) → full brute-force object scan when cross-reference data is corrupt or missing. Every deviation and repair is recorded in `doc.Diagnostics`; an exception is thrown only when recovery is impossible. `PdfOptions.Strict` turns deviations into failures for validator/compliance scenarios.

## Writing pipeline

- **Incremental update is the default save path for opened documents** (`SaveIncremental`): append changed objects + new cross-reference section, preserving prior revisions (required for signed files). `Save` performs a full rewrite (garbage-collects unreferenced objects, renumbers). The one carve-out is redaction: a redaction-dirty document is full-rewrite-only — `SaveIncremental` refuses with `PLUME5016`, since preserved prior revisions would keep the redacted bytes recoverable. `document.Pages.RemoveAt` is a second, deliberately different case: `SaveIncremental` appends onto a copy of the document's original bytes, so a removed page's bytes stay recoverable in the saved file — this is not refused, because it is how a page is dropped from a signed PDF without invalidating the signature. A caller who needs the removed page gone must use `Save` (see the removed-page barrier below) or `Pdf.Redact` for content that must be unrecoverable.
- **The removed-page barrier:** on a full-rewrite `Save` (plain, `Optimize`, `Linearize`), `PdfDocument.Save` hands the writers an exclusion set: the pages removed since open, every original page-tree node (the writers always replace the tree with a fresh flat `/Pages` node), and what belonged only to the removed pages — their annotations, form widgets placed on them, fields left with no widget, and structure elements whose content was all on them. The writers never follow a reference into the set and write `null` in its place, so nothing that still references a removed page can bring it (or, through its `/Parent`, the rest of the original tree) back. Then a save-time clean-up (`Documents/PageRemoval`, one pass per structure: annotations, form, outlines, named destinations, open action, structure tree) rewrites what pointed at a removed page as copies handed to the writers — the open document is never modified — and `PLUME5021` records what it left out. `SaveIncremental` appends to the original bytes and keeps them.
- The writer streams: source bytes copy through to the output without materializing the whole
  document. **No whole-document *byte* buffer, ever.** Composing
  (`Manuscript.Render`) builds the composed document's in-memory *object graph* (via
  `PdfDocument.CreateSynthetic`) before any bytes are written — that is not a violation of this
  promise: an object graph is not a byte buffer, and each page's content stream is individually
  encoded, not concatenated into one blob. A fused streaming `Render(Stream)`/`RenderTo(Stream)`
  that avoids the object-graph materialization too is deferred to the 1.x backlog, added only if
  real demand appears. Linearization reads
  this promise the same narrow way: its two-pass save stages through the writer's existing
  on-disk temp file — never a whole-document *in-memory* buffer — to back-patch the hint tables.

**Mutation and the dirty-object set (Phase 4):**
`ObjectRegistry` (`doc.Objects`) gains a general-purpose `MarkDirty(reference)` (for a
`PdfDictionary`/`PdfArray`/`PdfStream` already mutated in place — these containers were always
mutable) and `Allocate(value)` (for a brand-new indirect object, honouring
the cross-reference free list before minting a fresh number). Both writers (`IncrementalUpdateWriter`,
`FullRewriteWriter`) derive their "what changed" set from this registry-owned collection instead
of a single-purpose flag, closing a real silent-data-loss gap the flag-only design had: mutating
an already-open document's object graph through `doc.Objects` and calling `SaveIncremental`
previously produced a byte-identical file with the mutation silently dropped, because the writer
had no general signal that anything had changed. Allocation order is stable under
`PdfOptions.Deterministic`. This is the machinery Phase 4's forms mutation surface, `Pdf.Merge`'s
`/AcroForm` fix, and opened-document stamping all share — built generally once rather than
forms-scoped now and widened twice more later. The v1.0 opened-document-stamping commitment
(deferred from Phase 2) shipped in Phase 6: `PdfDocument.Stamp`/`Pdf.Stamp`
append a per-page stamp content stream — wrapping the original content in `q`/`Q`, never
rewriting existing bytes — so it is purely additive and works through `SaveIncremental`, the
signature-preserving path (unlike redaction, which is forced through the full rewrite).

**The same narrow reading applies read-side, to extraction (Phase 3):** `page.ExtractText()`/`page.ExtractImages()`
materialize exactly one page's result at a time — never more than one page's letters/words/lines
or images simultaneously. `Pdf.ExtractText(path)`, the flattened-string convenience door,
streams page-by-page into a pooled builder rather than holding every page's letter graph at
once; its XML docs state plainly that the *returned string* is unavoidably whole-document — that
materialization is the documented cost of the convenience API, not a hidden violation of this
promise.

## Raster pipeline

`Rasterizer.Rasterize` runs two phases with a hard rule about which per-call knob belongs to
which one:
**build-time** inputs (`PrintIntent`, optional-content `/OCProperties` resolution) are consumed
once while `RasterInterpreter.BuildDisplayList` walks the content stream and decide what enters
the display list at all — they never reach a paint call. **Paint-time** inputs govern how
something already in the display list is drawn, and live on one internal
`readonly record struct RasterPaintContext(ImageResamplingMode Resampling, bool AntiAlias)`,
built once per call in `Documents.PageRasterAdapter` from the validated
`PdfRasterizeOptions` and threaded explicitly through every paint entry point — `Paint`/
`PaintObject` and its transparency-group/tiling-pattern/soft-mask recursions,
`PaintPathObject`/`PaintTextObject`, `ImagePainter.Paint`, `GlyphRasterizer.Paint`,
`ScanlineRasterizer.Sweep`, and `AnnotationReader.RenderAnnotations`. There is no ambient
default read mid-paint: a Cecil architecture test (`RasterPaintContextWiringTests`) bans a
flag-less `Sweep`/`Paint` overload and bans any `PlumePdf.Raster` method other than the
adapter from calling `RasterPaintContext.Default`, so a knob added to the struct is
mechanically guaranteed to reach every painter rather than silently defaulting on a call site
someone forgot. The next per-call render knob follows the same test either way: paint-time →
add a field to `RasterPaintContext`; build-time → a `BuildDisplayList` parameter.

Two paint-time seams currently read the context. `ImagePainter.Decide(context, image)` picks a
per-axis filter from the placement's real sampling footprint (`ImageResamplingMode.Auto` follows
PDFium parity — footprint box when minifying, 2-tap bilinear when magnifying below an
8×-source-area cut-off or when `/Interpolate true` is set, nearest beyond); `Paint` executes that
same decision and hands the modes to `TryFootprint`, the single per-axis footprint resolver both
the axis-aligned and general paint paths share.
`ScanlineRasterizer.Sweep`'s `antiAlias` flag thresholds fill/stroke/glyph coverage at 50 %
instead of anti-aliasing it. `ClipRegionResolver` is the one documented exemption: it takes no
flag and always anti-aliases clip-region coverage (PDFium parity where it is measurable), so no
cache-key change was needed there for correctness — the per-call invariant that would have
made one necessary holds anyway: `RasterGraphicsState`'s `ClipPath` (which owns the memoized
`ClipChainCoverage`) is constructed only while `BuildDisplayList` runs, and each `Rasterize`
call builds its own display list from scratch, so no `ClipPath` — and no clip coverage cache —
is ever shared across two calls, whatever their options.

## Network I/O (Phase 5)

Offline by default: no code under `src/` opens a socket unless the caller explicitly supplies a
timestamp/revocation client or endpoint configuration — RFC 3161 timestamping and OCSP/CRL fetching for LTV are the first genuinely IO-bound,
network-dependent surface this codebase ships. `src/PlumePdf/IO/Http/` holds the default,
opt-in-only `HttpClient`-backed implementations (`HttpTimestampAuthority`,
`HttpRevocationFetcher`) of the public `ITimestampAuthority`/`IRevocationFetcher` seams; their
mere presence in the tree implies no ambient network behavior, since `Documents.Signing`
orchestration only ever constructs one when `PdfSignOptions`/an LTV call explicitly asks for it.
Both carry required timeouts and response-size caps (`PdfOptions.TimestampTimeout`/
`RevocationTimeout` and companion byte caps) — the same per-hazard resource-limit
discipline already applied to every other untrusted-input surface, extended to a hostile or
merely slow remote responder. CI's gating lane never depends on a live TSA/OCSP responder: an
in-process fake timestamp authority built over `Rfc3161TimestampRequest`/`Rfc3161TimestampToken`
plus a generated test certificate authority stand in, with a hermetic-lane sentinel test that
fails loudly if that fixture set is ever empty; live-network validation (the EU DSS demo
validator) is a separate, explicitly non-gating, manual maintainer lane, mirroring the
benchmark-comparison two-lane pattern already established above.

## Error philosophy

Lenient-by-default with diagnostics, per the recovery ladder above. Failure is reserved for the impossible, not the irregular — "read anything" is a pillar. Exceptions that do escape are actionable: they name the offending object/offset and the recovery that was attempted.

**Diagnostics scoping:** `doc.Diagnostics`
is the open/parse-time surface — every deviation tolerated while reading structure, cross-reference
data, and encryption. Extraction results (Phase 3's `ExtractedText` and kin) carry their **own**
result-scoped `Diagnostics` instead of appending to `doc.Diagnostics` per call — a server
extracting the same open document repeatedly would otherwise grow `doc.Diagnostics` without bound
and duplicate entries per pass. `doc.Diagnostics` additionally receives one first-occurrence
summary entry per page per extraction call, so "check `doc.Diagnostics` once" stays true
in substance even though the full detail for a given page's extraction lives on that page's own
result.

## Threading & mutation

- An opened `PdfDocument` supports **thread-safe concurrent reads**: parsed structures are immutable and the lazy object cache is lock-free (`ConcurrentDictionary`/`Interlocked` publication). Parallel page extraction is a first-class server scenario.
- **Mutation is single-threaded by contract**: documented, and guarded by debug-build checks. A mutated document remains readable from the mutating thread only.
- `Manuscript` is a plain mutable object graph with no thread-safety guarantees.
- **`Rasterize` never mutates the opened document — the scratch-registry read-only invariant
  (Phase 9).** Rendering a
  `/Widget` annotation with a `/V` value but no `/AP` (or any document with `/NeedAppearances`
  set) needs the same appearance-generation code `Pdf.FillForm`/`FlattenForm` use — code written
  exclusively against a mutating, single-writer `ObjectRegistry`. `Raster.ScratchObjectRegistry`
  resolves this without a special-case generator: it wraps the real `document.Objects` as a
  **read-through** resolver but collects every write (`AllocateNumber`/`RegisterNew`) into its
  own private, per-call map, discarded the instant `Rasterize` returns. The generator itself needs
  no Rasterize-aware branch — it just gets handed a registry whose writes go nowhere durable. One
  mechanism buys two guarantees at once: the document is provably unchanged after a `Rasterize`
  call (asserted byte-identical — next-object-number counter, dirty set, and free list — by
  `tests/PlumePdf.Tests/Raster/RasterReadOnlyInvariantTests.cs`), and *N* concurrent threads
  rasterizing the same open document each get their own private scratch registry with no shared
  mutable write path — the same mechanism that proves read-only doubles as the concurrency proof,
  gated by `tests/PlumePdf.CorpusTests/ConcurrencyStressTests.cs`'s parallel-vs-serial
  pixel-identity check. Zero public API surface: the scratch registry is `internal`, invisible to
  callers, who simply see that `Rasterize` — unlike `Pdf.FillForm`/`FlattenForm` — never dirties
  the document it reads from.

## NativeAOT & trimming

`IsAotCompatible=true` from the first commit; the analyzer chain gates CI. No reflection, `Reflection.Emit`, or dynamic codegen anywhere in the core. Any metadata-driven behavior (e.g. `Pdf.FillForm(path, IEnumerable<KeyValuePair<string, string>> values)` mapping field-value pairs) uses explicit overloads, never runtime reflection; a `[FormModel]`-attribute source generator over named partial types remains a possible 1.x addition but is not required for AOT-safety, since the shipped overloads already avoid reflection entirely. A mechanical architecture test bans `System.Reflection` from `src/PlumePdf` outright, closing the gap where a reflection-based shape (`values.GetType().GetProperties()`) would otherwise compile clean and pass the trim analyzer and `aot-smoke` lane without anyone noticing.

## Enforcement & solution layout

```
plumepdf.sln            src/PlumePdf + tests (unit, architecture, corpus)
benchmarks/bench.sln    SEPARATE solution: BenchmarkDotNet suites incl.
                        competitor comparisons (iText7)
```

The benchmark solution is the **AGPL isolation wall**: competitor libraries are package references there and only there — never in the main solution, never vendored. **The wall is solution-level, not project-level:** competitor packages are confined to one designated project inside `bench.sln` — `PlumePdf.Benchmarks.Comparisons`, added in Phase 3 (the first phase a competitor package reference actually exists) — while `PlumePdf.Benchmarks` itself (the PlumePDF-only macro suites below) stays competitor-free so its own build output is never AGPL-linked. CI's per-commit gate is build-only (`dotnet build benchmarks/bench.sln`) — it proves the harness compiles, nothing more. PlumePDF-only macro suites with `[MemoryDiagnoser]` run manually/per-phase against a stored baseline; competitor comparisons (iText7) are manual and informational, also per-phase. A hard per-commit regression gate is deferred until a dedicated, quiet runner exists — shared GitHub-hosted runners are a documented source of false-positive regressions, and a gate the team learns to bypass is worse than no gate. Conformance corpora (Arlington, veraPDF, hand-crafted edge cases) drive the corpus test project per the standards research.
