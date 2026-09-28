#!/usr/bin/env bash
# Installs the Phase 8 PDFium oracle: downloads a pinned bblanchon/pdfium-binaries release
# (chromium/NNNNN + per-asset SHA-256, verified below), installs libpdfium to a stable
# non-ephemeral path, compiles scripts/pdfium-oracle/pdfium_shim.c against the pinned
# headers, and runs the shim's own --self-test — which must render end to end (library
# init through PNG bytes on disk), not just "the binary starts" — before this script
# reports success. Mirrors the veraPDF/hb-shape install steps in .github/workflows/ci.yml:
# not best-effort, no `continue-on-error`; a broken pin is a loud CI failure, never a
# silent no-op that leaves PLUMEPDF_REQUIRE_PDFIUM's gate unarmed.
#
# bblanchon/pdfium-binaries ships shared libraries only (no static archive) — see
# PdfiumOracle.cs's <remarks> for why this is a deliberate, documented
# deviation from the hb-shape lesson's literal "statically linked" wording rather than a
# regression of it: the *intent* (no exit-127 because the lib was left in an ephemeral
# build tree) is satisfied by installing libpdfium to a stable path instead.
#
# CI target is linux-x64 (installed to /usr/local/lib + ldconfig — that mechanism needs
# root, which ubuntu-latest CI runners have via passwordless sudo). This script also
# supports macOS arm64, separately pinned, for local validation only (Apple Silicon dev
# machines have no ldconfig; the shared library
# instead gets its Mach-O install name rewritten to an absolute path under the same
# stable, non-ephemeral $INSTALL_DIR used everywhere else, so no DYLD_LIBRARY_PATH/sudo
# is needed there either). Either platform installs the compiled pdfium_shim binary to
# the same path: $INSTALL_DIR/pdfium_shim, matching PdfiumOracle.ShimPath's documented
# default fallback (~/pdfium-oracle/pdfium_shim) — so a default-argument invocation on
# either platform needs zero extra configuration (no PLUMEPDF_PDFIUM_SHIM override, no
# PATH entry) for the C# probe to find it.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
INSTALL_DIR="${1:-${HOME}/pdfium-oracle}"

# Pin shape mirrors VERAPDF_VERSION/HARFBUZZ_VERSION in .github/workflows/ci.yml: a
# version bump is a deliberate, reviewed edit of the tag + both hashes together, never a
# rolling pin. Verified 2026-08-21 against
# https://github.com/bblanchon/pdfium-binaries/releases/tag/chromium%2F8009 (per-asset
# SHA-256 published directly on the release page) and by downloading + re-hashing both
# assets locally.
PDFIUM_VERSION="chromium/8009"
PDFIUM_LINUX_ASSET="pdfium-linux-x64.tgz"
PDFIUM_LINUX_SHA256="be513e8021a5bf8eb2116e00d78c3bacb82c5a02b3785156ae14fe5e33084385"
PDFIUM_MAC_ASSET="pdfium-mac-arm64.tgz"
PDFIUM_MAC_SHA256="b1f2f17c7432a9942514dda5094ee9822c743bdfd07e7187725efbd34fde941f"

UNAME_S="$(uname -s)"
UNAME_M="$(uname -m)"

case "${UNAME_S}-${UNAME_M}" in
  Linux-x86_64)
    PLATFORM="linux-x64"
    ASSET="${PDFIUM_LINUX_ASSET}"
    EXPECTED_SHA256="${PDFIUM_LINUX_SHA256}"
    LIB_NAME="libpdfium.so"
    ;;
  Darwin-arm64)
    PLATFORM="mac-arm64"
    ASSET="${PDFIUM_MAC_ASSET}"
    EXPECTED_SHA256="${PDFIUM_MAC_SHA256}"
    LIB_NAME="libpdfium.dylib"
    ;;
  *)
    echo "install-pdfium-oracle.sh: unsupported platform '${UNAME_S}-${UNAME_M}' — only" >&2
    echo "linux-x64 (CI target) and mac-arm64 (local validation) are pinned. Add a" >&2
    echo "new case + pinned tag/SHA-256 deliberately, mirroring the two above, to extend." >&2
    exit 1
    ;;
esac

echo "install-pdfium-oracle.sh: platform=${PLATFORM} version=${PDFIUM_VERSION} install_dir=${INSTALL_DIR}"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "${WORK_DIR}"' EXIT

ARCHIVE="${WORK_DIR}/${ASSET}"
curl -sSfL "https://github.com/bblanchon/pdfium-binaries/releases/download/${PDFIUM_VERSION/\//%2F}/${ASSET}" \
  -o "${ARCHIVE}" --retry 3 --max-time 180

if command -v sha256sum >/dev/null 2>&1; then
  echo "${EXPECTED_SHA256}  ${ARCHIVE}" | sha256sum -c -
else
  # macOS has no sha256sum by default; shasum -a 256 is the equivalent.
  ACTUAL_SHA256="$(shasum -a 256 "${ARCHIVE}" | awk '{print $1}')"
  if [ "${ACTUAL_SHA256}" != "${EXPECTED_SHA256}" ]; then
    echo "install-pdfium-oracle.sh: SHA-256 mismatch for ${ASSET}" >&2
    echo "  expected: ${EXPECTED_SHA256}" >&2
    echo "  actual:   ${ACTUAL_SHA256}" >&2
    exit 1
  fi
fi
echo "install-pdfium-oracle.sh: ${ASSET} SHA-256 verified"

EXTRACT_DIR="${WORK_DIR}/extract"
mkdir -p "${EXTRACT_DIR}"
tar xf "${ARCHIVE}" -C "${EXTRACT_DIR}"

mkdir -p "${INSTALL_DIR}"
rm -rf "${INSTALL_DIR}/include"
cp -R "${EXTRACT_DIR}/include" "${INSTALL_DIR}/include"

if [ "${PLATFORM}" = "linux-x64" ]; then
  # The chosen mechanism: a stable, non-ephemeral, root-owned system path + ldconfig.
  # ubuntu-latest CI runners have passwordless sudo; this fails loudly (set -e) if not.
  sudo cp "${EXTRACT_DIR}/lib/${LIB_NAME}" "/usr/local/lib/${LIB_NAME}"
  sudo ldconfig
  LIB_DIR="/usr/local/lib"
else
  # macOS: no ldconfig. libpdfium.dylib ships with a relative install name
  # ("./libpdfium.dylib" — verified via `otool -D` against this exact pinned asset),
  # which would only resolve relative to the CURRENT WORKING DIRECTORY at runtime, not
  # relative to the binary — exactly the kind of "works right after building, breaks
  # from anywhere else" hazard the hb-shape lesson warns about. Rewriting the copy's own
  # install name to an absolute path under the stable $INSTALL_DIR (never a build/ temp
  # tree) means the linker bakes that absolute path into pdfium_shim's own load command
  # below, so no DYLD_LIBRARY_PATH or sudo is needed at all — validation-only, but held
  # to the same "must not silently break outside this one directory" standard as CI.
  mkdir -p "${INSTALL_DIR}/lib"
  cp "${EXTRACT_DIR}/lib/${LIB_NAME}" "${INSTALL_DIR}/lib/${LIB_NAME}"
  install_name_tool -id "${INSTALL_DIR}/lib/${LIB_NAME}" "${INSTALL_DIR}/lib/${LIB_NAME}"
  LIB_DIR="${INSTALL_DIR}/lib"
fi

echo "install-pdfium-oracle.sh: ${LIB_NAME} installed to ${LIB_DIR}"

# Phase 9: this same single-file compile also builds the new `--render-forms` CLI mode (form-fill
# environment + FPDF_FFLDraw) pdfium_shim.c's Phase 9 section adds — no new build flag, header
# search path, or library is needed, since fpdf_formfill.h already ships inside the pinned
# release's own include/ directory downloaded above. The self-test below (which the Phase 9
# shim extends to also exercise --render-forms against a /V-no-/AP fixture) is still the same
# single success predicate for both the Phase 8 and Phase 9 CLI modes.
CC="${CC:-cc}"
SHIM_BIN="${INSTALL_DIR}/pdfium_shim"
"${CC}" -O2 -Wall \
  -I "${INSTALL_DIR}/include" \
  "${REPO_ROOT}/scripts/pdfium-oracle/pdfium_shim.c" \
  -L "${LIB_DIR}" -lpdfium \
  -o "${SHIM_BIN}"

echo "install-pdfium-oracle.sh: compiled ${SHIM_BIN}"

# Success predicate: the SAME check PdfiumOracle.ProbeShim() makes (exit 0 AND stdout
# contains PDFIUM_SELF_TEST_OK) — so "installed" here can never mean something the C#
# probe would still find absent/broken. Not best-effort: a failure here must fail this
# script (and therefore the CI step invoking it) loudly, per this script's CI-gating intent.
SELF_TEST_OUTPUT="$("${SHIM_BIN}" --self-test)"
echo "${SELF_TEST_OUTPUT}"
if ! grep -qF "PDFIUM_SELF_TEST_OK" <<< "${SELF_TEST_OUTPUT}"; then
  echo "install-pdfium-oracle.sh: self-test did not report PDFIUM_SELF_TEST_OK — treating" >&2
  echo "the install as failed (a broken pin must never read as armed)." >&2
  exit 1
fi

echo "install-pdfium-oracle.sh: PDFium oracle armed at ${SHIM_BIN}"
