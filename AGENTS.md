# AGENTS.md

PlumePDF: a fully-featured, high-performance, agent-forward PDF library for .NET (Apache 2.0). **Status: 1.0.0-preview.** Shipped: reading and manipulation (open, merge, split, reorder, full-rewrite and incremental save, encryption read), creation with a real layout engine (`Manuscript` / `PdfDocument.Compose`, Standard-14 and embedded TrueType/OpenType fonts, Arabic/Devanagari shaping, UAX #9 bidi), extraction (text with positions and reading order, images, metadata, structure), AcroForms (fill, flatten, real `/AP` appearance generation), digital signatures (PAdES B-B through B-LTA signing, verification, the `IPdfSigner` / `ITimestampAuthority` / `IRevocationFetcher` seams), compliance (PDF/A-1b/2b create, `Pdf.ValidatePdfA` self-check with veraPDF as the CI oracle, tagged PDF / PDF-UA, true redaction, optimize/linearize, opened-document stamping, typed metadata write), raster image codecs (PNG/JPEG/TIFF/CCITT/JBIG2/JPEG 2000 decode, PNG/JPEG encode, `RasterImage`, `Pdf.FromImages`), and the in-house page rasterizer (`Pdf.Rasterize` / `page.Rasterize` — text, paths, shadings incl. mesh types 4–7, images, transparency, optional content, annotation appearances, print intent, per-call `ImageResampling` / `AntiAlias`), measured against PDFium by an SSIM oracle gate. Read before changing anything: `docs/spec.md` (scope and commitments), `docs/architecture.md` (layers, pipelines, threading), `docs/agent-forward.md` (the doc/error/testing contract), and `CONTEXT.md` (vocabulary — use these terms, avoid the listed synonyms).

## Commands

- Build: `dotnet build PlumePdf.sln` (run at repo root; warnings are errors, missing XML docs fail the build)
- Test: `dotnet test PlumePdf.sln` (unit + architecture + corpus projects; corpus tests self-skip without corpora)
- Format check: `dotnet format PlumePdf.sln --verify-no-changes` — run `dotnet format PlumePdf.sln` to fix
- Fetch corpora (optional, for the corpus lane): `./scripts/fetch-corpora.sh`
- Benchmarks: `dotnet run -c Release --project benchmarks/PlumePdf.Benchmarks` — the benchmarks solution is `benchmarks/bench.sln`; competitor PDF libraries (PdfPig, PDFsharp, iText7) live only in `benchmarks/PlumePdf.Benchmarks.Comparisons` within that solution (the AGPL isolation wall is solution-level, not project-level); NEVER add them to `PlumePdf.sln`, and never to `PlumePdf.Benchmarks` itself
- Do NOT run `dotnet test` inside an individual project directory — always at the root, so architecture tests run.

## Layout

- `src/PlumePdf/` — the single library assembly. Internal layers, low to high (IO → Filters → Objects → Fonts → Content → Documents → Layout → verbs — Filters sits below Objects because cross-reference streams and object streams are themselves Flate-compressed), enforced by `tests/PlumePdf.ArchitectureTests`.
- `tests/` — `PlumePdf.Tests` (unit), `PlumePdf.ArchitectureTests` (layering, AOT and determinism bans), `PlumePdf.CorpusTests` (conformance and external oracles), `PlumePdf.CookbookTests` (the snippets embedded in `docs/cookbook/`).
- `analyzers/` — `PlumePdf.Analyzers` (the `netstandard2.0` Roslyn analyzer, `PLMP####` ids) and its tests, both in `PlumePdf.sln`; packed into the main nupkg, never published standalone.
- `benchmarks/` — separate solution; the AGPL isolation wall.
- `docs/errors/` — one page per `PLUME####` code (the `Exception.HelpLink` target); `docs/cookbook/` — task recipes generated from tested source by MarkdownSnippets.

## Clean-room policy

PlumePDF exists to give .NET teams a PDF library free of AGPL/commercial license risk, so its IP provenance must be beyond reproach. A derivative-work claim would poison the whole codebase. We implement from specifications, not from restricted code. `scripts/check-provenance.sh` enforces the mechanical parts in CI.

- **Allowed sources:** ISO 32000-1/-2 and all published standards (font formats, filter specs, crypto RFCs/FIPS, PDF/A, PDF/UA), obtained legitimately; Adobe technical notes and other published vendor documentation; source code of **permissively-licensed** libraries only — PdfPig (Apache-2.0), PDFsharp (MIT), PDFium (BSD), pdf.js (Apache-2.0) and equivalents. Porting their code is allowed with license-notice compliance: attribute every port in `NOTICE` and in a provenance comment at the top of the implementing file.
- **Forbidden:** reading the **source code** of iText7 (AGPL), Aspose.PDF, Syncfusion PDF, QuestPDF, or any other copyleft/commercial PDF library — even "just to see how they did it". For these, only public API documentation, feature lists and marketing pages may be consulted. Also forbidden: copying or closely paraphrasing text from paywalled/EULA-restricted specifications into this repo — link and cite instead (the sponsored ISO 32000-2 access forbids redistribution; see `docs/spec-sources.md`).
- **Running** restricted tools (benchmarking against iText7, veraPDF, HarfBuzz, poppler as test oracles) is fine — the restriction is on reading or deriving from their source, not on executing their binaries. AGG 2.5+ is GPL and off-limits; the rasterizer ports AGG 2.3 as vendored in PDFium.
- Cross-reference recovery is implemented from ISO 32000-1 §7.5 and PdfPig (Apache-2.0) only.
- Any code contributed by agents or humans must be traceable to an allowed source or original work.

## Rules that bite

- **Never commit spec text**, customer documents, or anything derived from a customer's files; test fixtures are generated or come from the permissively-licensed corpora `scripts/fetch-corpora.sh` pins.
- Public API: every member needs `<summary>` (+ `<example>` on primary methods) — the build enforces it.
- Errors: throw `PlumePdfException` (or a subclass) with a stable `PLUME####` code — never a bare `Exception`. Every code minted in `src/` needs a `docs/errors/PLUME####.md` page and an index row in `docs/errors/README.md` (`scripts/check-error-docs.sh`); a retired code's page says "deprecated" on its first line. Codes are never renumbered.
- **Exception policy:** BCL argument exceptions (`ArgumentNullException.ThrowIfNull`, etc.) at public entry points for programmer error (null/invalid arguments); `PlumePdfException` + a `PLUME####` code for every document/format/IO/recovery failure. Internal layers assume validated input from the layer above — no defensive re-checking.
- **Diagnostics scoping:** `doc.Diagnostics` is the open/parse-time surface only. Extraction and rasterization results carry their own result-scoped `Diagnostics`; `doc.Diagnostics` gets one first-occurrence summary entry per page per call, not every deviation from every pass.
- Everything in `src/` must stay NativeAOT-compatible: no reflection (not even `Enum.IsDefined`), no `Reflection.Emit`; source generators or explicit code instead. Architecture tests ban mutable statics and `System.Math` transcendentals in the rasterizer (determinism).
- New features need tests; bug fixes need a regression test. Writer features respect `PdfOptions.Deterministic` (byte-identical output).
- `Rasterize` never mutates the opened document (the read-only invariant, proven by `RasterReadOnlyInvariantTests`); render-time appearance synthesis runs on a scratch, discarded object registry.
- Codec and rasterizer fast paths are pinned to their general paths by committed differential tests (byte-for-byte), never by a one-time comparison.
- Tuned thresholds are governed: loosening one — lowering an SSIM floor (`tests/PlumePdf.CorpusTests/thresholds/ssim.json`), widening the JPX tolerance, or raising a raster perf baseline or its tolerance — needs an `SSIM-LOOSEN:` / `TOL-LOOSEN:` / `PERF-LOOSEN:` justification in a commit message, or CI fails.
- **Analyzer package:** the `PlumePdf` NuGet package also ships a build-time-only Roslyn analyzer under `analyzers/dotnet/cs/` — one runtime assembly, one analyzer assembly, one package. `PLMP####` ids are a separate space from `PLUME####` codes and are documented the same way, one page per id under `docs/analyzers/`.
- **Runtime dependencies:** `src/PlumePdf/PlumePdf.csproj` carries exactly one `PackageReference` — `System.Security.Cryptography.Pkcs`, pinned to an exact `8.0.x` patch (Microsoft-owned, zero transitive deps on `net8.0`, AOT-cleared). It is a deliberate exception to the zero-dependency posture, not a precedent; `scripts/check-provenance.sh` forbids BouncyCastle and every competitor package.
- **Status prose:** live docs describe the shipped state — never "waits on" / "awaits" a PR (`scripts/check-doc-drift.sh`). History belongs in `CHANGELOG.md`.

## Known limitations

Recorded rather than implied fixed:

- Bidi: explicit isolates (LRI/RLI/FSI/PDI) are a coded refusal (`PLUME9013`); embeddings/overrides get correct levels but weak/neutral resolution is one flat pass rather than per X10 isolating-run sequence.
- Rasterizer: transfer functions beyond soft masks, BG/UCR and halftone dictionaries are not applied (`Info` diagnostics `PLUME7740`–`7742`); images inside tiling patterns don't paint; there is no public optional-content layer-toggling API; no CJK substitute font (`PLUME7511`); raster output is byte-deterministic per platform only; glyph aliasing under `AntiAlias = false` has no PDFium oracle.
- Codecs: JBIG2 Huffman and halftone regions are unsupported; JPEG 2000 is Part 1 only (Part 2, ROI, `PPM`/`PPT`, precision > 16 are coded refusals in `PLUME3700`–`3718`).
- Fonts: `CffParser`/`Type1Parser` have no diagnostics channel — malformed charset data degrades silently to substitution.
- Perf baselines are single-run seeds, not yet statistically calibrated; the perf gate is one-sided, so carry local before/after measurements for any raster hot-path change.
