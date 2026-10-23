#!/usr/bin/env bash
# Mechanical enforcement of the raster perf-baseline governance ruling: the SSIM-LOOSEN shape
# (scripts/check-ssim-threshold.sh) generalized from one threshold to N per-benchmark values.
#
# Two INDEPENDENT checks, both run every invocation:
#
#   1. Governance (always runs, needs no measurement): compares
#      benchmarks/perf-baselines/raster-baselines.json's working-tree values against its values
#      at BASE_REF (default: the merge-base with origin/main, or origin/main itself). A LOOSENED
#      gate — the top-level suiteTolerance raised, a benchmark's baselineMs or relativeTolerance
#      raised, a baselineMs set to zero or below, a benchmark taken out of the reference set
#      (its "reference" no longer the JSON value true), or a benchmark removed — requires the
#      literal token "PERF-LOOSEN:" somewhere in a commit message from BASE_REF..HEAD, or this
#      fails. Unchanged or tightened values, and added benchmarks, always pass.
#
#   2. Measured-vs-stored (only when MEASURED_JSON is given): parses a BenchmarkDotNet full JSON
#      export (JsonExporter.Full, wired into benchmarks/PlumePdf.Benchmarks/Program.cs). For each
#      benchmark the baseline file names, r = measured mean / baselineMs (its speed index). Then:
#        relative — r divided by the geometric mean of r over the OTHER "reference" benchmarks
#                   must not exceed 1 + relativeTolerance. Hosted runners differ in CPU model and
#                   load, but a slow runner is slow on every benchmark, so this ratio barely moves
#                   with the runner while a regression in one code path moves it fully.
#        suite    — the geometric mean of r over every reference benchmark must not exceed
#                   1 + suiteTolerance: the backstop for a slowdown that hits everything at once,
#                   which the relative check cancels by design. It sees raw runner speed, so it
#                   is the loose one.
#      A benchmark missing from the export fails (a run that measured nothing must not pass), as
#      does a baselineMs or measured mean that isn't a positive finite number, or a reference set
#      of fewer than two benchmarks. Benchmarks in the export that the file doesn't name are not
#      gated.
#      Lowering one reference benchmark's baselineMs raises its own r (stricter for it) and
#      raises the others' reference slightly (looser for them, by the 1/(n-1) power); it can't
#      be used to loosen the gate without failing that benchmark first.
#
# Usage: scripts/check-raster-perf-baseline.sh [MEASURED_JSON] [BASE_REF]
#        BASELINE_FILE=<path> overrides the baseline file.
#        SKIP_GOVERNANCE=1 runs only the measured check (scripts/harvest-raster-perf-readings.sh
#        replays old readings against uncommitted baselines with it; CI never sets it).
set -euo pipefail
cd "$(dirname "$0")/.."

BASELINE_FILE="${BASELINE_FILE:-benchmarks/perf-baselines/raster-baselines.json}"
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

if [ "${SKIP_GOVERNANCE:-}" = "1" ]; then
  echo "check-raster-perf-baseline: SKIP_GOVERNANCE=1 — skipping the governance diff." >&2
elif [ -z "$OLD_CONTENT" ]; then
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

  OLD_SUITE="$(jq -r '.suiteTolerance // "null"' <<<"$OLD_CONTENT")"
  NEW_SUITE="$(jq -r '.suiteTolerance // "null"' "$BASELINE_FILE")"
  check_loosened "top-level suiteTolerance" "$OLD_SUITE" "$NEW_SUITE"

  NEW_KEYS="$(jq -r '.benchmarks | keys[]' "$BASELINE_FILE")"
  while IFS= read -r key; do
    [ -z "$key" ] && continue
    for field in baselineMs relativeTolerance; do
      OLD_V="$(jq -r --arg k "$key" --arg f "$field" '.benchmarks[$k][$f] // "null"' <<<"$OLD_CONTENT")"
      NEW_V="$(jq -r --arg k "$key" --arg f "$field" '.benchmarks[$k][$f] // "null"' "$BASELINE_FILE")"
      check_loosened "${key}.${field}" "$OLD_V" "$NEW_V"
    done

    # The measured check counts a benchmark as a reference member only when "reference" is the
    # JSON value true, so anything else it becomes (false, deleted, "true", 1) takes it out.
    OLD_REF="$(jq -r --arg k "$key" '.benchmarks[$k].reference == true' <<<"$OLD_CONTENT")"
    NEW_REF="$(jq -r --arg k "$key" '.benchmarks[$k].reference == true' "$BASELINE_FILE")"
    if [ "$OLD_REF" = "true" ] && [ "$NEW_REF" != "true" ]; then
      echo "check-raster-perf-baseline: ${key} taken out of the reference set." >&2
      loosened_without_token="1"
    fi

    # A baseline at or below zero makes the measured check's ratios meaningless.
    NEW_MS="$(jq -r --arg k "$key" '.benchmarks[$k].baselineMs // "null"' "$BASELINE_FILE")"
    if [ "$NEW_MS" != "null" ] && [ "$(awk -v v="$NEW_MS" 'BEGIN { print (v + 0 <= 0) ? "1" : "0" }')" = "1" ]; then
      echo "check-raster-perf-baseline: ${key}.baselineMs is not positive (${NEW_MS})." >&2
      loosened_without_token="1"
    fi
  done <<<"$NEW_KEYS"

  # A benchmark removed from the file is no longer gated at all.
  REMOVED_KEYS="$(jq -r --slurpfile new "$BASELINE_FILE" '.benchmarks | keys - ($new[0].benchmarks | keys) | .[]' <<<"$OLD_CONTENT")"
  while IFS= read -r key; do
    [ -z "$key" ] && continue
    echo "check-raster-perf-baseline: ${key} removed." >&2
    loosened_without_token="1"
  done <<<"$REMOVED_KEYS"

  if [ -n "$loosened_without_token" ]; then
    if grep -qF "PERF-LOOSEN:" <<<"$COMMIT_MESSAGES"; then
      echo "check-raster-perf-baseline: baseline(s) loosened but a commit in ${BASE_REF}..HEAD carries the 'PERF-LOOSEN:' justification token. Governance PASS." >&2
    else
      echo "check-raster-perf-baseline: FAIL — baseline value(s) loosened in ${BASELINE_FILE} without a 'PERF-LOOSEN:' justification token in any commit message between ${BASE_REF} and HEAD." >&2
      echo "Add a commit message containing a line like:" >&2
      echo "  PERF-LOOSEN: <why the gate is being loosened, e.g. a re-calibration on the CI runner platform>" >&2
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
  CPU="$(jq -r '.HostEnvironmentInfo | "\(.ProcessorName // "unknown CPU"), \(.LogicalCoreCount // "?") logical cores"' "$MEASURED_JSON")"
  echo "check-raster-perf-baseline: runner CPU: ${CPU} (the bands assume 4 logical cores)." >&2

  # One line per result: "<kind> <PASS|FAIL> <message>". BenchmarkDotNet reports
  # Statistics.Mean in nanoseconds and names each report "Namespace.Type.Method"; the baseline
  # keys are "Type.Method" in the PlumePdf.Benchmarks namespace, matched exactly.
  RESULTS="$(jq -r --slurpfile measured "$MEASURED_JSON" '
    def gm: map(log) | add / length | exp;
    def f2: . * 100 | round / 100 | tostring;
    def good: type == "number" and . > 0 and (isinfinite | not) and (isnan | not);
    . as $base
    | ($base.benchmarks | to_entries | map(
        .key as $k
        | ([$measured[0].Benchmarks[] | select(.FullName == "PlumePdf.Benchmarks." + $k)][0].Statistics.Mean) as $ns
        | {key: $k, ref: (.value.reference == true), tol: (.value.relativeTolerance // 0),
           base: .value.baselineMs, ms: (if $ns == null then null else $ns / 1000000 end)})) as $all
    | ($all | map(select(.ms == null))) as $missing
    | ($all | map(select(.ms != null and ((.ms | good | not) or (.base | good | not))))) as $invalid
    | ($all | map(select(.ref)) | length) as $refs
    | if ($missing | length) > 0 or ($invalid | length) > 0 or $refs < 2 then
        ($missing[] | "missing FAIL \(.key): no measured result in the export."),
        ($invalid[] | "invalid FAIL \(.key): its baselineMs (\(.base)) or measured mean (\(.ms)) is not a positive finite number."),
        (if $refs < 2 then "reference FAIL fewer than two benchmarks are marked \"reference\": true (\($refs))." else empty end)
      else
        ($all | map(.r = .ms / .base)) as $all
        | ($all[] as $b
          | ([$all[] | select(.ref and .key != $b.key) | .r] | gm) as $refr
          | ($b.r / $refr) as $q
          | "relative \(if $q > 1 + $b.tol then "FAIL" else "PASS" end) \($b.key): \($b.ms | f2) ms, \($b.r | f2)x its baseline \($b.base) ms; \($q | f2)x the reference set (limit \(1 + $b.tol | f2)x)."),
        ([$all[] | select(.ref) | .r] | gm) as $s
        | "suite \(if $s > 1 + ($base.suiteTolerance // 0) then "FAIL" else "PASS" end) reference-set speed \($s | f2)x its baselines (limit \(1 + ($base.suiteTolerance // 0) | f2)x)."
      end
  ' "$BASELINE_FILE")"

  while IFS= read -r line; do
    [ -z "$line" ] && continue
    verdict="$(cut -d' ' -f2 <<<"$line")"
    message="$(cut -d' ' -f3- <<<"$line")"
    if [ "$verdict" = "FAIL" ]; then
      echo "check-raster-perf-baseline: FAIL — ${message}" >&2
      status=1
    else
      echo "check-raster-perf-baseline: ${message} PASS." >&2
    fi
  done <<<"$RESULTS"
fi

if [ "$status" -eq 0 ]; then
  echo "check-raster-perf-baseline: all checks passed." >&2
fi
exit "$status"
