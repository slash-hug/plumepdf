# Agent-Forward Charter

Agent-forwardness is a core identity pillar: every phase's exit gate includes agent-doc coverage of the new surface. An 18-item checklist is adopted **in full**, staged as follows.

## From Phase 1 (day one)

- **AGENTS.md** at the repo root, agents.md-convention: exact commands to run (and to avoid), module layout, and the two entry-point names (`PdfDocument`, `Pdf`). Command sections name literal commands, never prose descriptions.
- **XML-doc contract, compiler-enforced:** `<summary>` on every public member (build warning → error), `<param>`/`<returns>`/`<exception>` where applicable, and an `<example>` block on every primary API method so a runnable sample surfaces in IntelliSense at the call site.
- **Cookbook, one task per file** (`docs/cookbook/`), Stripe-style: each recipe is a single complete `dotnet run`-able snippet with its expected output shown. Snippets are embedded from compiling, CI-run test source via MarkdownSnippets — docs fail the build when they drift.
- **Verify snapshot tests** back each recipe: an agent locates the recipe by task name, runs the backing test, and diffs `.received` vs `.verified` as objective proof its usage is correct.
- **`PdfOptions.Deterministic` — public, documented, forever:** fixed document ID, fixed/omitted timestamps, stable object ordering → byte-identical output. The backbone of agent self-verification and reproducible builds. Every writer feature must respect it — signing (Phase 5) does so by scoping the guarantee precisely (RSA PKCS#1 v1.5, caller-supplied signing time, no TSA/LTV) and coded-refusing every combination outside it, rather than quietly breaking reproducibility.
- **Coded exceptions as API contract:** `PlumePdfException` base carries a stable, greppable `Code` (`PLUME####`, individually documented, never renumbered) and a `HelpLink` to that code's reference page, with an actionable message. Layout failures add QuestPDF-grade detail: the conflicting constraint in plain language, the element tree with measurements, and the offending call. `[Obsolete("Use X instead.", error: true)]` for hard redirects once replacements stabilize.
- **In-repo Claude skill** (`.claude/skills/plumepdf/SKILL.md`): trigger-precise description, body under ~5K tokens, updated each phase.
- **Agent-oriented NuGet README** embedded in the package (renders on nuget.org; sits in every consumer's package cache for offline discovery): entry points, canonical recipes, error-code index, docs links.

## With the docs site

- **llms.txt** at the site root (H1 + blockquote summary + H2-grouped links) and auto-generated **llms-full.txt**.

## From Phase 2 on

- **First-party Roslyn analyzer + code fixes** for real observed misuse (e.g. a composed Manuscript never rendered), so an agent's build-error loop self-corrects mechanically. Diagnostics are numbered CommunityToolkit-style (`PLMP0001`…), one reference page each; the same scheme covers any source-generator diagnostics (e.g. the AOT-safe `FillForm` mapping generator).

## Conventions already fixed elsewhere

Single-hub discoverability and uniform naming are a fixed contract (two starting names; `Add*`/`Get*`/`Extract*` patterns; the `doc.Objects` escape hatch documented wherever the high-level API is).
