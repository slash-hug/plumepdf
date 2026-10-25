#!/usr/bin/env bash
# Self-test for scripts/check-raster-perf-baseline.sh: runs the gate against synthetic
# BenchmarkDotNet exports built from the committed baselines, and its governance check against a
# scratch git repository, and fails if any verdict differs from the expected one. CI runs it
# before the real measured check (ci.yml's raster-perf-gate job), so a broken gate can't pass
# silently.
#
# Usage: scripts/test-raster-perf-gate.sh
set -euo pipefail
cd "$(dirname "$0")/.."

BASELINES="benchmarks/perf-baselines/raster-baselines.json"
GATE="scripts/check-raster-perf-baseline.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
failures=0

# Writes a BenchmarkDotNet-shaped export: every baseline benchmark at baselineMs * scale, times
# its own factor from OVERRIDES ("Key=factor,..."); a factor of 0 leaves the benchmark out.
export_run() {
  local out="$1" scale="$2" overrides="${3:-}"
  jq --argjson scale "$scale" --arg overrides "$overrides" '
    ($overrides | split(",") | map(select(length > 0) | split("=") | {key: .[0], value: (.[1] | tonumber)}) | from_entries) as $o
    | {HostEnvironmentInfo: {ProcessorName: "Synthetic CPU"},
       Benchmarks: [.benchmarks | to_entries[]
         | (($o[.key | split(".")[1]]) // 1) as $f
         | select($f != 0)
         | {FullName: ("PlumePdf.Benchmarks." + .key), Statistics: {Mean: (.value.baselineMs * $scale * $f * 1000000)}}]}
  ' "$BASELINES" > "$out"
}

# The measured cases run a copy of the gate in a scratch repository with no origin, so its
# governance check (which this script tests separately below) has nothing to diff and skips.
MEASURED_REPO="$WORK/measured"
mkdir -p "$MEASURED_REPO/scripts" "$MEASURED_REPO/$(dirname "$BASELINES")"
cp "$GATE" "$MEASURED_REPO/scripts/"
cp "$BASELINES" "$MEASURED_REPO/$BASELINES"
git -C "$MEASURED_REPO" init -q

# expect <PASS|FAIL> <name> <scale> [overrides] [pattern the output must contain]
expect() {
  local want="$1" name="$2" scale="$3" overrides="${4:-}" pattern="${5:-}"
  local json="$WORK/run.json" log="$WORK/run.log" got
  export_run "$json" "$scale" "$overrides"
  if "$MEASURED_REPO/scripts/$(basename "$GATE")" "$json" > "$log" 2>&1; then got=PASS; else got=FAIL; fi
  if [ "$got" != "$want" ]; then
    echo "test-raster-perf-gate: FAIL — '${name}': expected ${want}, got ${got}." >&2
    sed 's/^/    /' "$log" >&2
    failures=$((failures + 1))
  elif [ -n "$pattern" ] && ! grep -qE "$pattern" "$log"; then
    echo "test-raster-perf-gate: FAIL — '${name}': output lacks /${pattern}/." >&2
    sed 's/^/    /' "$log" >&2
    failures=$((failures + 1))
  else
    echo "test-raster-perf-gate: ok — ${name} (${got})." >&2
  fi
}

# Measured check. Several cases assume today's bands (e.g. image fails at 1.6x because its
# relativeTolerance is 0.5); a recalibration that moves a band may need a case updated here.
expect PASS "every benchmark at its baseline" 1.0
expect PASS "a runner 1.6x slower on everything" 1.6
expect PASS "a runner 0.55x as fast on everything" 0.55
expect FAIL "image path 1.6x slower, typical runner" 1.0 "RasterizeImageHeavyPage=1.6" "FAIL — RasterizeBenchmarks\.RasterizeImageHeavyPage"
expect FAIL "image path 1.6x slower, on a fast runner" 0.6 "RasterizeImageHeavyPage=1.6" "FAIL — RasterizeBenchmarks\.RasterizeImageHeavyPage"
expect FAIL "JPX path 1.35x slower, on a slow runner" 1.4 "RasterizeJpxScanPage=1.35" "FAIL — RasterizeBenchmarks\.RasterizeJpxScanPage"
expect FAIL "parallel throughput 2x slower" 1.0 "RasterizeFourDocumentsInParallel=2" "FAIL — RasterizeBenchmarks\.RasterizeFourDocumentsInParallel"
expect PASS "transparency 2x slower (its band is 2.25x)" 1.0 "RasterizeTransparencyHeavyPage=2"
expect FAIL "everything 1.8x slower" 1.8 "" "FAIL — reference-set speed"
expect FAIL "a benchmark missing from the export" 1.0 "RasterizeScanPageMinify=0" "no measured result"
expect PASS "one reference benchmark 2x faster" 1.0 "RasterizeJpxScanPage=0.5"

# expect_export <PASS|FAIL> <name> <jq filter applied to a baseline-speed export> [pattern]
expect_export() {
  local want="$1" name="$2" filter="$3" pattern="${4:-}" json="$WORK/run.json" log="$WORK/run.log" got
  export_run "$json" 1.0
  jq -c "$filter" "$json" | sed 's/:123456789}/:NaN}/' > "$json.tmp" && mv "$json.tmp" "$json"
  if "$MEASURED_REPO/scripts/$(basename "$GATE")" "$json" > "$log" 2>&1; then got=PASS; else got=FAIL; fi
  if [ "$got" != "$want" ] || { [ -n "$pattern" ] && ! grep -qE "$pattern" "$log"; }; then
    echo "test-raster-perf-gate: FAIL — '${name}': expected ${want}${pattern:+ matching /${pattern}/}, got ${got}." >&2
    sed 's/^/    /' "$log" >&2
    failures=$((failures + 1))
  else
    echo "test-raster-perf-gate: ok — ${name} (${got})." >&2
  fi
}

# expect_baselines <PASS|FAIL> <name> <jq filter applied to the baselines> <scale> [overrides] [pattern]
expect_baselines() {
  local want="$1" name="$2" filter="$3" scale="$4" overrides="${5:-}" pattern="${6:-}" saved="$WORK/baselines.saved"
  cp "$MEASURED_REPO/$BASELINES" "$saved"
  jq "$filter" "$saved" > "$MEASURED_REPO/$BASELINES"
  local json="$WORK/run.json" log="$WORK/run.log" got
  BASELINES="$saved" export_run "$json" "$scale" "$overrides"
  if "$MEASURED_REPO/scripts/$(basename "$GATE")" "$json" > "$log" 2>&1; then got=PASS; else got=FAIL; fi
  cp "$saved" "$MEASURED_REPO/$BASELINES"
  if [ "$got" != "$want" ] || { [ -n "$pattern" ] && ! grep -qE "$pattern" "$log"; }; then
    echo "test-raster-perf-gate: FAIL — '${name}': expected ${want}${pattern:+ matching /${pattern}/}, got ${got}." >&2
    sed 's/^/    /' "$log" >&2
    failures=$((failures + 1))
  else
    echo "test-raster-perf-gate: ok — ${name} (${got})." >&2
  fi
}

JPX_MEAN='.Benchmarks[] | select(.FullName | endswith(".RasterizeJpxScanPage")) | .Statistics.Mean'
# jq writes nan back out as null, so the literal NaN BenchmarkDotNet can emit is spliced in as text.
expect_export FAIL "a NaN measured mean" "(${JPX_MEAN}) = 123456789" "not a positive finite"
expect_export FAIL "a negative measured mean" "(${JPX_MEAN}) = -1" "not a positive finite"
expect_export PASS "an unknown extra benchmark in the export" '.Benchmarks += [{FullName: "PlumePdf.Benchmarks.RasterizeBenchmarks.RasterizeSomethingNew", Statistics: {Mean: 1}}]'
expect_export FAIL "a benchmark matched only by a longer name" '(.Benchmarks[] | select(.FullName | endswith(".RasterizeJpxScanPage")) | .FullName) = "PlumePdf.Benchmarks.Other.RasterizeBenchmarks.RasterizeJpxScanPage"' "no measured result"
expect_baselines FAIL "a negative baseline with everything 3x slower" '.benchmarks["RasterizeBenchmarks.RasterizeJpxScanPage"].baselineMs = -1' 3.0 "" "not a positive finite"
expect_baselines FAIL "fewer than two reference benchmarks" '.benchmarks |= with_entries(.value.reference = (.key == "RasterizeBenchmarks.RasterizeTextHeavyPage"))' 1.0 "" "fewer than two"
# The reference is a geometric mean: with the two reference benchmarks at 4x and 0.25x, every
# other benchmark's reference is exactly 1 (an arithmetic mean would make it 2.125), so a
# benchmark at its baseline prints 1x the reference set.
expect_baselines PASS "the reference is a geometric mean" '.benchmarks |= with_entries(.value.reference = (.key | test("TextHeavy|ImageHeavy"))) | .benchmarks |= with_entries(.value.relativeTolerance = 100) | .suiteTolerance = 10' 1.0 "RasterizeTextHeavyPage=4,RasterizeImageHeavyPage=0.25" "RasterizeJpxScanPage: .*; 1x the reference set"
# A benchmark exactly at its limit passes; the limit is inclusive.
expect_baselines PASS "a benchmark exactly at its limit" '.benchmarks["RasterizeBenchmarks.RasterizeJpxScanPage"].relativeTolerance = 1' 1.0 "RasterizeJpxScanPage=2"

# Governance: commit the gate and its baselines to a scratch repository, change the baselines in a
# second commit, and check the verdict with and without the PERF-LOOSEN: token.
governance() {
  local want="$1" name="$2" filter="$3" message="$4" repo="$WORK/repo" got
  rm -rf "$repo"
  mkdir -p "$repo/scripts" "$repo/$(dirname "$BASELINES")"
  cp "$GATE" "$repo/scripts/"
  cp "$BASELINES" "$repo/$BASELINES"
  git -C "$repo" init -q
  git -C "$repo" -c user.name=t -c user.email=t@t add -A
  git -C "$repo" -c user.name=t -c user.email=t@t commit -qm base
  local base
  base="$(git -C "$repo" rev-parse HEAD)"
  jq "$filter" "$BASELINES" > "$repo/$BASELINES"
  git -C "$repo" -c user.name=t -c user.email=t@t commit -q --allow-empty -am "$message"
  if "$repo/scripts/$(basename "$GATE")" "" "$base" > "$WORK/gov.log" 2>&1; then got=PASS; else got=FAIL; fi
  if [ "$got" != "$want" ]; then
    echo "test-raster-perf-gate: FAIL — governance '${name}': expected ${want}, got ${got}." >&2
    sed 's/^/    /' "$WORK/gov.log" >&2
    failures=$((failures + 1))
  else
    echo "test-raster-perf-gate: ok — governance ${name} (${got})." >&2
  fi
}

TEXT='.benchmarks["RasterizeBenchmarks.RasterizeTextHeavyPage"]'
governance PASS "unchanged" '.' "no change"
governance PASS "a tolerance lowered" "${TEXT}.relativeTolerance -= 0.1" "tighten"
governance PASS "a baseline lowered" "${TEXT}.baselineMs -= 1" "tighten"
governance FAIL "a tolerance raised" "${TEXT}.relativeTolerance += 0.1" "loosen"
governance FAIL "a baseline raised" "${TEXT}.baselineMs += 1" "loosen"
governance FAIL "suiteTolerance raised" ".suiteTolerance += 0.1" "loosen"
governance FAIL "taken out of the reference set" "${TEXT}.reference = false" "loosen"
governance FAIL "the reference field deleted" "${TEXT} |= del(.reference)" "loosen"
governance FAIL "the reference field made a string" "${TEXT}.reference = \"true\"" "loosen"
governance FAIL "a benchmark removed" 'del(.benchmarks["RasterizeBenchmarks.RasterizeTextHeavyPage"])' "loosen"
governance FAIL "a baseline made negative" "${TEXT}.baselineMs = -1" "loosen"
governance PASS "a tolerance raised with the token" "${TEXT}.relativeTolerance += 0.1" "PERF-LOOSEN: test"

if [ "$failures" -gt 0 ]; then
  echo "test-raster-perf-gate: ${failures} case(s) failed." >&2
  exit 1
fi
echo "test-raster-perf-gate: all cases passed." >&2
