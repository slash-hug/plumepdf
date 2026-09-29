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

## Releasing

Releases are automatic once a version bump reaches `main`; the merge is the approval. `main` only
changes through a pull request with passing CI, so a merge is the only way a release can start.

1. Open a PR that sets `<Version>` in `src/PlumePdf/PlumePdf.csproj` and turns
   `CHANGELOG.md`'s `## [Unreleased]` entries for it into a dated `## [<version>] — YYYY-MM-DD`
   section. That section becomes the GitHub Release notes, and the release refuses to run
   without it.
2. Merge it. `.github/workflows/release.yml` builds, tests and packs, pushes the package and
   its symbols to nuget.org, then creates the `v<version>` tag and a GitHub Release (a
   prerelease for a version with a `-` suffix) with the packages attached.
3. Once the new version is listed on nuget.org, move `PackageValidationBaselineVersion` in
   `PlumePdf.csproj` forward to it in a separate PR (not in the bump PR: the baseline package
   has to exist when the release builds). Every PR's pack step checks the public API against
   that version; a deliberate break is recorded with
   `dotnet pack -p:ApiCompatGenerateSuppressionFile=true`, which writes
   `CompatibilitySuppressions.xml` for review.

A published NuGet version can never be replaced, only unlisted, so a bad release is fixed by
the next patch version. If a release fails halfway, open that run in the Actions tab and use
**Re-run failed jobs**: it finishes the same commit, and the NuGet push skips a version that is
already there. Don't start a new run for it — the workflow refuses to release a version that
nuget.org has but that was never tagged, because a new run would tag a different commit. If the
tag was created by hand, create the GitHub Release by hand too; the workflow skips tagged
versions.

## Bug reports and feature requests

Use the issue templates. For anything security-shaped, use `.github/SECURITY.md`'s private
reporting flow instead of a public issue.
