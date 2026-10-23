#!/usr/bin/env bash
# Maintainer tool for re-deriving benchmarks/perf-baselines/raster-baselines.json from the
# per-commit gate's own readings (see that file's calibrationNote). Not run by CI.
#
# Reads the BenchmarkDotNet summary table and the CPU line from the logs of recent
# raster-perf-gate jobs (ci.yml), writes one BenchmarkDotNet-shaped export per job, prints each
# benchmark's median reading (the candidate baselineMs), then replays every job through
# scripts/check-raster-perf-baseline.sh with the working tree's baselines and prints, per
# benchmark, the worst relative ratio seen, plus how many jobs would have failed. Size each
# relativeTolerance from those worst ratios (see the calibrationNote), lower or raise baselineMs
# to the medians, and commit — raising either needs a "PERF-LOOSEN:" token.
#
# Needs: gh (authenticated, with read access to the repository's Actions logs) and jq.
# Usage: scripts/harvest-raster-perf-readings.sh [RUNS=100] [SINCE=YYYY-MM-DD]
#   RUNS  how many recent CI workflow runs to look through (each has one gate job per attempt)
#   SINCE skip runs created before this date, e.g. the date of a deliberate performance change
set -euo pipefail
cd "$(dirname "$0")/.."

RUNS="${1:-100}"
SINCE="${2:-0000-00-00}"
OUT="$(mktemp -d)"
echo "harvest-raster-perf-readings: writing exports to ${OUT}" >&2

for run in $(gh run list --workflow ci.yml -L "$RUNS" --json databaseId,status,createdAt \
    --jq ".[] | select(.status == \"completed\" and .createdAt >= \"${SINCE}\") | .databaseId"); do
  attempts="$(gh api "repos/{owner}/{repo}/actions/runs/${run}" --jq '.run_attempt')"
  for attempt in $(seq 1 "$attempts"); do
    for job in $(gh api "repos/{owner}/{repo}/actions/runs/${run}/attempts/${attempt}/jobs" \
        --jq '.jobs[] | select(.name == "raster-perf-gate" and .conclusion != "skipped") | .id'); do
      log="$(gh api --allow-escape-sequences "repos/{owner}/{repo}/actions/jobs/${job}/logs" 2>/dev/null \
        | sed 's/\x1b\[[0-9;]*m//g' || true)"
      cpu="$(grep -m1 -oE '(AMD|Intel)[^,]*, [0-9]+ CPU, [0-9]+ logical' <<<"$log" || true)"
      [ -z "$cpu" ] && continue
      # Summary rows look like "| RasterizeTextHeavyPage | 41.79 ms | ...".
      grep -E '\| Rasterize[A-Za-z]+ +\| +[0-9.,]+ ms' <<<"$log" \
        | awk -F'|' '{ name = $2; mean = $3; gsub(/ /, "", name); gsub(/ ms| |,/, "", mean); print name "\t" mean }' \
        | jq -R -s --arg cpu "$cpu" '
            {HostEnvironmentInfo: {ProcessorName: ($cpu | sub(", [0-9]+ CPU.*"; "")),
                                   LogicalCoreCount: ($cpu | capture("(?<n>[0-9]+) logical").n | tonumber)},
             Benchmarks: [split("\n")[] | select(length > 0) | split("\t")
               | {FullName: ("PlumePdf.Benchmarks.RasterizeBenchmarks." + .[0]),
                  Statistics: {Mean: ((.[1] | tonumber) * 1000000)}}]}' \
        > "${OUT}/${job}.json"
      # A re-run attempt lists the jobs it carried over again, under new ids with the same log;
      # keep one copy of each reading set.
      digest="$(jq -c '[.Benchmarks[] | [.FullName, .Statistics.Mean]] | sort' "${OUT}/${job}.json")"
      if grep -qxF "$digest" "${OUT}/seen" 2>/dev/null; then
        rm "${OUT}/${job}.json"
      else
        echo "$digest" >> "${OUT}/seen"
      fi
    done
  done
done

count="$(find "$OUT" -name '*.json' | wc -l | tr -d ' ')"
[ "$count" -eq 0 ] && { echo "harvest-raster-perf-readings: no gate jobs found." >&2; exit 1; }
echo "harvest-raster-perf-readings: ${count} gate jobs." >&2
echo
echo "CPU models:"
jq -r '"\(.HostEnvironmentInfo.ProcessorName), \(.HostEnvironmentInfo.LogicalCoreCount) logical cores"' "$OUT"/*.json | sort | uniq -c
echo
echo "Median reading per benchmark (candidate baselineMs):"
jq -s -r '[.[].Benchmarks[]] | group_by(.FullName)[]
  | (map(.Statistics.Mean / 1000000) | sort) as $v
  | "  \(.[0].FullName | split(".") | .[-1]): \(if ($v | length) % 2 == 1 then $v[($v | length) / 2 | floor] else ($v[($v | length) / 2 - 1] + $v[($v | length) / 2]) / 2 end * 10 | round / 10) ms (n=\($v | length))"' "$OUT"/*.json

echo
echo "Replayed through scripts/check-raster-perf-baseline.sh with the working tree's baselines:"
failed=0
for export in "$OUT"/*.json; do
  SKIP_GOVERNANCE=1 scripts/check-raster-perf-baseline.sh "$export" > "${export%.json}.log" 2>&1 || failed=$((failed + 1))
done
cat "$OUT"/*.log \
  | grep -oE 'RasterizeBenchmarks\.Rasterize[A-Za-z]+: .*; [0-9.]+x the reference set|reference-set speed [0-9.]+x' \
  | sed -E 's/RasterizeBenchmarks\.(Rasterize[A-Za-z]+):.*; ([0-9.]+)x the reference set/\1 \2/; s/reference-set speed ([0-9.]+)x/suite \1/' \
  | sort -k1,1 -k2,2n | awk '{ worst[$1] = $2 } END { for (b in worst) printf "  worst %s: %sx\n", b, worst[b] }' | sort
echo "  jobs that would fail: ${failed} of ${count}"
