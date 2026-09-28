#!/usr/bin/env bash
# Installs the JPX (JPEG 2000) per-sample oracle: downloads a pinned
# uclouvain/openjpeg release (v2.5.4 + per-asset SHA-256, verified
# below), installs opj_compress/opj_decompress/opj_dump + libopenjp2 to a stable
# non-ephemeral path, and runs an end-to-end round-trip self-test (compress a tiny PGM,
# decompress it back, compare) — not just "the binary starts" — before this script
# reports success. Mirrors scripts/install-pdfium-oracle.sh: not best-effort, no
# `continue-on-error`; a broken pin is a loud CI failure, never a silent no-op that
# leaves PLUMEPDF_REQUIRE_OPJ's gate unarmed.
#
# Deliberately NOT a source build: GitHub's auto-generated tag tarballs are not
# hash-stable, and building from source needs libpng-dev (or PNG input silently
# disables) — the prebuilt release assets are used instead, verified byte-identical to
# a Homebrew 2.5.4 build. BSD-2-Clause tool used only as an oracle/fixture
# generator — no OpenJPEG code enters the repo, NOTICE unchanged.
#
# CI target is linux-x86_64. This script also supports macOS arm64 for local
# verification (dev machines).
#
# Platform-specific loader wiring (both verified against the pinned release assets
# themselves with otool/objdump, not carried forward from a description — the
# PLUME7729 lesson):
#   - macOS: the binaries reference `@rpath/libopenjp2.7.dylib` and ship with NO
#     LC_RPATH (`otool -l` shows none), so they fail to launch from a fresh copy until
#     an rpath is added. Fixed the same way install-pdfium-oracle.sh:108-120 rewrites a
#     Mach-O load command: `install_name_tool -add_rpath "$INSTALL_DIR/lib"` on each
#     freshly-copied binary (each run copies fresh bytes from the extracted archive
#     before adding the rpath, so re-running this script never tries to add a
#     already-present rpath twice).
#   - Linux: the binaries need `libopenjp2.so.7` and carry no RPATH/RUNPATH either
#     (`objdump -p` shows a NEEDED entry and no *PATH tag). Two ways to fix that:
#     `patchelf --set-rpath` (not guaranteed present on ubuntu-latest without an
#     `apt-get install`, which needs root and an extra package — exactly what this
#     script must avoid), or point the loader at the lib directory at run time. This
#     script takes the second route: the real ELF binaries are installed to
#     $INSTALL_DIR/libexec/, libopenjp2.so* goes to $INSTALL_DIR/lib/, and
#     $INSTALL_DIR/bin/opj_* are thin wrapper scripts that export LD_LIBRARY_PATH and
#     exec the real binary — zero root, zero extra packages, works on any glibc Linux.
set -euo pipefail

# ${HOME:-/tmp} rather than a bare ${HOME}: under `set -u` an unset $HOME would abort
# with "unbound variable" instead of a clear message — this still gives a usable
# default (matching install-pdfium-oracle.sh's own ${HOME}/pdfium-oracle shape when
# $HOME is set, which is every normal shell/CI runner).
INSTALL_DIR="${1:-${HOME:-/tmp}/jpx-oracle}"

# Pin shape mirrors PDFIUM_VERSION in scripts/install-pdfium-oracle.sh: a version bump
# is a deliberate, reviewed edit of the tag + both hashes together, never a rolling pin.
# Verified 2026-09-05 by downloading both assets from
# https://github.com/uclouvain/openjpeg/releases/tag/v2.5.4 and re-hashing them locally.
OPJ_VERSION="v2.5.4"
OPJ_LINUX_ASSET="openjpeg-v2.5.4-linux-x86_64.tar.gz"
OPJ_LINUX_SHA256="77915284c4823bbb5f75053d2b6ec8af11378c9fc5f3d1742a17e1bed984277d"
OPJ_MAC_ASSET="openjpeg-v2.5.4-osx-arm64.zip"
OPJ_MAC_SHA256="214b8b32cf42d32d79698ad766eea75366d1fb214a7ffe5ae3c563a7e7418981"

UNAME_S="$(uname -s)"
UNAME_M="$(uname -m)"

case "${UNAME_S}-${UNAME_M}" in
  Linux-x86_64)
    PLATFORM="linux-x86_64"
    ASSET="${OPJ_LINUX_ASSET}"
    EXPECTED_SHA256="${OPJ_LINUX_SHA256}"
    ;;
  Darwin-arm64)
    PLATFORM="osx-arm64"
    ASSET="${OPJ_MAC_ASSET}"
    EXPECTED_SHA256="${OPJ_MAC_SHA256}"
    ;;
  *)
    echo "install-jpx-oracle.sh: unsupported platform '${UNAME_S}-${UNAME_M}' — only" >&2
    echo "linux-x86_64 (CI target) and osx-arm64 (local validation) are pinned. Add a" >&2
    echo "new case + pinned asset/SHA-256 deliberately, mirroring the two above, to" >&2
    echo "extend." >&2
    exit 1
    ;;
esac

echo "install-jpx-oracle.sh: platform=${PLATFORM} version=${OPJ_VERSION} install_dir=${INSTALL_DIR}"

# Guard the wipe below: ${INSTALL_DIR} is caller-controlled (the $1 override exists
# for exactly this reason), so `rm -rf` on it unconditionally would destroy an
# unrelated directory a caller pointed this script at by mistake (e.g.
# `./install-jpx-oracle.sh ~/Documents`). Only remove it when it's absent, already
# empty, or carries this script's own marker file from a prior run (proof this
# directory has only ever held a previous install of this exact oracle) — otherwise
# fail loudly and leave it untouched.
MARKER_FILE="${INSTALL_DIR}/.plumepdf-jpx-oracle"
if [ -e "${INSTALL_DIR}" ]; then
  if [ ! -d "${INSTALL_DIR}" ]; then
    echo "install-jpx-oracle.sh: ${INSTALL_DIR} exists and is not a directory —" >&2
    echo "refusing to touch it. Pass a different install directory." >&2
    exit 1
  fi
  if [ -f "${MARKER_FILE}" ]; then
    : # a previous run of this exact script installed here — safe to wipe and redo
  elif [ -z "$(ls -A "${INSTALL_DIR}" 2>/dev/null)" ]; then
    : # exists but empty — safe to use
  else
    echo "install-jpx-oracle.sh: refusing to remove ${INSTALL_DIR} — it already" >&2
    echo "exists, is not empty, and has no ${MARKER_FILE} marker from a previous" >&2
    echo "run of this script, so it may be an unrelated directory. Pass a" >&2
    echo "dedicated install directory (default: \${HOME}/jpx-oracle) instead." >&2
    exit 1
  fi
  rm -rf "${INSTALL_DIR}"
fi
WORK_DIR="$(mktemp -d)"
trap 'rm -rf "${WORK_DIR}"' EXIT

ARCHIVE="${WORK_DIR}/${ASSET}"
curl -fsSL "https://github.com/uclouvain/openjpeg/releases/download/${OPJ_VERSION}/${ASSET}" \
  -o "${ARCHIVE}" --retry 3 --max-time 180

if command -v sha256sum >/dev/null 2>&1; then
  echo "${EXPECTED_SHA256}  ${ARCHIVE}" | sha256sum -c -
else
  # macOS has no sha256sum by default; shasum -a 256 is the equivalent.
  ACTUAL_SHA256="$(shasum -a 256 "${ARCHIVE}" | awk '{print $1}')"
  if [ "${ACTUAL_SHA256}" != "${EXPECTED_SHA256}" ]; then
    echo "install-jpx-oracle.sh: SHA-256 mismatch for ${ASSET}" >&2
    echo "  expected: ${EXPECTED_SHA256}" >&2
    echo "  actual:   ${ACTUAL_SHA256}" >&2
    exit 1
  fi
fi
echo "install-jpx-oracle.sh: ${ASSET} SHA-256 verified"

EXTRACT_DIR="${WORK_DIR}/extract"
mkdir -p "${EXTRACT_DIR}"
if [ "${PLATFORM}" = "linux-x86_64" ]; then
  tar xzf "${ARCHIVE}" -C "${EXTRACT_DIR}"
else
  unzip -q "${ARCHIVE}" -d "${EXTRACT_DIR}"
fi

# Both assets contain a single top-level directory: openjpeg-${OPJ_VERSION}-<platform>/, with
# bin/opj_compress, bin/opj_decompress, bin/opj_dump and lib/libopenjp2.*.
SRC_DIR="${EXTRACT_DIR}/openjpeg-${OPJ_VERSION}-${PLATFORM}"
if [ ! -d "${SRC_DIR}/bin" ] || [ ! -d "${SRC_DIR}/lib" ]; then
  echo "install-jpx-oracle.sh: expected ${SRC_DIR}/{bin,lib} in the extracted archive," >&2
  echo "found: $(ls -1 "${EXTRACT_DIR}")" >&2
  exit 1
fi

mkdir -p "${INSTALL_DIR}"
touch "${MARKER_FILE}"
mkdir -p "${INSTALL_DIR}/lib"
cp -R "${SRC_DIR}/lib/." "${INSTALL_DIR}/lib/"

if [ "${PLATFORM}" = "osx-arm64" ]; then
  # Fresh copy each run, so install_name_tool never sees an rpath already added by a
  # previous invocation (see the header comment).
  mkdir -p "${INSTALL_DIR}/bin"
  for TOOL in opj_compress opj_decompress opj_dump; do
    cp "${SRC_DIR}/bin/${TOOL}" "${INSTALL_DIR}/bin/${TOOL}"
    chmod +x "${INSTALL_DIR}/bin/${TOOL}"
    install_name_tool -add_rpath "${INSTALL_DIR}/lib" "${INSTALL_DIR}/bin/${TOOL}"
  done
  echo "install-jpx-oracle.sh: rpath added to ${INSTALL_DIR}/bin/opj_{compress,decompress,dump}"
else
  # Linux: real binaries in libexec/, thin LD_LIBRARY_PATH wrapper scripts in bin/ —
  # see the header comment for why this is preferred over patchelf here.
  mkdir -p "${INSTALL_DIR}/libexec" "${INSTALL_DIR}/bin"
  for TOOL in opj_compress opj_decompress opj_dump; do
    cp "${SRC_DIR}/bin/${TOOL}" "${INSTALL_DIR}/libexec/${TOOL}"
    chmod +x "${INSTALL_DIR}/libexec/${TOOL}"
    cat > "${INSTALL_DIR}/bin/${TOOL}" <<EOF
#!/usr/bin/env bash
# Thin wrapper generated by install-jpx-oracle.sh: the pinned openjpeg release binary
# needs libopenjp2.so.7 and ships with no RPATH/RUNPATH, so this exports
# LD_LIBRARY_PATH and execs the real binary instead of requiring patchelf/root.
set -euo pipefail
export LD_LIBRARY_PATH="${INSTALL_DIR}/lib\${LD_LIBRARY_PATH:+:\${LD_LIBRARY_PATH}}"
exec "${INSTALL_DIR}/libexec/${TOOL}" "\$@"
EOF
    chmod +x "${INSTALL_DIR}/bin/${TOOL}"
  done
  echo "install-jpx-oracle.sh: LD_LIBRARY_PATH wrappers installed to ${INSTALL_DIR}/bin/opj_{compress,decompress,dump}"
fi

echo "install-jpx-oracle.sh: OpenJPEG ${OPJ_VERSION} installed to ${INSTALL_DIR}"

# Success predicate, mirroring install-pdfium-oracle.sh's self-test standard: not
# "the binary starts" but an end-to-end round trip. First the version banner (both
# tools print "compiled against openjp2 library v2.5.4" / similar — checked for the
# pinned version number), then a tiny 8x8 PGM compressed and decompressed back,
# compared byte-for-byte against the (lossless, -r 1) round trip.
# Note: opj_decompress -h exits 1 (it's a usage dump, not a success path) — captured
# with `|| true` so that doesn't trip `set -e`/`pipefail` before the grep check below.
FULL_HELP="$("${INSTALL_DIR}/bin/opj_decompress" -h 2>&1 || true)"
echo "install-jpx-oracle.sh: opj_decompress -h: $(printf '%s' "${FULL_HELP}" | grep -m1 . )"
if ! printf '%s' "${FULL_HELP}" | grep -qF "2.5.4"; then
  echo "install-jpx-oracle.sh: opj_decompress -h did not mention 2.5.4 — treating the" >&2
  echo "install as failed (a broken pin must never read as armed)." >&2
  exit 1
fi

SELFTEST_DIR="${WORK_DIR}/selftest"
mkdir -p "${SELFTEST_DIR}"
SRC_PGM="${SELFTEST_DIR}/src.pgm"
J2K_FILE="${SELFTEST_DIR}/out.j2k"
OUT_PGM="${SELFTEST_DIR}/out.pgm"
PIXEL_BYTES=64

# An 8x8 8-bit grayscale PGM (P5) with a deterministic gradient, compressed lossless
# (-r 1, single resolution level: opj_compress's default of 6 resolutions needs far
# more than 8px per side — "Number of resolutions is too high in comparison to the
# size of tiles" — verified against this exact image) and decompressed back — the
# pixel payload must be bit-exact. Only the trailing PIXEL_BYTES are compared, not the
# whole file: opj_decompress always writes its own "#OpenJPEG-2.5.4" comment line into
# the PGM header (verified), which the freshly-generated source file does not have.
{
  printf 'P5\n8 8\n255\n'
  for i in $(seq 0 63); do
    printf "$(printf '\\%03o' $(( (i * 4) % 256 )))"
  done
} > "${SRC_PGM}"

"${INSTALL_DIR}/bin/opj_compress" -i "${SRC_PGM}" -o "${J2K_FILE}" -r 1 -n 1 >/dev/null
"${INSTALL_DIR}/bin/opj_decompress" -i "${J2K_FILE}" -o "${OUT_PGM}" >/dev/null

if ! cmp -s <(tail -c "${PIXEL_BYTES}" "${SRC_PGM}") <(tail -c "${PIXEL_BYTES}" "${OUT_PGM}"); then
  echo "install-jpx-oracle.sh: round-trip self-test FAILED — the decoded pixel bytes in" >&2
  echo "${OUT_PGM} differ from ${SRC_PGM} after a lossless (-r 1) compress/decompress" >&2
  echo "round trip." >&2
  exit 1
fi

echo "install-jpx-oracle.sh: round-trip self-test OK (${SRC_PGM} pixels == ${OUT_PGM} pixels)"

# opj_dump's own self-test coverage: dump the round-trip .j2k's codestream markers
# (-v — verbose — is required for the marker list) and confirm the SIZ marker was
# actually parsed. Verified against this exact binary: opj_dump prints marker TYPES as
# their hex code, never the literal string "SIZ" (`type=0xff51` is SIZ per T.800 Table
# A.4 — 0xff51 is SIZ's marker code), so that hex code is what's grepped for here,
# alongside the decoded image dimensions (x1=8, y1=8), which only a correctly-parsed
# SIZ segment can produce.
DUMP_OUTPUT="$("${INSTALL_DIR}/bin/opj_dump" -i "${J2K_FILE}" -v 2>&1)"
if ! printf '%s' "${DUMP_OUTPUT}" | grep -qF "0xff51"; then
  echo "install-jpx-oracle.sh: opj_dump self-test FAILED — no SIZ marker (0xff51) found" >&2
  echo "in the round-trip codestream's marker list:" >&2
  printf '%s\n' "${DUMP_OUTPUT}" >&2
  exit 1
fi
if ! printf '%s' "${DUMP_OUTPUT}" | grep -qF "x1=8, y1=8"; then
  echo "install-jpx-oracle.sh: opj_dump self-test FAILED — decoded image dimensions" >&2
  echo "did not match the 8x8 source:" >&2
  printf '%s\n' "${DUMP_OUTPUT}" >&2
  exit 1
fi
echo "install-jpx-oracle.sh: opj_dump self-test OK (SIZ marker 0xff51 + 8x8 dimensions found)"

echo "install-jpx-oracle.sh: JPX oracle armed at ${INSTALL_DIR}/bin"
