#!/usr/bin/env bash
# Mechanical enforcement of AGENTS.md's "Codes are documented one page per
# code under docs/errors/" rule: every PLUME#### literal minted in src/ (a
# Code = "PLUME####" thrown as a PlumePdfException, or recorded as a
# PdfDiagnostic) must have a matching docs/errors/PLUME####.md page, and
# the index README's table must list every page that exists — so a page
# can't be minted and silently drift out of the index, or removed without
# the index noticing.
#
# Same mechanism, separate ID space: every PLMP#### literal minted in
# analyzers/ (a Roslyn analyzer DiagnosticId) must have a matching
# docs/analyzers/PLMP####.md page, and every such page must still
# correspond to a minted id. PLMP has no index-README requirement
# (docs/analyzers/ carries per-code pages only).
#
# Exits 0 with no output beyond progress lines when clean; exits 1 and
# prints every violation found (does not stop at the first one) otherwise.
set -euo pipefail
cd "$(dirname "$0")/.."

status=0
fail() {
  echo "ERROR-DOCS VIOLATION: $1" >&2
  status=1
}

echo "== checking src/ codes have a docs/errors/ page =="
minted=$(grep -rohE '"PLUME[0-9]{4}"' src/ | tr -d '"' | sort -u)
documented=$(find docs/errors -maxdepth 1 -name 'PLUME*.md' -exec basename {} .md \; | sort -u)

for code in $minted; do
  if [ ! -f "docs/errors/${code}.md" ]; then
    fail "PLUME code '$code' is thrown/recorded in src/ but has no docs/errors/${code}.md page."
  fi
done

for code in $documented; do
  if ! grep -qF "\"$code\"" -r src/ 2>/dev/null; then
    # README.md's own policy: "if a failure mode is retired, its page stays and is marked
    # deprecated rather than deleted" — a page whose title line says so (case-insensitively)
    # satisfies that policy instead of drifting out of sync forever; anything else minted-once
    # but now silent must still be retired explicitly, not just left looking current.
    if ! head -n 1 "docs/errors/${code}.md" | grep -qi 'deprecated'; then
      fail "docs/errors/${code}.md exists but no src/ site mints '$code' any more (retire the page as deprecated per README.md, don't just leave it looking current)."
    fi
  fi
done

echo "== checking docs/errors/README.md's index lists every page =="
for code in $documented; do
  if ! grep -qF "[$code]($code.md)" docs/errors/README.md; then
    fail "docs/errors/${code}.md exists but is missing from the index table in docs/errors/README.md."
  fi
done

echo "== checking analyzers/ PLMP diagnostic ids have a docs/analyzers/ page =="
if [ -d analyzers ]; then
  minted_plmp=$(grep -rohE '"PLMP[0-9]{4}"' analyzers/ | tr -d '"' | sort -u)
  documented_plmp=$(find docs/analyzers -maxdepth 1 -name 'PLMP*.md' -exec basename {} .md \; 2>/dev/null | sort -u)

  for code in $minted_plmp; do
    if [ ! -f "docs/analyzers/${code}.md" ]; then
      fail "PLMP id '$code' is defined in analyzers/ but has no docs/analyzers/${code}.md page."
    fi
  done

  for code in $documented_plmp; do
    if ! grep -qF "\"$code\"" -r analyzers/ 2>/dev/null; then
      fail "docs/analyzers/${code}.md exists but no analyzer defines '$code' any more (retire the page as deprecated, don't just leave it looking current)."
    fi
  done
fi

if [ "$status" -eq 0 ]; then
  echo "OK: every minted PLUME code has a docs/errors/ page, the index is in sync, and every minted PLMP id has a docs/analyzers/ page."
fi

exit "$status"
