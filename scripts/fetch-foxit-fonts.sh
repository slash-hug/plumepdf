#!/usr/bin/env bash
# Fetches PDFium's two Foxit substitute faces (Foxit Symbol, Foxit Dingbats) at a PINNED
# PDFium commit, verifies both source files and both extracted font payloads against
# SHA-256 pins, and writes the bare-CFF programs to an output directory — the input for
# `tools/FontAssetGen`.
#
# The faces live in PDFium's tree as `std::array<uint8_t, N>` initializers in
# core/fxge/fontdata/chromefontdata/FoxitSymbol.cpp / FoxitDingbats.cpp
# ("Copyright 2014 The PDFium Authors / Original code copyright 2014 Foxit Software Inc.",
# PDFium's LICENSE: BSD-3-Clause text followed by the Apache-2.0 text — see NOTICE). They are
# ordinary bare CFF programs (header 01 00 04 02; Name INDEX ChromSymbolOTF / ChromDingbatsOTF).
#
# Pin shape mirrors install-pdfium-oracle.sh: a bump is a deliberate, reviewed edit of the
# commit + all four hashes together, never a rolling pin. Any mismatch is a STOP — report it,
# never "fix" the pin to match what was downloaded. Only pdfium.googlesource.com serves the
# pinned commit (the GitHub mirror does not), so the source URL is not configurable.
#
# Prerequisites: bash, curl, base64, python3 (stdlib only), and sha256sum or shasum.
# Usage: scripts/fetch-foxit-fonts.sh [OUT_DIR]     (default: a fresh mktemp dir, printed)
# Output: OUT_DIR/FoxitSymbol.cff, OUT_DIR/FoxitDingbats.cff (+ the two .cpp sources kept
# alongside for provenance). Nothing under the repo is written; committing the .cff files is
# NOT the intended flow — FontAssetGen turns them into compiled-in .g.cs blobs.
set -euo pipefail

PDFIUM_COMMIT="272ef3633b0f05896edfdc702d0328ed924e984e"
PDFIUM_BASE="https://pdfium.googlesource.com/pdfium/+/${PDFIUM_COMMIT}/core/fxge/fontdata/chromefontdata"

# Verified 2026-09-04 by downloading + re-hashing locally, and by parsing both payloads with
# fontTools 4.60.2 (191 / 203 glyphs; charsets cover every Standard-14 Symbol / ZapfDingbats name).
FOXIT_SYMBOL_CPP_SHA256="46bcac4ed2c2cffd3f9e9d3a3c9fe69c741013ef7ee22e4f24f064c09594ac81"
FOXIT_SYMBOL_CFF_SHA256="47967d055530e7357088a08403115425643ec2cdfd6201ba8af0fbd7116c1539"
FOXIT_SYMBOL_CFF_BYTES=16729
FOXIT_DINGBATS_CPP_SHA256="ac6b1ad158b84801ae2a36667a95238657fb178e39416d6458350e2f01244eeb"
FOXIT_DINGBATS_CFF_SHA256="845c752392b6c914fb989c75a08b7792b88f542d2499042ef2889f8c814a16ed"
FOXIT_DINGBATS_CFF_BYTES=29513

OUT_DIR="${1:-$(mktemp -d -t plumepdf-foxit-fonts.XXXXXX)}"
mkdir -p "${OUT_DIR}"

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

verify_sha256() {
  local path="$1" expected="$2" actual
  actual="$(sha256_of "${path}")"
  if [ "${actual}" != "${expected}" ]; then
    echo "fetch-foxit-fonts.sh: SHA-256 mismatch for ${path}" >&2
    echo "  expected: ${expected}" >&2
    echo "  actual:   ${actual}" >&2
    echo "  STOP: do not edit the pin — report the mismatch (PDFium commit ${PDFIUM_COMMIT})." >&2
    exit 1
  fi
  echo "fetch-foxit-fonts.sh: $(basename "${path}") SHA-256 verified"
}

fetch_and_extract() {
  local name="$1" cpp_sha="$2" cff_sha="$3" cff_bytes="$4"
  local cpp="${OUT_DIR}/${name}.cpp" cff="${OUT_DIR}/${name}.cff"

  # googlesource serves file contents base64-encoded under ?format=TEXT.
  curl -sSfL "${PDFIUM_BASE}/${name}.cpp?format=TEXT" --retry 3 --max-time 120 | base64 -d > "${cpp}"
  verify_sha256 "${cpp}" "${cpp_sha}"

  # Extract the `std::array<uint8_t, N> kFoxit...FontData = {{ 0x.., ... }}` initializer.
  python3 - "${cpp}" "${cff}" <<'PY'
import re, sys
src = open(sys.argv[1], encoding="utf-8").read()
m = re.search(r"std::array<uint8_t,\s*(\d+)>\s+\w+\s*=\s*\{\{(.*?)\}\}", src, re.S)
if not m:
    sys.exit("fetch-foxit-fonts.sh: could not locate the std::array initializer")
declared = int(m.group(1))
data = bytes(int(t, 0) for t in re.findall(r"0[xX][0-9A-Fa-f]+|\b\d+\b", m.group(2)))
if len(data) != declared:
    sys.exit(f"fetch-foxit-fonts.sh: extracted {len(data)} bytes but the array declares {declared}")
open(sys.argv[2], "wb").write(data)
PY

  local actual_bytes
  actual_bytes="$(wc -c < "${cff}" | tr -d ' ')"
  if [ "${actual_bytes}" != "${cff_bytes}" ]; then
    echo "fetch-foxit-fonts.sh: ${name}.cff is ${actual_bytes} bytes, expected ${cff_bytes}" >&2
    exit 1
  fi
  verify_sha256 "${cff}" "${cff_sha}"

  # Sanity: bare CFF header (major 1, minor 0, hdrSize 4, offSize 2).
  if [ "$(head -c 4 "${cff}" | od -An -tx1 | tr -d ' \n')" != "01000402" ]; then
    echo "fetch-foxit-fonts.sh: ${name}.cff does not start with a bare-CFF header" >&2
    exit 1
  fi
}

echo "fetch-foxit-fonts.sh: pdfium commit=${PDFIUM_COMMIT} out_dir=${OUT_DIR}"
fetch_and_extract FoxitSymbol   "${FOXIT_SYMBOL_CPP_SHA256}"   "${FOXIT_SYMBOL_CFF_SHA256}"   "${FOXIT_SYMBOL_CFF_BYTES}"
fetch_and_extract FoxitDingbats "${FOXIT_DINGBATS_CPP_SHA256}" "${FOXIT_DINGBATS_CFF_SHA256}" "${FOXIT_DINGBATS_CFF_BYTES}"
echo "fetch-foxit-fonts.sh: done — ${OUT_DIR}/FoxitSymbol.cff, ${OUT_DIR}/FoxitDingbats.cff"
