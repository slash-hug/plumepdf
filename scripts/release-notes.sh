#!/usr/bin/env bash
# Prints the body of CHANGELOG.md's section for one version — everything between its
# "## [<version>]" heading and the next "## [" heading — for use as release notes.
# Fails when the section is missing or empty, so a release can never ship without notes.
#
#   ./scripts/release-notes.sh 1.0.0-preview.2 > release-notes.md
set -euo pipefail

version="${1:?usage: release-notes.sh <version>}"
changelog="${2:-CHANGELOG.md}"

notes="$(awk -v heading="## [$version]" '
  index($0, heading) == 1 { inside = 1; next }
  inside && /^## \[/ { exit }
  inside { print }
' "$changelog")"

# Trim leading and trailing blank lines.
notes="$(printf '%s\n' "$notes" | sed -e '/./,$!d' | sed -e ':a' -e '/^\n*$/{$d;N;ba' -e '}')"

if [ -z "$notes" ]; then
  echo "release-notes: CHANGELOG.md has no (or an empty) '## [$version]' section." >&2
  exit 1
fi

printf '%s\n' "$notes"
