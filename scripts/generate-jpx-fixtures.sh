#!/usr/bin/env bash
# Generates the committed JPX (JPEG 2000) fixture matrix under
# tests/PlumePdf.CorpusTests/Fixtures/jpx/: every opj_compress-generated and hand-built
# fixture, its opj_decompress PGX reference(s) (where a reference exists), MANIFEST.json
# (per-fixture command/construction, references, tolerance class, and a machine-checked
# structural `assert` block), and jpx-tolerance.json (the two global bit-exact /
# tolerance-a threshold pairs).
#
# This is a BY-HAND tool, like scripts/install-jpx-oracle.sh and
# scripts/install-pdfium-oracle.sh — it is never run in CI. The fixture bytes it produces
# are committed to the repo; CI only ever reads them (via
# tests/PlumePdf.CorpusTests/JpxOracleTests.cs and JpxFixtureFreshnessTests.cs). Re-run it
# by hand only when the fixture matrix itself needs to change (a new recipe, a design
# amendment, or a pinned OpenJPEG version bump — see scripts/install-jpx-oracle.sh's own
# version pin).
#
# All byte-level work — deterministic source generation, JP2 box construction, and the
# structural marker-segment walker used for every MANIFEST `assert` block — lives in
# scripts/jpx-fixtures/generate.py and scripts/jpx-fixtures/markers.py (python3, matching
# this repo's existing fixture-generation convention:
# tests/PlumePdf.CorpusTests/Fixtures/generate_image_fixtures.py). This script is a thin
# bash entry point: it locates a pinned OpenJPEG 2.5.4 (preferring
# ~/jpx-oracle/bin/opj_compress, the scripts/install-jpx-oracle.sh install location, and
# falling back to PATH — generate.py does the same resolution and re-checks the version
# itself) and hands off to generate.py.
#
# Usage:
#   scripts/generate-jpx-fixtures.sh            # regenerate the full fixture matrix
#   scripts/generate-jpx-fixtures.sh --check    # re-run only the structural assertions
#                                                # against the already-committed fixture
#                                                # bytes and MANIFEST.json — no OpenJPEG
#                                                # invocation, no regeneration. This is
#                                                # what CI-adjacent verification runs.
set -euo pipefail
cd "$(dirname "$0")/.."

if ! command -v python3 >/dev/null 2>&1; then
  echo "generate-jpx-fixtures: python3 is required but not found on PATH." >&2
  exit 1
fi

if [ "${1:-}" = "--check" ]; then
  exec python3 scripts/jpx-fixtures/generate.py --check
fi

OPJ_DIR="${HOME}/jpx-oracle/bin"
if [ -x "${OPJ_DIR}/opj_compress" ] && [ -x "${OPJ_DIR}/opj_decompress" ] && [ -x "${OPJ_DIR}/opj_dump" ]; then
  echo "generate-jpx-fixtures: using pinned OpenJPEG at ${OPJ_DIR} (scripts/install-jpx-oracle.sh)." >&2
elif command -v opj_compress >/dev/null 2>&1 && command -v opj_decompress >/dev/null 2>&1 && command -v opj_dump >/dev/null 2>&1; then
  echo "generate-jpx-fixtures: ${OPJ_DIR} not found — falling back to PATH's opj_compress/opj_decompress/opj_dump." >&2
else
  echo "generate-jpx-fixtures: no OpenJPEG CLI found (checked ${OPJ_DIR} and PATH)." >&2
  echo "Run scripts/install-jpx-oracle.sh first, or install OpenJPEG 2.5.4 (e.g. 'brew install openjpeg') and ensure it is on PATH." >&2
  exit 1
fi

python3 scripts/jpx-fixtures/generate.py

echo "generate-jpx-fixtures: matrix size:" >&2
du -sh tests/PlumePdf.CorpusTests/Fixtures/jpx >&2

echo "generate-jpx-fixtures: re-running structural assertions (--check) as a self-test..." >&2
python3 scripts/jpx-fixtures/generate.py --check
