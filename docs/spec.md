# PlumePDF v1 Specification

This is the umbrella spec: it fixes scope and commitments, and points at the documents that hold each area's detail rather than restating them.

## Vision

A fully-featured, high-performance, agent-forward PDF library for .NET under Apache 2.0. Competitive research showed commercial SDKs consistently paywall the **trust tier** — true redaction, PAdES/LTV signatures, PDF/A, complete forms — while permissive open source covers only the basics. **PlumePDF's identity is closing that trust gap under a license usable at work**, with performance receipts and first-class agent ergonomics.

## Scope

**v1.0 ships all pillars:** creation with a real layout engine (including complex-script shaping), parsing of arbitrary real-world PDFs, extraction (text with positions, images), manipulation (merge/split/stamp/watermark), AcroForms (fill/create/flatten), digital signatures (PAdES B-B→B-LTA, timestamping, LTV, verification), PDF/A create+validate, tagged PDF / PDF/UA, true redaction, optimization/linearization, and — per the 2026-08-20 Pixels-expansion charter amendment, further amended 2026-09-05 — the pixels tier: page rasterization (`Rasterize`, in-house managed engine, best-effort with diagnostics, per-call render-intent tuning — image resampling mode, anti-aliasing), raster image codecs (PNG/JPEG/TIFF incl. multi-frame decode, PNG/JPEG encode, standalone `RasterImage` surface), in-house JPEG 2000 (JPX) Part 1 decode, and image→PDF from encoded bytes.

**Out of scope permanently:** XFA, OCR, HTML-to-PDF, Office↔PDF conversion, barcode decoding (computer vision, not PDF). Rasterization, once listed here, ships in v1.0. **Deferred to 1.x:** barcode generation (layout-element hook), AES-256/permissions encryption write, OCG layer-toggling API, JBIG2 Huffman/halftone paths, CJK substitute-font fallback, raster print-pipeline exotica, cross-platform raster byte-determinism.

## Public API surface

Namespace and single NuGet package `PlumePdf`. Three doors, one hub — an agent needs two names:

```csharp
// task verbs (one-liners)
Pdf.Merge("a.pdf", "b.pdf").Save("merged.pdf");

// the hub — always a real PDF
using var doc = PdfDocument.Open("in.pdf");        // lenient, streaming, lazy
var created = PdfDocument.Compose(page => { ... }); // fluent creation

// data-first creation
var doc2 = new Manuscript { Sections = { ... } }.Render();

// escape hatch, all the way down
var trailer = doc.Objects.Trailer;
```

Sync CPU-bound core; true-async variants at IO edges. Coded exceptions (`PlumePdfException` → `Code` + `HelpLink`) are API contract. `PdfOptions.Deterministic` (byte-identical output) is public and permanent — under signing (Phase 5), that guarantee is scoped precisely to RSA PKCS#1 v1.5 with a caller-supplied signing time and no TSA/LTV; everything else under the flag is a coded refusal, never silently non-reproducible output.

## Commitments

- **Performance:** low-allocation Span-based parsing, streaming (no whole-document buffering), NativeAOT compatibility from commit one, CI benchmark gate vs PdfPig/PDFsharp/iText7. Detail: `docs/architecture.md`.
- **Correctness:** conformance corpus (Arlington model, veraPDF corpus, hand-crafted edge cases) in CI; lenient-by-default reading with `doc.Diagnostics`. Detail: `docs/architecture.md`.
- **Agent-forward:** the 18-point staged charter — AGENTS.md, CI-verified cookbook, in-repo skill, coded errors, deterministic snapshots, analyzers from P2. Detail: `docs/agent-forward.md`.
- **IP provenance:** strict clean-room; spec text never committed; benchmark competitors isolated in a separate solution.
- **Targets:** .NET 8+ only.

## Quality gates

Every phase exits only when its conformance-corpus targets pass in CI, the benchmark gate is green including competitor comparisons for that phase's scenarios, and cookbook + AGENTS.md cover the new surface. Where a phase's scenarios have no permissively-licensed competitor to compare against, the gate demotes to PlumePDF-only suites with stored baselines instead of a competitor comparison — recorded per-phase, not silently skipped; this applied for Phase 6 and Phase 6.5's shaping scenarios, since no permissively-licensed .NET complex-script shaper or complex-script-shaping competitor exists.

## Governance

Apache 2.0, public. Vocabulary in `CONTEXT.md`.
