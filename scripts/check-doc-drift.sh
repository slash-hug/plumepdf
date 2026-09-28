#!/usr/bin/env bash
# Documentation-drift gate. Status prose is the one doc class no code<->doc gate covers (error
# pages, snippets and provenance each have their own), so this fails when a live doc describes
# shipped work as still in flight: tracked Markdown under docs/ (except docs/errors, which
# check-error-docs.sh governs), AGENTS.md, CONTEXT.md, the READMEs and .claude/skills must not say
# something "waits on / awaits / pending" a PR or feature. Record what landed instead;
# CHANGELOG.md is history and is exempt. Only tracked files are scanned, so local, gitignored
# notes never trip the gate.
#
# Usage: ./scripts/check-doc-drift.sh   (exit 1 on any violation)
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

pattern='(waits on|awaits|awaiting|pending) (this feature'"'"'s |the |a )?(PR|pull request|merge of|feature'"'"'s PR)'
hits="$(git grep -n -i -E "$pattern" -- 'docs/*.md' 'AGENTS.md' 'CONTEXT.md' 'README.md' 'src/PlumePdf/README.md' '.claude/skills/*.md' ':!docs/errors/*' || true)"
if [[ -n "$hits" ]]; then
  echo "DOC-DRIFT VIOLATION: live docs describe something as waiting on a PR — say what landed instead, or move the sentence to CHANGELOG.md:"
  echo "$hits"
  exit 1
fi
echo "check-doc-drift: no live doc waits on a PR. PASS."
