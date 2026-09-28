#!/usr/bin/env bash
# Mechanical enforcement of the clean-room policy in AGENTS.md and the AGPL
# isolation wall (docs/architecture.md).
#
# Two independent checks, both must pass:
#
#   1. Package graph: src/PlumePdf/PlumePdf.csproj — including its transitive
#      closure — must never resolve to a competitor PDF package. Competitor
#      packages (even permissively-licensed ones like PdfPig/PDFsharp, which
#      the clean-room policy allows *reading the source of* for porting) may
#      only appear as package references in benchmarks/bench.sln.
#
#   2. Clean-room heuristics: src/ and docs/ must never contain a verbatim
#      competitor copyright/license header, an AGPL license block, a
#      forbidden library's own namespace used as if it were our code, or the
#      literal ISO-spec page furniture that only shows up when spec text is
#      pasted verbatim rather than cited by section number (see
#      docs/spec-sources.md). Plain-text mentions of competitor/spec names
#      (e.g. "iText7 (AGPL)", "PdfPig (Apache-2.0)" in AGENTS.md) are
#      expected and do NOT trip these checks — only markers that indicate
#      actual copied text do.
#
# Exits 0 with no output beyond progress lines when clean; exits 1 and prints
# every violation found (does not stop at the first one) otherwise.
set -euo pipefail
cd "$(dirname "$0")/.."

status=0
fail() {
  echo "PROVENANCE VIOLATION: $1" >&2
  status=1
}

# ---------------------------------------------------------------------------
# 1. AGPL isolation wall — package graph check
# ---------------------------------------------------------------------------
echo "== checking src/PlumePdf package graph =="
# bouncycastle: explicitly rejected as the CMS/PKCS#7 dependency in favour of
# the BCL's System.Security.Cryptography.Pkcs — gated here the same way every other rejected
# competitor library already is.
FORBIDDEN_PACKAGES='itext|pdfpig|pdfsharp|aspose|syncfusion|questpdf|bouncycastle'

dotnet restore src/PlumePdf/PlumePdf.csproj >/dev/null
package_list=$(dotnet list src/PlumePdf/PlumePdf.csproj package --include-transitive 2>&1)
echo "$package_list"
if echo "$package_list" | grep -Eiq "$FORBIDDEN_PACKAGES"; then
  fail "src/PlumePdf/PlumePdf.csproj (or a transitive dependency) references a forbidden competitor package (matched pattern: $FORBIDDEN_PACKAGES). Competitor PDF packages may only be referenced from benchmarks/bench.sln (the AGPL isolation wall)."
fi

# ---------------------------------------------------------------------------
# 2. Clean-room heuristics over src/, docs/, and benchmarks/
# ---------------------------------------------------------------------------
# benchmarks/ is included because PlumePdf.Benchmarks.Comparisons is the
# first place a real competitor (itext7/AGPL) package is actually restored
# and linked in this repo's CI — exactly the spot an accidental
# verbatim-source paste would now land, even though referencing the
# *package* there is expected and allowed (that's the whole point of the
# AGPL isolation wall — see check 1 above, which deliberately does NOT
# include benchmarks/, since competitor packages belong there). This is the
# same text-marker heuristic as src/ and docs/, just widened to cover the one
# directory where the clean-room policy's "never read their source" rule is
# now actually load-bearing rather than theoretical.
echo "== scanning src/, docs/, and benchmarks/ for clean-room violations =="

scan_dirs=()
[ -d src ] && scan_dirs+=(src)
[ -d docs ] && scan_dirs+=(docs)
[ -d benchmarks ] && scan_dirs+=(benchmarks)

# scripts/ and tests/ are deliberately OUTSIDE this scan (same clean-room policy, applied
# with a narrower scope): scripts/generate-jpx-fixtures.sh legitimately names OpenJPEG (the
# per-sample oracle it drives to produce fixture references) and its generated
# tests/PlumePdf.CorpusTests/Fixtures/jpx/MANIFEST.json records that tool's own
# version/command line, neither of which is a competitor-source paste into src/ —
# widening the scan to those directories would make the oracle's own name self-trip.

self="scripts/check-provenance.sh"

run_scan() {
  local label="$1" pattern="$2"
  [ "${#scan_dirs[@]}" -eq 0 ] && return 0
  local hits
  # --exclude-dir=bin/obj: this is a SOURCE-provenance check, not a check on
  # build output — under benchmarks/, bin/obj now legitimately contain the
  # restored itext7/PdfPig binaries themselves (compiled assemblies routinely
  # embed their own copyright/license strings in metadata), which would
  # otherwise false-positive against the exact patterns below on every build.
  hits=$(grep -RInE --exclude-dir=bin --exclude-dir=obj "$pattern" "${scan_dirs[@]}" 2>/dev/null | grep -v "^${self}:" || true)
  if [ -n "$hits" ]; then
    fail "$label (pattern: $pattern):"
    echo "$hits" >&2
  fi
}

# Verbatim competitor copyright/license headers or AGPL license text — these
# strings only appear if real competitor source or license text was pasted
# in; they never appear in our own discursive citations.
run_scan "competitor copyright/license marker"  'iText Group NV'
run_scan "competitor copyright/license marker"  'iText Software Corp'
run_scan "competitor copyright/license marker"  'Aspose Pty Ltd'
run_scan "competitor namespace used as own code" 'Aspose\.Pdf\.Generator'
run_scan "competitor copyright/license marker"  'Syncfusion Inc\.'
run_scan "competitor namespace used as own code" 'Syncfusion\.Pdf\.'
run_scan "competitor copyright/license marker"  'QuestPDF Community License'
run_scan "competitor namespace used as own code" 'QuestPDF\.Fluent'
run_scan "AGPL license text pasted into the repo" 'GNU AFFERO GENERAL PUBLIC LICENSE'
run_scan "AGPL SPDX marker pasted into the repo"  'SPDX-License-Identifier:[[:space:]]*AGPL'

# BouncyCastle was the concrete rejected alternative to the BCL
# System.Security.Cryptography.Pkcs dependency — its namespace used as if it were our own code
# is exactly the shape a copy-pasted CMS/ASN.1 helper would take.
run_scan "competitor namespace used as own code" 'org\.bouncycastle'

# veraPDF is the CI-gating PDF/A exit-demo *oracle* (running its CLI is fine, the clean-room
# policy permits executing restricted binaries) but its validation profiles/source are
# GPLv3/MPLv2 — off the clean-room policy's permissive allowlist — and are never consulted to
# author PlumePdfAValidator's rules. Same bouncycastle-precedent shape: its Java namespace used
# as if it were our own code is exactly what a copy-pasted profile/rule implementation would
# look like.
run_scan "competitor namespace used as own code" 'org\.verapdf'

# Literal ISO-spec page furniture — only appears if spec text (which carries
# this footer on every page of the real document) was pasted verbatim
# instead of being cited by section number (see docs/spec-sources.md).
run_scan "verbatim ISO spec passage (cite by section number instead)" 'ISO 32000-[12]:20[0-9]{2}\(E\)'
run_scan "verbatim ISO spec passage (cite by section number instead)" 'ISO 20[0-9]{2}.{0,20}All rights reserved'

# pdf.js/PDFBox-via-PdfPig/stb_image_write.h are permitted, permissively-licensed PORT sources
# (the clean-room policy in AGENTS.md allows reading their source to reimplement the published
# shape, with NOTICE attribution) — not forbidden competitors like the packages/markers above.
# The distinction this check exists to catch is the same one the clean-room policy draws
# everywhere else: a clean-room port follows an algorithm's published shape into PlumePDF's own
# types, it never carries the upstream FILE's own verbatim copyright/license header comment
# into a PlumePDF source file (that would be vendoring the file wholesale, undisclosed, not
# porting its shape). Each marker below is a distinctive string from that upstream file-header
# comment block, not a name mention — "pdf.js (Apache-2.0)" in prose (this script, the NOTICE
# file) is expected and does NOT trip these.
run_scan "pdf.js file-header copyright pasted verbatim (clean-room port from published shape only, not the vendored file)" 'Copyright [0-9]{4} Mozilla Foundation'
run_scan "Apache PDFBox file-header notice pasted verbatim (clean-room port from published shape only, not the vendored file)" 'Licensed to the Apache Software Foundation \(ASF\)'
run_scan "stb_image_write.h license block pasted verbatim (clean-room port from published shape only, not the vendored file)" 'ALTERNATIVE A - MIT License'

# Same clean-room principle, applied to JPX: the same pdf.js/PDFBox/stb shape above, for
# the three JPEG 2000 implementations PlumePDF's clean-room decoder (ported from
# ITU-T T.800 + amendments and the published algorithm shape only) must never read source
# from or vendor a file header from — OpenJPEG (BSD-2-Clause, used only as the oracle CLI:
# scripts/install-jpx-oracle.sh, scripts/generate-jpx-fixtures.sh — running it is fine,
# reading or pasting its source is not), JJ2000 and Kakadu (neither is used anywhere in
# this repo; the markers exist purely as a tripwire). Each marker is a distinctive string
# from that project's own file-header comment block, not a name mention — "OpenJPEG
# (BSD-2-Clause)" in prose (this script, NOTICE) is expected and does NOT trip these.
run_scan "OpenJPEG file-header notice pasted verbatim (clean-room port from published shape only, not the vendored file)" 'Universite catholique de Louvain'
run_scan "JJ2000 file-header notice pasted verbatim (clean-room port from published shape only, not the vendored file)" 'JJ2000 Partners'
run_scan "Kakadu file-header notice pasted verbatim (clean-room port from published shape only, not the vendored file)" 'Kakadu Software'

if [ "$status" -eq 0 ]; then
  echo "provenance checks passed: no forbidden packages, no clean-room markers found."
fi
exit "$status"
