#!/usr/bin/env bash
# Fetches freely-redistributable spec documents into specs/ (gitignored) for local
# reading — see docs/spec-sources.md for the citation policy (cite by section
# number, never paste text; scripts/check-provenance.sh enforces this mechanically).
#
# ISO 32000-2 (PDF 2.0) is deliberately NOT fetched here: it's EULA-restricted
# (registration/click-through required), so this script prints the sponsored-access
# URL instead of downloading it automatically. Never place a copy of it under
# specs/ or anywhere else in this repo.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p specs
cd specs

fetch() {
  local name="$1" url="$2"
  if [ -f "$name" ]; then echo "${name}: already present"; return; fi
  echo "fetching ${name}…"
  curl -sSfL "$url" -o "$name"
}

fetch "ISO-32000-1-2008_PDF17.pdf" \
  "https://opensource.adobe.com/dc-acrobat-sdk-docs/standards/pdfstandards/pdf/PDF32000_2008.pdf"
fetch "PNG-W3C-Recommendation.html" \
  "https://www.w3.org/TR/png/"
fetch "FIPS-197-AES.pdf" \
  "https://nvlpubs.nist.gov/nistpubs/FIPS/NIST.FIPS.197.pdf"
fetch "FIPS-180-4-SHA.pdf" \
  "https://nvlpubs.nist.gov/nistpubs/FIPS/NIST.FIPS.180-4.pdf"

echo
echo "ISO 32000-2:2020 (PDF 2.0) is EULA-restricted and was NOT fetched."
echo "Visit https://pdfa.org/sponsored-standards/ to read it (registration required)."
echo "Never download a copy of it into this repo, even under specs/ — cite by"
echo "section number only (see docs/spec-sources.md)."
echo
echo "done — specs/ ready."
