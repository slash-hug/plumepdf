#!/usr/bin/env bash
# Mechanical enforcement of the JPX tolerance-governance ruling: the same
# "tightening is free, loosening needs a commit-message token" shape as
# scripts/check-raster-perf-baseline.sh (itself generalized from
# scripts/check-ssim-threshold.sh), applied here to TWO files together under one token,
# because either one alone can silently weaken what the oracle gate actually enforces:
#
#   1. tests/PlumePdf.CorpusTests/Fixtures/jpx/jpx-tolerance.json — the two global
#      per-class threshold objects (bit-exact / tolerance-a). Every field is "larger
#      number = looser" by construction (see the file's own "description"). This
#      section walks the UNION of class names and, within each class present on both
#      sides, the UNION of field names — not just one side's keys — so neither
#      "delete tolerance-a, add a same-shaped tolerance-a2" nor "rename a field" can
#      slip a loosened value past a plain key-matching diff (both a missing class and a
#      missing field always require the token, regardless of whether a differently
#      named replacement appears alongside it).
#   2. tests/PlumePdf.CorpusTests/Fixtures/jpx/MANIFEST.json — each fixture's own
#      "toleranceClass" selects WHICH threshold pair (if any) applies to it, so
#      jpx-tolerance.json's numbers can be airtight while every fixture is quietly
#      switched to a weaker class (or deleted outright) and nothing above would catch
#      it. Class strength, strongest to weakest: bit-exact > tolerance-a >
#      {not-compared, metadata-only}; a "refusal:<CODE>" class moving to any
#      non-refusal class also counts as weakened (a fixture that used to prove a
#      refusal firing no longer does), and so does any move INTO a refusal class (a per-sample
#      compare becomes a code assertion), and so does dropping a reference from a fixture's
#      "references" list (fewer components compared). A fixture disappearing from MANIFEST.json
#      entirely is treated the same as a class downgrade.
#
# For both files: a class/field/fixture present only in the working tree (new) never
# needs justification; present only at BASE_REF (removed) always does, regardless of
# what a same-shaped replacement is called. Unchanged or strictly tightened always
# passes without a token.
#
# Usage: scripts/check-jpx-tolerance.sh [BASE_REF]
set -euo pipefail
cd "$(dirname "$0")/.."

TOLERANCE_FILE="tests/PlumePdf.CorpusTests/Fixtures/jpx/jpx-tolerance.json"
MANIFEST_FILE="tests/PlumePdf.CorpusTests/Fixtures/jpx/MANIFEST.json"
BASE_REF="${1:-}"

if ! command -v jq >/dev/null 2>&1; then
  echo "check-jpx-tolerance: jq is required but not found on PATH." >&2
  exit 1
fi

if [ ! -f "$TOLERANCE_FILE" ] && [ ! -f "$MANIFEST_FILE" ]; then
  echo "check-jpx-tolerance: neither $TOLERANCE_FILE nor $MANIFEST_FILE exists in the working tree — nothing to check." >&2
  exit 0
fi

if [ -z "$BASE_REF" ]; then
  if git rev-parse --verify --quiet origin/main >/dev/null; then
    BASE_REF="$(git merge-base HEAD origin/main 2>/dev/null || echo origin/main)"
  else
    echo "check-jpx-tolerance: no origin/main and no BASE_REF given — nothing to diff against, passing." >&2
    exit 0
  fi
fi

status=0
loosened_without_token=""

# Classifies a MANIFEST.json toleranceClass value into a comparable strength: "refusal"
# (a special kind, compared only via the explicit refusal->non-refusal rule below), or
# an integer rank where LARGER = STRONGER (3 bit-exact > 2 tolerance-a > 1
# not-compared/metadata-only). Anything unrecognized ranks 0 — the safest default, since
# it means moving TO an unrecognized class from any known one always looks like a
# weakening (requires the token) rather than silently passing.
classify_rank() {
  case "$1" in
    refusal:*) echo "refusal" ;;
    bit-exact) echo "3" ;;
    tolerance-a) echo "2" ;;
    metadata-only) echo "1" ;;
    not-compared|not-compared:*) echo "1" ;;
    *) echo "0" ;;
  esac
}

# ---------------------------------------------------------------------------
# 1. jpx-tolerance.json: union of class names, union of field names per class.
# ---------------------------------------------------------------------------
OLD_TOL="$(git show "${BASE_REF}:${TOLERANCE_FILE}" 2>/dev/null || true)"
if [ ! -f "$TOLERANCE_FILE" ]; then
  echo "check-jpx-tolerance: ${TOLERANCE_FILE} removed from the working tree — treating as a full loosening, token required." >&2
  loosened_without_token="1"
elif [ -z "$OLD_TOL" ]; then
  echo "check-jpx-tolerance: ${TOLERANCE_FILE} did not exist at ${BASE_REF} — first-ever creation, passing that file's governance." >&2
else
  ALL_CLASSES="$( (jq -r '.classes | keys[]' <<<"$OLD_TOL"; jq -r '.classes | keys[]' "$TOLERANCE_FILE") | sort -u)"
  while IFS= read -r class; do
    [ -z "$class" ] && continue
    OLD_HAS="$(jq -r --arg c "$class" '.classes | has($c)' <<<"$OLD_TOL")"
    NEW_HAS="$(jq -r --arg c "$class" '.classes | has($c)' "$TOLERANCE_FILE")"
    if [ "$OLD_HAS" = "true" ] && [ "$NEW_HAS" = "false" ]; then
      echo "check-jpx-tolerance: class '${class}' removed from ${TOLERANCE_FILE} (a same-shaped replacement under a new name does not exempt this)." >&2
      loosened_without_token="1"
      continue
    fi
    if [ "$OLD_HAS" = "false" ] && [ "$NEW_HAS" = "true" ]; then
      echo "check-jpx-tolerance: class '${class}' is new since ${BASE_REF} — no comparison needed." >&2
      continue
    fi
    ALL_FIELDS="$( (jq -r --arg c "$class" '.classes[$c] | keys[]' <<<"$OLD_TOL"; jq -r --arg c "$class" '.classes[$c] | keys[]' "$TOLERANCE_FILE") | sort -u)"
    while IFS= read -r field; do
      [ -z "$field" ] && continue
      OLD_HAS_FIELD="$(jq -r --arg c "$class" --arg f "$field" '.classes[$c] | has($f)' <<<"$OLD_TOL")"
      NEW_HAS_FIELD="$(jq -r --arg c "$class" --arg f "$field" '.classes[$c] | has($f)' "$TOLERANCE_FILE")"
      if [ "$OLD_HAS_FIELD" = "true" ] && [ "$NEW_HAS_FIELD" = "false" ]; then
        echo "check-jpx-tolerance: field '${class}.${field}' removed from ${TOLERANCE_FILE} (a renamed field does not exempt this)." >&2
        loosened_without_token="1"
        continue
      fi
      if [ "$OLD_HAS_FIELD" = "false" ] && [ "$NEW_HAS_FIELD" = "true" ]; then
        echo "check-jpx-tolerance: field '${class}.${field}' is new since ${BASE_REF} — no comparison needed." >&2
        continue
      fi
      OLD_VAL="$(jq -r --arg c "$class" --arg f "$field" '.classes[$c][$f]' <<<"$OLD_TOL")"
      NEW_VAL="$(jq -r --arg c "$class" --arg f "$field" '.classes[$c][$f]' "$TOLERANCE_FILE")"
      IS_LOOSENED="$(awk -v old="$OLD_VAL" -v new="$NEW_VAL" 'BEGIN { print (new > old) ? "1" : "0" }')"
      if [ "$IS_LOOSENED" = "1" ]; then
        echo "check-jpx-tolerance: ${class}.${field} loosened (${OLD_VAL} -> ${NEW_VAL})." >&2
        loosened_without_token="1"
      else
        echo "check-jpx-tolerance: ${class}.${field} unchanged or tightened (${OLD_VAL} -> ${NEW_VAL}). PASS." >&2
      fi
    done <<<"$ALL_FIELDS"
  done <<<"$ALL_CLASSES"
fi

# ---------------------------------------------------------------------------
# 2. MANIFEST.json: per-fixture toleranceClass strength, union of fixture files.
# ---------------------------------------------------------------------------
OLD_MAN="$(git show "${BASE_REF}:${MANIFEST_FILE}" 2>/dev/null || true)"
if [ ! -f "$MANIFEST_FILE" ]; then
  echo "check-jpx-tolerance: ${MANIFEST_FILE} removed from the working tree — treating as a full loosening, token required." >&2
  loosened_without_token="1"
elif [ -z "$OLD_MAN" ]; then
  echo "check-jpx-tolerance: ${MANIFEST_FILE} did not exist at ${BASE_REF} — first-ever creation, passing that file's governance." >&2
else
  ALL_FILES="$( (jq -r '.fixtures[].file' <<<"$OLD_MAN"; jq -r '.fixtures[].file' "$MANIFEST_FILE") | sort -u)"
  while IFS= read -r fname; do
    [ -z "$fname" ] && continue
    OLD_CLASS="$(jq -r --arg f "$fname" '[.fixtures[] | select(.file == $f) | .toleranceClass][0] // empty' <<<"$OLD_MAN")"
    NEW_CLASS="$(jq -r --arg f "$fname" '[.fixtures[] | select(.file == $f) | .toleranceClass][0] // empty' "$MANIFEST_FILE")"
    if [ -n "$OLD_CLASS" ] && [ -z "$NEW_CLASS" ]; then
      echo "check-jpx-tolerance: fixture '${fname}' (toleranceClass '${OLD_CLASS}') removed from ${MANIFEST_FILE}." >&2
      loosened_without_token="1"
      continue
    fi
    if [ -z "$OLD_CLASS" ] && [ -n "$NEW_CLASS" ]; then
      echo "check-jpx-tolerance: fixture '${fname}' is new since ${BASE_REF} (toleranceClass '${NEW_CLASS}') — no comparison needed." >&2
      continue
    fi
    # A reference present at BASE_REF but gone from the working tree weakens the oracle by
    # exactly as much as a class downgrade (a 3-component bit-exact compare cut to one
    # component), so it needs the token too — union of old references, old-only => loosened.
    OLD_REFS="$(jq -r --arg f "$fname" '[.fixtures[] | select(.file == $f) | .references // [] | .[]][]?' <<<"$OLD_MAN" 2>/dev/null || true)"
    while IFS= read -r ref; do
      [ -z "$ref" ] && continue
      if ! jq -e --arg f "$fname" --arg r "$ref" '[.fixtures[] | select(.file == $f) | .references // [] | .[]] | index($r) != null' "$MANIFEST_FILE" >/dev/null 2>&1; then
        echo "check-jpx-tolerance: fixture '${fname}' reference '${ref}' removed from ${MANIFEST_FILE} (fewer components compared)." >&2
        loosened_without_token="1"
      fi
    done <<<"$OLD_REFS"
    if [ "$OLD_CLASS" = "$NEW_CLASS" ]; then
      echo "check-jpx-tolerance: fixture '${fname}' toleranceClass unchanged ('${OLD_CLASS}'). PASS." >&2
      continue
    fi
    OLD_RANK="$(classify_rank "$OLD_CLASS")"
    NEW_RANK="$(classify_rank "$NEW_CLASS")"
    WEAKENED=0
    # Crossing the refusal boundary in EITHER direction needs the token: refusal -> sample
    # class stops proving the refusal fires; sample class -> refusal stops proving a
    # per-sample decode (strictly less coverage). Only same-kind moves are rank-compared.
    if [ "$OLD_RANK" = "refusal" ] && [ "$NEW_RANK" != "refusal" ]; then
      WEAKENED=1
    elif [ "$OLD_RANK" != "refusal" ] && [ "$NEW_RANK" = "refusal" ]; then
      WEAKENED=1
    elif [ "$OLD_RANK" != "refusal" ] && [ "$NEW_RANK" != "refusal" ]; then
      if [ "$NEW_RANK" -lt "$OLD_RANK" ]; then
        WEAKENED=1
      fi
    fi
    if [ "$WEAKENED" = "1" ]; then
      echo "check-jpx-tolerance: fixture '${fname}' toleranceClass weakened ('${OLD_CLASS}' -> '${NEW_CLASS}')." >&2
      loosened_without_token="1"
    else
      echo "check-jpx-tolerance: fixture '${fname}' toleranceClass changed but not weakened ('${OLD_CLASS}' -> '${NEW_CLASS}'). PASS." >&2
    fi
  done <<<"$ALL_FILES"
fi

# ---------------------------------------------------------------------------
# Verdict: one token covers any loosening found in either file above.
# ---------------------------------------------------------------------------
if [ -n "$loosened_without_token" ]; then
  COMMIT_MESSAGES="$(git log --format=%B "${BASE_REF}..HEAD" 2>/dev/null || true)"
  if grep -qF "TOL-LOOSEN:" <<<"$COMMIT_MESSAGES"; then
    echo "check-jpx-tolerance: tolerance/class value(s) loosened but a commit in ${BASE_REF}..HEAD carries the 'TOL-LOOSEN:' justification token. Governance PASS." >&2
  else
    echo "check-jpx-tolerance: FAIL — a tolerance value, class, field, or fixture was loosened/removed without a 'TOL-LOOSEN:' justification token in any commit message between ${BASE_REF} and HEAD." >&2
    echo "Add a commit message containing a line like:" >&2
    echo "  TOL-LOOSEN: <why the tolerance/class is being widened or removed>" >&2
    status=1
  fi
else
  echo "check-jpx-tolerance: nothing loosened or removed (tightened, unchanged, or newly added) — governance PASS." >&2
fi

if [ "$status" -eq 0 ]; then
  echo "check-jpx-tolerance: all checks passed." >&2
fi
exit "$status"
