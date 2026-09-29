# Contributing to PlumePDF

Thanks for considering a contribution. PlumePDF has a few non-negotiable rules that are
unusual enough to read before writing any code — they exist to keep the library's Apache-2.0
licensing and clean-room provenance defensible.

## The clean-room rule (read this first)

PlumePDF is developed clean-room, per the clean-room policy in AGENTS.md:

- **Never** derive code from the source of iText7, Aspose, Syncfusion, or QuestPDF (or any
  other non-permissively-licensed PDF library), and never paste ISO/spec text into the repo.
- Implementations come from ISO 32000-1 (cited by section number), the Arlington PDF model
  TSVs, and permissively licensed sources (PdfPig, PDFsharp, Pdfium — Apache/MIT-class) with
  attribution in `NOTICE`.
- veraPDF's validation profiles and source are equally off-limits for rule derivation;
  running external binaries (qpdf, veraPDF, pdfsig) as *oracles* is fine.
- `scripts/check-provenance.sh` enforces parts of this mechanically and runs in CI.

A PR that cannot state where its logic came from will be asked; one derived from a restricted
source will be closed regardless of quality. By contributing you agree your contribution is
licensed under Apache-2.0 like the rest of the project.

## Getting oriented

`AGENTS.md` is the binding architecture/convention document. The decided plan lives in
`docs/spec.md` and `docs/architecture.md`; `CONTEXT.md` defines the project vocabulary.
Reading `AGENTS.md` end to end (it is short) answers most "how should this be shaped?"
questions.

## Building and testing

```bash
dotnet build PlumePdf.sln -m:1       # warnings are errors; missing XML docs fail the build
dotnet test PlumePdf.sln             # unit + architecture + corpus (corpus self-skips without corpora)
dotnet format PlumePdf.sln --verify-no-changes
./scripts/fetch-corpora.sh           # optional: real-world corpus for the corpus lane
bash scripts/check-error-docs.sh     # every PLUME#### code has a docs/errors/ page
bash scripts/check-provenance.sh     # clean-room / AGPL isolation markers
```

Run `dotnet test` from the repo root only, so the architecture tests always run. Benchmarks
live in a separate solution (`benchmarks/bench.sln`) — competitor packages are referenced
there and only there (the AGPL isolation wall); never add them to `PlumePdf.sln`.

## What a mergeable PR looks like

- **Tests:** new features need tests; bug fixes need a regression test. Writer-facing changes
  must keep `PdfOptions.Deterministic` byte-identical (there are double-save regression tests
  to copy from).
- **Errors:** failures throw `PlumePdfException` with a stable `PLUME####` code (never a bare
  `Exception`), documented one page per code under `docs/errors/` and indexed in its README —
  in the same PR that mints the code.
- **Docs:** every public member carries `<summary>` XML docs (`<example>` on primary methods);
  the build enforces this. Cookbook snippets are embedded from compiling tests via
  MarkdownSnippets (`dotnet mdsnippets`) — edit the test, not the fenced code.
- **AOT:** everything in `src/` stays NativeAOT-compatible — no reflection.
- **Commits:** conventional commits (`feat:`, `fix:`, `test:`, `docs:`, `chore:`) whose body
  explains *why*.
- **Dependencies:** the runtime dependency posture is deliberately near-zero; adding a package
  to `src/` needs to be agreed on first — open an issue before writing the code.

## Bug reports and feature requests

Use the issue templates. For anything security-shaped, use `.github/SECURITY.md`'s private
reporting flow instead of a public issue.
