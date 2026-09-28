#!/usr/bin/env bash
# Mechanical enforcement of the Phase 8 SSIM threshold-governance ruling: the repo's first
# tuned-threshold gate, so "tightening is free, loosening requires the commit message to
# record why" becomes CI-mechanical enforcement rather than reviewer vigilance, matching the
# repo's lesson-to-gate doctrine (the same doctrine that turned "static-link the oracle binary"
# from a postmortem into ci.yml's own install-step shape).
#
# Compares tests/PlumePdf.CorpusTests/thresholds/ssim.json's "floor" value in the working tree
# against its value at BASE_REF (default: the merge-base with origin/main, or origin/main
# itself if no merge-base is found — e.g. a shallow checkout). Three outcomes:
#   - the file didn't exist at BASE_REF (first-ever creation): pass, nothing to compare against.
#   - the new floor is >= the old floor (tightened or unchanged): pass, no justification needed.
#   - the new floor is < the old floor (loosened): the diff's own commit message(s), from
#     BASE_REF..HEAD, must contain the literal token "SSIM-LOOSEN:" somewhere, or this fails.
#
# Usage: scripts/check-ssim-threshold.sh [BASE_REF]
set -euo pipefail
cd "$(dirname "$0")/.."

THRESHOLDS_FILE="tests/PlumePdf.CorpusTests/thresholds/ssim.json"
BASE_REF="${1:-}"

if [ -z "$BASE_REF" ]; then
  if git rev-parse --verify --quiet origin/main >/dev/null; then
    BASE_REF="$(git merge-base HEAD origin/main 2>/dev/null || echo origin/main)"
  else
    echo "check-ssim-threshold: no origin/main and no BASE_REF given — nothing to diff against, passing." >&2
    exit 0
  fi
fi

if [ ! -f "$THRESHOLDS_FILE" ]; then
  echo "check-ssim-threshold: $THRESHOLDS_FILE does not exist in the working tree — nothing to check." >&2
  exit 0
fi

NEW_FLOOR="$(jq -r '.floor' "$THRESHOLDS_FILE")"

OLD_CONTENT="$(git show "${BASE_REF}:${THRESHOLDS_FILE}" 2>/dev/null || true)"
if [ -z "$OLD_CONTENT" ]; then
  echo "check-ssim-threshold: $THRESHOLDS_FILE did not exist at ${BASE_REF} — first-ever creation, passing (new floor: ${NEW_FLOOR})." >&2
  exit 0
fi

OLD_FLOOR="$(jq -r '.floor' <<<"$OLD_CONTENT")"

echo "check-ssim-threshold: floor at ${BASE_REF} = ${OLD_FLOOR}; floor in working tree = ${NEW_FLOOR}"

IS_LOOSENED="$(awk -v old="$OLD_FLOOR" -v new="$NEW_FLOOR" 'BEGIN { print (new < old) ? "1" : "0" }')"

# Per-fixture floors: every numeric "floor" under "perTestFloors" is governed exactly like
# the top-level one — a lowered value needs the same SSIM-LOOSEN token.
# A key present at BASE_REF but missing now counts as loosened (the leg lost its floor).
LOOSENED_PER_TEST=""
while IFS=$'\t' read -r key old_pt; do
  [ -z "$key" ] && continue
  new_pt="$(jq -r --arg k "$key" '.perTestFloors[$k].floor // "missing"' "$THRESHOLDS_FILE")"
  if [ "$new_pt" = "missing" ] || [ "$(awk -v old="$old_pt" -v new="$new_pt" 'BEGIN { print (new < old) ? "1" : "0" }')" = "1" ]; then
    LOOSENED_PER_TEST="${LOOSENED_PER_TEST} ${key}(${old_pt}->${new_pt})"
  fi
done < <(jq -r '(.perTestFloors // {}) | to_entries[] | select(.value | type == "object" and has("floor")) | "\(.key)\t\(.value.floor)"' <<<"$OLD_CONTENT")

if [ -n "$LOOSENED_PER_TEST" ]; then
  echo "check-ssim-threshold: per-test floor(s) loosened:${LOOSENED_PER_TEST}" >&2
  IS_LOOSENED=1
fi

if [ "$IS_LOOSENED" != "1" ]; then
  echo "check-ssim-threshold: floor did not decrease (tightened or unchanged) — no justification required. PASS." >&2
  exit 0
fi

# Loosened: every commit message from BASE_REF..HEAD, concatenated, must carry the token.
COMMIT_MESSAGES="$(git log --format=%B "${BASE_REF}..HEAD" 2>/dev/null || true)"

if grep -qF "SSIM-LOOSEN:" <<<"$COMMIT_MESSAGES"; then
  echo "check-ssim-threshold: floor loosened (${OLD_FLOOR} -> ${NEW_FLOOR}) but a commit in ${BASE_REF}..HEAD carries the 'SSIM-LOOSEN:' justification token. PASS." >&2
  exit 0
fi

echo "check-ssim-threshold: FAIL — SSIM floor loosened (top-level ${OLD_FLOOR} -> ${NEW_FLOOR}; per-test:${LOOSENED_PER_TEST:- none}) in ${THRESHOLDS_FILE} without a 'SSIM-LOOSEN:' justification token in any commit message between ${BASE_REF} and HEAD." >&2
echo "Add a commit message containing a line like:" >&2
echo "  SSIM-LOOSEN: <why the floor is being lowered, e.g. a calibration re-run against a wider corpus>" >&2
exit 1
