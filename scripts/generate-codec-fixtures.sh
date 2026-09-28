#!/usr/bin/env bash
# Phase 7: generates JPEG/PNG/multi-frame-G4-TIFF fixtures using the CI corpus job's
# installed oracle CLIs — cjpeg (libjpeg-turbo-progs), pnmtopng (netpbm, the same package
# pngtopam ships in), and ppm2tiff/tiffcp (libtiff-tools) — and writes them to a directory
# consumed WITHIN THE SAME RUN by the armed oracle-parity test lanes (DjpegInteropTests /
# PngtopamInteropTests / LibtiffInteropTests).
#
# Deliberately never committed to this repo and never hashed as a golden: fixture bytes an
# apt-installed tool produces are a property of the CI runner image, not of PlumePDF, so
# treating them as byte-stable across an ubuntu-latest bump is exactly the rolling-artifact
# pinning failure this repo's engineering lessons ban ("pin versioned artifacts, never a
# hash of a rolling URL/toolchain output"). scripts/fetch-corpora.sh stays fetch-only per its
# own header; this script is the separate, run-scoped generation half — comparisons run
# against these bytes in the same job, never against a committed golden derived from them.
#
# Usage: scripts/generate-codec-fixtures.sh [output-dir]
#   output-dir defaults to a fresh mktemp -d directory (echoed to stdout either way).
#
# Requires cjpeg, pnmtopng, ppm2tiff, tiffcp on PATH — missing loudly fails (set -e) rather
# than silently skipping a fixture, and every output is asserted non-empty before this script
# exits 0, so a tool that "succeeds" while writing nothing can never make a downstream
# oracle-parity test vacuously pass.
set -euo pipefail

OUT_DIR="${1:-$(mktemp -d -t plumepdf-codec-fixtures.XXXXXX)}"
mkdir -p "$OUT_DIR"
echo "generate-codec-fixtures: writing to ${OUT_DIR}"

for tool in cjpeg pnmtopng ppm2tiff tiffcp; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "generate-codec-fixtures: required tool '${tool}' not found on PATH" >&2
    exit 1
  fi
done

assert_non_empty() {
  local path="$1"
  if [ ! -s "$path" ]; then
    echo "generate-codec-fixtures: ${path} is missing or empty" >&2
    exit 1
  fi
  echo "  wrote ${path} ($(wc -c < "$path" | tr -d ' ') bytes)"
}

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# --- A small procedurally-generated 32x24 RGB gradient, as a raw PPM (P6) ------------------
# No external asset needed: the oracle tools below only care that the source pixels are a
# real, non-degenerate image, not what it depicts. Kept small to keep this step's CI-time
# cost negligible — every pixel round-trips through one `printf '\%03o'`
# octal-escape call per channel, so dimensions stay two-digit.
WIDTH=32
HEIGHT=24
{
  printf 'P6\n%d %d\n255\n' "$WIDTH" "$HEIGHT"
  for ((y = 0; y < HEIGHT; y++)); do
    for ((x = 0; x < WIDTH; x++)); do
      r=$(((x * 255) / (WIDTH - 1)))
      g=$(((y * 255) / (HEIGHT - 1)))
      b=$(((r + g) / 2))
      printf "\\$(printf '%03o' "$r")\\$(printf '%03o' "$g")\\$(printf '%03o' "$b")"
    done
  done
} > "${WORK}/gradient.ppm"
assert_non_empty "${WORK}/gradient.ppm"

# --- cjpeg: baseline JPEG oracle fixture (libjpeg-turbo) -----------------------------------
cjpeg -quality 95 -outfile "${OUT_DIR}/oracle-gradient-q95.jpg" "${WORK}/gradient.ppm"
assert_non_empty "${OUT_DIR}/oracle-gradient-q95.jpg"

# --- pnmtopng (netpbm — same package pngtopam ships in): PNG oracle fixture ----------------
pnmtopng "${WORK}/gradient.ppm" > "${OUT_DIR}/oracle-gradient.png"
assert_non_empty "${OUT_DIR}/oracle-gradient.png"

# --- ppm2tiff + tiffcp (libtiff-tools): multi-frame G4 TIFF oracle fixture -----------------
# A bilevel (1-bit) checkerboard PBM, G4 (Group 4, ITU-T T.6) compressed via ppm2tiff, then
# replicated into a 3-frame TIFF via tiffcp — the "multi-frame G4 TIFF" the exit demo and
# Pdf.FromImages' one-page-per-frame behavior are validated against. Written
# as raw (P4), packed-bit PBM — ppm2tiff's own PNM reader (distinct from netpbm's, which does
# accept plain P1) only recognizes the binary PNM variants — WIDTH is kept a multiple of 8 so
# every row packs into whole bytes with no partial-byte tail to handle.
{
  printf 'P4\n%d %d\n' "$WIDTH" "$HEIGHT"
  for ((y = 0; y < HEIGHT; y++)); do
    for ((byteIndex = 0; byteIndex < WIDTH / 8; byteIndex++)); do
      byte=0
      for ((bit = 0; bit < 8; bit++)); do
        x=$((byteIndex * 8 + bit))
        pixel=$((((x / 4) + (y / 4)) % 2))
        byte=$((byte | (pixel << (7 - bit))))
      done
      printf "\\$(printf '%03o' "$byte")"
    done
  done
} > "${WORK}/checkerboard.pbm"
assert_non_empty "${WORK}/checkerboard.pbm"

ppm2tiff -c g4 "${WORK}/checkerboard.pbm" "${WORK}/frame.tiff"
assert_non_empty "${WORK}/frame.tiff"

tiffcp "${WORK}/frame.tiff" "${WORK}/frame.tiff" "${WORK}/frame.tiff" "${OUT_DIR}/oracle-multiframe-g4.tiff"
assert_non_empty "${OUT_DIR}/oracle-multiframe-g4.tiff"

echo "generate-codec-fixtures: done — 3 fixtures in ${OUT_DIR}"
