<!-- Thanks! Please read .github/CONTRIBUTING.md first — especially the clean-room rule. -->

## What & why

<!-- What changes, and the problem it solves. Link the issue if one exists. -->

## Provenance (clean-room)

<!-- Where did the logic come from? ISO 32000-1 §…, Arlington TSVs, a permissive source
     (attributed in NOTICE), or original work. PRs derived from restricted sources
     (iText/Aspose/Syncfusion/QuestPDF source, ISO text pasted, veraPDF profiles) are closed. -->

## Checklist

- [ ] `dotnet build PlumePdf.sln -m:1` — zero warnings (they're errors)
- [ ] `dotnet test PlumePdf.sln` from the repo root — green, including architecture tests
- [ ] `dotnet format PlumePdf.sln --verify-no-changes`
- [ ] New/changed behavior has tests (bug fixes: a regression test)
- [ ] Any new `PLUME####` code has its `docs/errors/` page + README index row
      (`bash scripts/check-error-docs.sh`)
- [ ] Public members have XML docs; cookbook snippets edited via their tests + `dotnet mdsnippets`
- [ ] No new dependency in `src/` (or: it was agreed on first and that discussion is linked)
- [ ] Writer-facing changes keep `PdfOptions.Deterministic` byte-identical
