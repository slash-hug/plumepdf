#!/usr/bin/env bash
# Prints the body of CHANGELOG.md's section for one version — everything between its
# "## [<version>]" heading and the next "## " heading or link definition — for use as release
# notes. Fails when the section is missing, empty, or still headed "Unreleased", so a release can
# never ship without dated notes.
#
#   ./scripts/release-notes.sh 1.0.0-preview.2 > release-notes.md
set -euo pipefail

version="${1:?usage: release-notes.sh <version>}"
changelog="${2:-CHANGELOG.md}"

heading="$(awk -v heading="## [$version]" '{ sub(/\r$/, "") } index($0, heading) == 1 { print; exit }' "$changelog")"
if [ -z "$heading" ]; then
  echo "release-notes: CHANGELOG.md has no '## [$version]' section." >&2
  exit 1
fi

if printf '%s' "$heading" | grep -qi 'unreleased'; then
  echo "release-notes: CHANGELOG.md's '## [$version]' section is still marked Unreleased; date it first." >&2
  exit 1
fi

notes="$(awk -v heading="## [$version]" '
  { sub(/\r$/, "") }
  index($0, heading) == 1 { inside = 1; next }
  inside && (/^## / || /^\[[^]]+\]: /) { exit }
  inside { print }
' "$changelog")"

# Trim leading and trailing blank lines.
notes="$(printf '%s\n' "$notes" | sed -e '/./,$!d' | sed -e ':a' -e '/^\n*$/{$d;N;ba' -e '}')"

if [ -z "$notes" ]; then
  echo "release-notes: CHANGELOG.md's '## [$version]' section is empty." >&2
  exit 1
fi

printf '%s\n' "$notes"
