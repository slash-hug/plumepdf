# PlumePDF

**A fully-featured, high-performance, agent-forward PDF library for .NET — Apache 2.0, no strings.**

Existing options force a choice: pay for a commercial SDK, accept AGPL (iText7), or live with partial coverage from permissive libraries. PlumePDF closes that gap — the "trust tier" (signatures, redaction, PDF/A, forms) included, under a license you can actually use at work.

## Pillars

- **Create** — high-level document composition with a real layout engine, including Arabic and Devanagari shaping and UAX #9 bidi
- **Read** — parse arbitrary real-world (including malformed) PDFs, with recoverable deviations reported as diagnostics
- **Extract** — text with positions and reading order, images, metadata, structure
- **Manipulate** — merge, split, reorder, stamp, watermark, optimize, linearize
- **Trust** — AcroForms (fill, flatten, appearance generation), digital signatures (PAdES B-B through B-LTA, verification), PDF/A create and validate, tagged PDF / PDF-UA, true redaction
- **Render** — an in-house managed page rasterizer and raster image codecs (PNG, JPEG, TIFF, CCITT, JBIG2, JPEG 2000), measured against PDFium in CI
- **Performance** — low-allocation Span-based core, streaming, NativeAOT-compatible, benchmark-gated in CI
- **Agent-forward** — discoverable API, shipped agent docs (`AGENTS.md`, `llms.txt`, cookbook), coded errors with a reference page each

## Status

**1.0.0-preview.** Every pillar above is implemented. Start with the [package README](src/PlumePdf/README.md) for the public surface, the [cookbook](docs/cookbook/) for task recipes, and the [error-code index](docs/errors/README.md). The [spec](docs/spec.md) and [architecture](docs/architecture.md) describe scope and internals. Agents: start with [AGENTS.md](AGENTS.md).

## Governance

- License: [Apache 2.0](LICENSE); third-party attributions in [NOTICE](NOTICE)
- Targets: .NET 8+
- IP provenance: strict clean-room policy (see [AGENTS.md](AGENTS.md)) — implemented from ISO 32000 and permissively-licensed sources only
- Vocabulary: [CONTEXT.md](CONTEXT.md)
