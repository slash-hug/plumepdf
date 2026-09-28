#!/usr/bin/env bash
# Mechanical enforcement of the Phase 9 raster perf-baseline governance ruling: the
# SSIM-LOOSEN shape (scripts/check-ssim-threshold.sh) generalized from one threshold to N
# per-benchmark baselines.
#
# Two INDEPENDENT checks, both run every invocation:
#
#   1. Governance (always runs, needs no measurement): compares
#      benchmarks/perf-baselines/raster-baselines.json's working-tree values against its values
#      at BASE_REF (default: the merge-base with origin/main, or origin/main itself). Per
#      benchmark, and for the top-level default toleranceFraction: unchanged or TIGHTENED
#      (baselineMs lower, or toleranceFraction lower — a stricter gate) always passes; a LOOSENED
#      value (baselineMs or toleranceFraction raised) requires the literal token "PERF-LOOSEN:"
#      somewhere in a commit message from BASE_REF..HEAD, or this fails. A benchmark key present
#      only in the working tree (newly added) or only at BASE_REF (removed) is not a comparison
#      and never requires justification.
#
#   2. Measured-vs-stored (only when MEASURED_JSON is given and exists): parses a BenchmarkDotNet
#      full JSON export (JsonExporter.Full, wired into benchmarks/PlumePdf.Benchmarks/Program.cs)
#      and, for every "PlumePdf.Benchmarks.RasterizeBenchmarks.*" entry the baseline file also
#      names, fails if the measured mean exceeds baselineMs * (1 + effective toleranceFraction).
#      Skipped (with a note, not a failure) when no MEASURED_JSON is given — this suite is NOT a
#      per-commit CI gate on raw numbers (mirroring every other benchmarks/ suite here);
#      the CI perf-gate job (ci.yml) supplies one from a scoped, fast run.
#
# Usage: scripts/check-raster-perf-baseline.sh [MEASURED_JSON] [BASE_REF]
set -euo pipefail
cd "$(dirname "$0")/.."

BASELINE_FILE="benchmarks/perf-baselines/raster-baselines.json"
MEASURED_JSON="${1:-}"
BASE_REF="${2:-}"

if ! command -v jq >/dev/null 2>&1; then
  echo "check-raster-perf-baseline: jq is required but not found on PATH." >&2
  exit 1
fi

if [ ! -f "$BASELINE_FILE" ]; then
  echo "check-raster-perf-baseline: $BASELINE_FILE does not exist in the working tree — nothing to check." >&2
  exit 0
fi

status=0

# ---------------------------------------------------------------------------
# 1. Governance: working tree vs BASE_REF, SSIM-LOOSEN shape.
# ---------------------------------------------------------------------------
if [ -z "$BASE_REF" ]; then
  if git rev-parse --verify --quiet origin/main >/dev/null; then
    BASE_REF="$(git merge-base HEAD origin/main 2>/dev/null || echo origin/main)"
  else
    echo "check-raster-perf-baseline: no origin/main and no BASE_REF given — skipping the governance diff." >&2
    BASE_REF=""
  fi
fi

OLD_CONTENT=""
if [ -n "$BASE_REF" ]; then
  OLD_CONTENT="$(git show "${BASE_REF}:${BASELINE_FILE}" 2>/dev/null || true)"
fi

if [ -z "$OLD_CONTENT" ]; then
  echo "check-raster-perf-baseline: $BASELINE_FILE did not exist at ${BASE_REF:-<no base ref>} — first-ever creation (or no base to diff against), passing the governance check." >&2
else
  COMMIT_MESSAGES="$(git log --format=%B "${BASE_REF}..HEAD" 2>/dev/null || true)"
  loosened_without_token=""

  check_loosened() {
    local label="$1" old="$2" new="$3"
    [ "$old" = "null" ] && return 0
    [ "$new" = "null" ] && return 0
    local is_loosened
    is_loosened="$(awk -v old="$old" -v new="$new" 'BEGIN { print (new > old) ? "1" : "0" }')"
    if [ "$is_loosened" = "1" ]; then
      echo "check-raster-perf-baseline: ${label} loosened (${old} -> ${new})." >&2
      loosened_without_token="1"
    fi
  }

  OLD_DEFAULT_TOLERANCE="$(jq -r '.toleranceFraction // 0' <<<"$OLD_CONTENT")"
  NEW_DEFAULT_TOLERANCE="$(jq -r '.toleranceFraction // 0' "$BASELINE_FILE")"
  check_loosened "top-level default toleranceFraction" "$OLD_DEFAULT_TOLERANCE" "$NEW_DEFAULT_TOLERANCE"

  NEW_KEYS="$(jq -r '.benchmarks | keys[]' "$BASELINE_FILE")"
  while IFS= read -r key; do
    [ -z "$key" ] && continue
    OLD_MS="$(jq -r --arg k "$key" '.benchmarks[$k].baselineMs // "null"' <<<"$OLD_CONTENT")"
    NEW_MS="$(jq -r --arg k "$key" '.benchmarks[$k].baselineMs // "null"' "$BASELINE_FILE")"
    check_loosened "${key}.baselineMs" "$OLD_MS" "$NEW_MS"

    OLD_TOL="$(jq -r --arg k "$key" '.benchmarks[$k].toleranceFraction // "null"' <<<"$OLD_CONTENT")"
    NEW_TOL="$(jq -r --arg k "$key" '.benchmarks[$k].toleranceFraction // "null"' "$BASELINE_FILE")"
    check_loosened "${key}.toleranceFraction (per-benchmark override)" "$OLD_TOL" "$NEW_TOL"
  done <<<"$NEW_KEYS"

  if [ -n "$loosened_without_token" ]; then
    if grep -qF "PERF-LOOSEN:" <<<"$COMMIT_MESSAGES"; then
      echo "check-raster-perf-baseline: baseline(s) loosened but a commit in ${BASE_REF}..HEAD carries the 'PERF-LOOSEN:' justification token. Governance PASS." >&2
    else
      echo "check-raster-perf-baseline: FAIL — baseline value(s) loosened in ${BASELINE_FILE} without a 'PERF-LOOSEN:' justification token in any commit message between ${BASE_REF} and HEAD." >&2
      echo "Add a commit message containing a line like:" >&2
      echo "  PERF-LOOSEN: <why the baseline is being raised, e.g. a re-calibration on the CI runner platform>" >&2
      status=1
    fi
  else
    echo "check-raster-perf-baseline: no baseline value increased (tightened or unchanged) — governance PASS." >&2
  fi
fi

# ---------------------------------------------------------------------------
# 2. Measured-vs-stored (only when a measured JSON export is supplied).
# ---------------------------------------------------------------------------
if [ -z "$MEASURED_JSON" ]; then
  echo "check-raster-perf-baseline: no MEASURED_JSON given — skipping the measured-vs-stored check (this suite is not a per-commit gate on raw numbers; the CI perf-gate job supplies a measured export)." >&2
elif [ ! -f "$MEASURED_JSON" ]; then
  echo "check-raster-perf-baseline: MEASURED_JSON '${MEASURED_JSON}' does not exist." >&2
  status=1
else
  echo "check-raster-perf-baseline: comparing measured results in ${MEASURED_JSON} against ${BASELINE_FILE}." >&2
  DEFAULT_TOLERANCE="$(jq -r '.toleranceFraction // 0' "$BASELINE_FILE")"

  KEYS="$(jq -r '.benchmarks | keys[]' "$BASELINE_FILE")"
  while IFS= read -r key; do
    [ -z "$key" ] && continue
    BASELINE_MS="$(jq -r --arg k "$key" '.benchmarks[$k].baselineMs' "$BASELINE_FILE")"
    TOLERANCE="$(jq -r --arg k "$key" --argjson def "$DEFAULT_TOLERANCE" '.benchmarks[$k].toleranceFraction // $def' "$BASELINE_FILE")"

    # BenchmarkDotNet's full JSON export names each report "Namespace.Type.Method" and reports
    # Statistics.Mean in nanoseconds; $key here is "Type.Method" (e.g.
    # "RasterizeBenchmarks.RasterizeTextHeavyPage") — matched by suffix so the namespace prefix
    # doesn't have to be duplicated into the baseline file.
    MEASURED_NS="$(jq -r --arg suffix ".${key}" \
      '[.Benchmarks[] | select(.FullName | endswith($suffix))][0].Statistics.Mean // "null"' \
      "$MEASURED_JSON")"

    if [ "$MEASURED_NS" = "null" ]; then
      echo "check-raster-perf-baseline: WARNING — no measured result found for '${key}' in ${MEASURED_JSON}; skipping (not a failure — the measured run may have used --filter)." >&2
      continue
    fi

    MEASURED_MS="$(awk -v ns="$MEASURED_NS" 'BEGIN { printf "%.4f", ns / 1000000.0 }')"
    LIMIT_MS="$(awk -v base="$BASELINE_MS" -v tol="$TOLERANCE" 'BEGIN { printf "%.4f", base * (1 + tol) }')"
    OVER="$(awk -v m="$MEASURED_MS" -v l="$LIMIT_MS" 'BEGIN { print (m > l) ? "1" : "0" }')"

    if [ "$OVER" = "1" ]; then
      echo "check-raster-perf-baseline: FAIL — ${key} measured ${MEASURED_MS} ms, exceeding the baseline ${BASELINE_MS} ms + ${TOLERANCE} tolerance (limit ${LIMIT_MS} ms)." >&2
      status=1
    else
      echo "check-raster-perf-baseline: ${key} measured ${MEASURED_MS} ms <= limit ${LIMIT_MS} ms (baseline ${BASELINE_MS} ms + ${TOLERANCE} tolerance). PASS." >&2
    fi
  done <<<"$KEYS"
fi

if [ "$status" -eq 0 ]; then
  echo "check-raster-perf-baseline: all checks passed." >&2
fi
exit "$status"
