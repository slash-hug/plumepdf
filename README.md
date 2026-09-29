<p align="center">
  <img src="brand/plumepdf-icon.svg" width="128" height="128" alt="PlumePDF logo — a teal quill feather with an amber nib">
</p>

<h1 align="center">PlumePDF</h1>

<p align="center">
  <strong>A fully-featured, high-performance, agent-forward PDF library for .NET — Apache 2.0, no strings.</strong>
</p>

<p align="center">
  <a href="https://github.com/slash-hug/plumepdf/actions/workflows/ci.yml"><img src="https://github.com/slash-hug/plumepdf/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI"></a>
  <a href="https://www.nuget.org/packages/PlumePdf"><img src="https://img.shields.io/nuget/vpre/PlumePdf?label=nuget" alt="NuGet"></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-Apache--2.0-blue" alt="License: Apache-2.0"></a>
</p>

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

## Install

```bash
dotnet add package PlumePdf --prerelease
```

The `--prerelease` flag is needed until 1.0.0 ships. Targets .NET 8 and .NET 10, a single assembly with one runtime dependency (`System.Security.Cryptography.Pkcs`), and NativeAOT-safe — no reflection anywhere. The package also carries a build-time Roslyn analyzer (see [Using PlumePDF with AI coding agents](#using-plumepdf-with-ai-coding-agents)).

## Quick start

Two names cover almost everything: **`PdfDocument`** (open, inspect, mutate, save) and **`Pdf`** (one-line task verbs). The examples below are embedded from tests CI runs on every change, so they always compile and match real output (`report` is the test's output buffer).

**Create a document** — a fluent layout engine with headers, footers, columns and page numbering:

<!-- snippet: create-invoice -->
<a id='snippet-create-invoice'></a>
```cs
using var document = PdfDocument.Compose(page =>
{
    page.Size(PageSize.A4).Margin(40);
    page.Header().Text("INVOICE #1042").Bold().FontSize(20);
    page.Content().Column(col =>
    {
        col.Spacing(12);
        col.Item().Text("Bill to: Acme Corp");
        col.Item().Text("Total: $500.00").Bold();
    });
    page.Footer().AlignCenter().Text(text =>
    {
        text.Span("Page ");
        text.CurrentPageNumber();
        text.Span(" of ");
        text.TotalPageCount();
    });
});

document.Save("output/invoice.pdf");
report.AppendLine($"Pages: {document.Pages.Count}");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L350-L372' title='Snippet source file'>snippet source</a> | <a href='#snippet-create-invoice' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

**Extract text** — a flattened "quick door" for the whole document, or positions, words and lines per page:

<!-- snippet: extract-text -->
<a id='snippet-extract-text'></a>
```cs
using var source = PdfDocument.Open("output/extract-text-source.pdf");

// The "quick door": Pdf.ExtractText(path) flattens every page's text into one string.
var wholeDocumentText = Pdf.ExtractText("output/extract-text-source.pdf");

// The "rich door": PdfPage.ExtractText() gives positions, words/lines, and the raw
// Letters escape hatch — call it per page so a context-budgeted agent can pull only
// the pages it needs instead of the whole document at once.
ExtractedText page1 = source.Pages[0].ExtractText();

report.AppendLine($"Whole-document text: {wholeDocumentText}");
report.AppendLine($"Page 1 text: {page1.Text}");
report.AppendLine($"Page 1 words: {page1.Words.Count}");
report.AppendLine($"First letter: '{page1.Letters[0].Value}' at ({page1.Letters[0].X:F1}, {page1.Letters[0].Y:F1})");
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.cs#L476-L491' title='Snippet source file'>snippet source</a> | <a href='#snippet-extract-text' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

**Render a page to pixels** — an in-house managed rasterizer, no native dependency:

<!-- snippet: rasterize-page -->
<a id='snippet-rasterize-page'></a>
```cs
var image = Pdf.Rasterize("samples/classic-xref.pdf", PdfRasterizeOptions.Default with { Dpi = 150 });
var frame = image.Frames[0];
File.WriteAllBytes("output/page-0.png", frame.EncodePng());
```
<sup><a href='/tests/PlumePdf.CookbookTests/CookbookTests.Phase8.cs#L20-L24' title='Snippet source file'>snippet source</a> | <a href='#snippet-rasterize-page' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Recipes

Every recipe in the [cookbook](docs/cookbook/README.md) is one task, embedded from a CI-run test with its verified expected output.

- **Create** — [invoice](docs/cookbook/create-invoice.md) · [Arabic / right-to-left](docs/cookbook/create-arabic-document.md) · [tagged / accessible PDF](docs/cookbook/create-tagged-pdf.md) · [PDF/A](docs/cookbook/create-pdfa.md) · [PDF from images](docs/cookbook/from-images.md) · [deterministic output](docs/cookbook/deterministic-output.md)
- **Read and extract** — [open and inspect](docs/cookbook/open-and-inspect.md) · [text](docs/cookbook/extract-text.md) · [images](docs/cookbook/extract-images.md) · [metadata](docs/cookbook/read-metadata.md) · [damaged PDFs](docs/cookbook/handle-damaged-pdf.md)
- **Manipulate** — [merge](docs/cookbook/merge.md) · [split](docs/cookbook/split.md) · [reorder and remove pages](docs/cookbook/reorder-pages.md) · [stamp](docs/cookbook/stamp-document.md) · [save incrementally](docs/cookbook/save-incremental.md) · [optimize and linearize](docs/cookbook/optimize-linearize.md)
- **Forms** — [fill](docs/cookbook/fill-form.md) · [flatten](docs/cookbook/flatten-form.md)
- **Signatures** — [sign](docs/cookbook/sign-document.md) · [verify](docs/cookbook/verify-signatures.md) · [timestamp and LTV](docs/cookbook/timestamp-and-ltv.md)
- **Compliance** — [validate PDF/A](docs/cookbook/validate-pdfa.md) · [redact](docs/cookbook/redact.md)
- **Render and images** — [rasterize a page](docs/cookbook/rasterize-page.md) · [scanned JPEG 2000 pages](docs/cookbook/rasterize-scanned-jpeg2000.md) · [substitute fonts](docs/cookbook/rasterize-substitute-fonts.md) · [decode an image file](docs/cookbook/decode-image.md)

The full public surface, area by area, is in the [package README](src/PlumePdf/README.md).

## Using PlumePDF with AI coding agents

PlumePDF is built to be used by coding agents as well as people:

- **[`llms.txt`](llms.txt)** — a compact index of the API, conventions, and docs. Point your agent at it (or paste it into context) before it writes PlumePDF code.
- **Agent skill** — [`.claude/skills/plumepdf/SKILL.md`](.claude/skills/plumepdf/SKILL.md) teaches an agent the entry points, the save model, and the error contract. Copy the `plumepdf` folder into your project's `.claude/skills/` (or your agent's equivalent skills directory).
- **Coded errors** — every failure throws a `PlumePdfException` with a stable `PLUME####` `Code`, and `Exception.HelpLink` opens that code's page (Cause / Example / Fix / Recovery). An agent that hits an error can follow the link instead of guessing; the [error-code index](docs/errors/README.md) lists them all.
- **Diagnostics, not silence** — recoverable problems in a document land on `doc.Diagnostics` (and on each extraction or rasterization result), so an agent can see what was repaired.
- **Analyzer** — `PLMP0001` flags, at build time, a composed document that is never rendered or saved.
- **Cookbook as ground truth** — each recipe's snippet compiles and runs in CI; copying one is safe.

Agents working *on* this repository (rather than with the library) start at [`AGENTS.md`](AGENTS.md): build commands, layering rules, and the clean-room policy.

## Documentation

- [Package README](src/PlumePdf/README.md) — the public surface by area
- [Cookbook](docs/cookbook/README.md) — task recipes
- [Error-code index](docs/errors/README.md) — one page per `PLUME####` code
- [Specification](docs/spec.md) — scope and commitments
- [Architecture](docs/architecture.md) — layers, pipelines, threading, AOT
- [Agent-forward contract](docs/agent-forward.md) — the documentation, error, and testing bar
- [Vocabulary](CONTEXT.md) — canonical terms

## Contributing

```bash
dotnet build PlumePdf.sln -m:1
dotnet test PlumePdf.sln
dotnet format PlumePdf.sln --verify-no-changes
```

Run tests from the repository root so the architecture tests run too; corpus tests self-skip unless `./scripts/fetch-corpora.sh` has been run. See [CONTRIBUTING](.github/CONTRIBUTING.md) and [AGENTS.md](AGENTS.md) for the rules that bite — every change needs tests, every error code a doc page, and all code must trace to the clean-room policy.

## Governance

- License: [Apache 2.0](LICENSE); third-party attributions in [NOTICE](NOTICE)
- IP provenance: strict clean-room policy (see [AGENTS.md](AGENTS.md)) — implemented from ISO 32000 and permissively-licensed sources only
- Vocabulary: [CONTEXT.md](CONTEXT.md)
