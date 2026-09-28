#!/usr/bin/env bash
# Fetches the openly-redistributable conformance corpora into corpora/ (gitignored).
# Arlington (Apache-2.0) and veraPDF-corpus (CC BY 4.0) may be fetched freely;
# Isartor/SafeDocs are no-redistribution and are NOT fetched here — add them locally if
# needed, never commit them.
#
# Also fetches a pinned subset of Mozilla pdf.js's test/pdfs real-world corpus
# (Apache-2.0), one file at a time from a fixed commit SHA — not a submodule, so clone
# time stays fast and the set is reproducible across CI runs. Never commit these files
# to this repo; they only ever live in gitignored corpora/.
#
# Two more lanes, forms-specific: a gating extension of the pinned pdf.js subset above
# (annotation/widget fixtures) and a separate, non-gating, failure-tolerant fetch of the
# real-world exit-demo forms (f1040/I-9) from their verified web.archive.org snapshots —
# see the comments at each section below for per-URL licensing notes.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p corpora
cd corpora

fetch() {
  local name="$1" url="$2"
  if [ -d "$name" ]; then echo "$name: already present"; return; fi
  echo "fetching ${name}…"
  curl -sSfL "$url" -o "$name.zip"
  unzip -q "$name.zip"
  rm "$name.zip"
}

fetch "veraPDF-corpus-master" "https://github.com/veraPDF/veraPDF-corpus/archive/refs/heads/master.zip"
fetch "arlington-pdf-model-master" "https://github.com/pdf-association/arlington-pdf-model/archive/refs/heads/master.zip"

# --- Pinned pdf.js test/pdfs subset -----------------------------------------
# Pinned to a specific commit so the fetched set never silently changes.
PDFJS_COMMIT="0adc2e7c6652b967617e3a1820183224abbab4ca"
PDFJS_BASE_URL="https://raw.githubusercontent.com/mozilla/pdf.js/${PDFJS_COMMIT}/test/pdfs"
PDFJS_DIR="pdfjs-subset-${PDFJS_COMMIT:0:12}"

# 27 real-world files curated from mozilla/pdf.js's test/pdfs (Apache-2.0) for
# structural and format-shape diversity within Phase 1 scope: classic/damaged
# xref (xref_command_missing, GHOSTSCRIPT-*-fuzzed), real Standard Security
# Handler encryption (encrypted-attachment), page-tree edge cases
# (Pages-tree-refs), and assorted minimal real-producer output (the
# issueNNNN/bugNNNN files are Mozilla's own reduced bug-report repros).
PDFJS_FILES=(
  "helloworld-bad.pdf"
  "xref_command_missing.pdf"
  "encrypted-attachment.pdf"
  "bug1020226.pdf"
  "issue15590.pdf"
  "clippath.pdf"
  "asciihexdecode.pdf"
  "issue4461.pdf"
  "checkbox_no_appearance.pdf"
  "bad-PageLabels.pdf"
  "file_url_link.pdf"
  "issue4575.pdf"
  "REDHAT-1531897-0.pdf"
  "issue4304.pdf"
  "Pages-tree-refs.pdf"
  "acroform_calculation_order.pdf"
  "90ms_rksj_h_sample.pdf"
  "bitmap-symbol.pdf"
  "issue1293r.pdf"
  "bug1606566.pdf"
  "GHOSTSCRIPT-698804-1-fuzzed.pdf"
  "IdentityToUnicodeMap_charCodeOf.pdf"
  "issue17554.pdf"
  "bug2035197_2.pdf"
  "bug850854.pdf"
  "bug878026.pdf"
  "basicapi.pdf"
)

# 38 more files, added in Phase 4: pdf.js's own annotation/widget fixtures, same commit,
# same mechanism, same Apache-2.0 license - the CI-gating half of the two-lane forms
# corpus (the non-gating half, the real-world exit-demo forms, is fetched separately
# below). Widget/field fixtures (annotation-*-widget, annotation-tx*, textfields,
# checkbox-bad-appearance, listbox_actions, resetform, js-buttons,
# bug1947248_forms, file_pdfjs_form, form_two_pages, widget_hidden_print) give
# the field/widget reader real producer-generated shapes to enumerate. The two
# xfa_*.pdf fixtures exercise the XFA-drop-on-fill path without PlumePDF
# ever parsing XFA content. The remaining annotation-*.pdf files are markup
# (not widget) annotation subtypes plus their "-without-appearance" siblings -
# included because /Annots-walking code must safely skip past every annotation
# subtype it isn't looking for, not just find the ones it is.
PDFJS_FORMS_FILES=(
  "annotation-border-styles.pdf"
  "annotation-button-widget.pdf"
  "annotation-caret-ink.pdf"
  "annotation-choice-widget.pdf"
  "annotation-freetext.pdf"
  "annotation-highlight-without-appearance.pdf"
  "annotation-highlight.pdf"
  "annotation-ink-without-appearance.pdf"
  "annotation-line-without-appearance.pdf"
  "annotation-line.pdf"
  "annotation-polyline-polygon-without-appearance.pdf"
  "annotation-polyline-polygon.pdf"
  "annotation-square-circle-without-appearance.pdf"
  "annotation-square-circle.pdf"
  "annotation-squiggly-without-appearance.pdf"
  "annotation-squiggly.pdf"
  "annotation-strikeout-without-appearance.pdf"
  "annotation-strikeout.pdf"
  "annotation-text-widget.pdf"
  "annotation-text-without-popup.pdf"
  "annotation-tx.pdf"
  "annotation-tx2.pdf"
  "annotation-tx3.pdf"
  "annotation-underline-without-appearance.pdf"
  "annotation-underline.pdf"
  "annotation_hidden_print.pdf"
  "bug1947248_forms.pdf"
  "checkbox-bad-appearance.pdf"
  "file_pdfjs_form.pdf"
  "form_two_pages.pdf"
  "freetext_no_appearance.pdf"
  "js-buttons.pdf"
  "listbox_actions.pdf"
  "resetform.pdf"
  "textfields.pdf"
  "widget_hidden_print.pdf"
  "xfa_filled_imm1344e.pdf"
  "xfa_issue14315.pdf"
)


# Phase 7: pdf.js's own JBIG2/CCITT decode-parity fixtures, same pinned commit as every
# other pdf.js file above — the corpus's Jbig2BitmapMatrixTests (bitmap-symbol-*/
# bitmap-halftone-* decode-parity, ~50-file non-empty-set assert) and the CCITT/JBIG2-globals
# edge-case tests (ccitt_EndOfBlock_false, jbig2_file_header, jbig2_symbol_offset) consume.
# 14 bitmap-halftone-* + 37 bitmap-symbol-* +
# 3 edge-case files = 54 (plus JBIG2Globals.pdf below, fetched separately since pdf.js stores
# it as a .link pointer to an external URL, not a byte-committed file at this path).
PDFJS_CODEC_FILES=(
  "bitmap-halftone-10bpp-mmr.pdf"
  "bitmap-halftone-10bpp.pdf"
  "bitmap-halftone-composite.pdf"
  "bitmap-halftone-grid.pdf"
  "bitmap-halftone-refine.pdf"
  "bitmap-halftone-skip-dummy.pdf"
  "bitmap-halftone-skip-grid-template1.pdf"
  "bitmap-halftone-skip-grid-template2.pdf"
  "bitmap-halftone-skip-grid-template3.pdf"
  "bitmap-halftone-skip-grid.pdf"
  "bitmap-halftone-template1.pdf"
  "bitmap-halftone-template2.pdf"
  "bitmap-halftone-template3.pdf"
  "bitmap-halftone.pdf"
  "bitmap-symbol-big-segmentid.pdf"
  "bitmap-symbol-context-reuse.pdf"
  "bitmap-symbol-empty.pdf"
  "bitmap-symbol-negative-sbdsoffset.pdf"
  "bitmap-symbol-refine.pdf"
  "bitmap-symbol-symbolrefine-textrefine.pdf"
  "bitmap-symbol-symbolrefineone-customat.pdf"
  "bitmap-symbol-symbolrefineone-template1.pdf"
  "bitmap-symbol-symbolrefineone.pdf"
  "bitmap-symbol-symbolrefineseveral.pdf"
  "bitmap-symbol-symhuff-texthuff.pdf"
  "bitmap-symbol-symhuff-texthuffB10B13.pdf"
  "bitmap-symbol-symhuffB5B3-texthuffB7B9B12.pdf"
  "bitmap-symbol-symhuffcustom-texthuffcustom.pdf"
  "bitmap-symbol-symhuffrefine-textrefine.pdf"
  "bitmap-symbol-symhuffrefineone.pdf"
  "bitmap-symbol-symhuffrefineseveral.pdf"
  "bitmap-symbol-symhuffuncompressed-texthuff.pdf"
  "bitmap-symbol-textbottomleft.pdf"
  "bitmap-symbol-textbottomlefttranspose.pdf"
  "bitmap-symbol-textbottomright.pdf"
  "bitmap-symbol-textbottomrighttranspose.pdf"
  "bitmap-symbol-textcomposite.pdf"
  "bitmap-symbol-texthuffrefine.pdf"
  "bitmap-symbol-texthuffrefineB15.pdf"
  "bitmap-symbol-texthuffrefinecustom.pdf"
  "bitmap-symbol-texthuffrefinecustomdims.pdf"
  "bitmap-symbol-texthuffrefinecustompos.pdf"
  "bitmap-symbol-texthuffrefinecustomposdims.pdf"
  "bitmap-symbol-texthuffrefinecustomsize.pdf"
  "bitmap-symbol-textrefine-customat.pdf"
  "bitmap-symbol-textrefine-negative-delta-width.pdf"
  "bitmap-symbol-textrefine.pdf"
  "bitmap-symbol-texttopright.pdf"
  "bitmap-symbol-texttoprighttranspose.pdf"
  "bitmap-symbol-texttranspose.pdf"
  "bitmap-symbol.pdf"
  "ccitt_EndOfBlock_false.pdf"
  "jbig2_file_header.pdf"
  "jbig2_symbol_offset.pdf"
)

PDFJS_ALL_FILES=("${PDFJS_FILES[@]}" "${PDFJS_FORMS_FILES[@]}" "${PDFJS_CODEC_FILES[@]}")

if [ -d "$PDFJS_DIR" ] && [ "$(find "$PDFJS_DIR" -name '*.pdf' ! -name 'JBIG2Globals.pdf' | wc -l)" -eq "${#PDFJS_ALL_FILES[@]}" ]; then
  echo "$PDFJS_DIR: already present (${#PDFJS_ALL_FILES[@]} files)"
else
  echo "fetching $PDFJS_DIR (${#PDFJS_ALL_FILES[@]} files from pdf.js@${PDFJS_COMMIT:0:12})…"
  mkdir -p "$PDFJS_DIR"
  for f in "${PDFJS_ALL_FILES[@]}"; do
    curl -sSfL "${PDFJS_BASE_URL}/${f}" -o "${PDFJS_DIR}/${f}"
  done
fi

# JBIG2Globals.pdf: pdf.js stores this one as a *.pdf.link pointer file (its own convention
# for a fixture too large/binary to commit directly) whose one line of text names the real
# download URL — resolved here in the same two-step shape pdf.js's own test-fetching tooling
# uses, independent of the completeness check above (own idempotency guard) so a re-run never
# re-downloads it needlessly.
if [ -f "${PDFJS_DIR}/JBIG2Globals.pdf" ]; then
  echo "${PDFJS_DIR}/JBIG2Globals.pdf: already present"
else
  echo "resolving JBIG2Globals.pdf.link…"
  JBIG2_GLOBALS_URL="$(curl -sSfL "${PDFJS_BASE_URL}/JBIG2Globals.pdf.link")"
  curl -sSfL "$JBIG2_GLOBALS_URL" -o "${PDFJS_DIR}/JBIG2Globals.pdf"
fi

# --- Non-gating exit-demo forms (Phase 4) -----------------------------------
# The real-world government forms the Phase 4 exit demo fills and flattens,
# fetched from their verified, immutable web.archive.org snapshots rather than
# the live government URL (which changes revision over time and would
# silently break the pin later). Both are U.S. federal government works —
# public domain under 17 U.S.C. Sec 105, no redistribution restriction.
#
# Characteristics verified against these exact snapshot bytes
# (`qpdf --qdf --object-streams=disable`, since object-stream
# compression hides dictionary content from a plain `strings` scan):
#   - f1040-2022.pdf: 136 /Widget annotations, 39 pre-existing /AP, /XFA
#     present, no /Encrypt, no /NeedAppearances.
#   - i-9.pdf: /Encrypt present, /XFA present, /Perms and /UR3 present,
#     /NeedAppearances present. The 2020-07-22 revision is pinned
#     deliberately, not the newest snapshot — later USCIS editions moved off
#     Adobe LiveCycle/usage-rights encryption and no longer exercise the
#     fill paths this demo set exists to cover.
#
# archive.org is slow and rate-limited under CI load, so this step is
# deliberately NOT gating: a failed fetch here is logged and the script keeps
# going rather than exiting non-zero — forms exit-demo tests self-skip (with a
# visible, distinguishable message) when these files are absent, exactly like
# every other corpus-dependent test in this repo.
mkdir -p demo-forms
cd demo-forms

fetch_demo_form() {
  local name="$1" url="$2"
  if [ -f "$name" ]; then
    echo "$name: already present"
    return
  fi

  echo "fetching ${name} (non-gating demo form)…"
  if curl -sSfL "$url" -o "$name"; then
    echo "$name: fetched"
  else
    echo "DEMO-FORM FETCH SKIPPED: ${name} could not be fetched from ${url} (archive.org may be rate-limiting, or the snapshot moved) — this is not a build failure; forms exit-demo tests self-skip without it." >&2
    rm -f "$name"
  fi
}

# IRS Form 1040, tax year 2022. Live source: https://www.irs.gov/pub/irs-prior/f1040--2022.pdf
fetch_demo_form "f1040-2022.pdf" \
  "https://web.archive.org/web/20260503034830/https://www.irs.gov/pub/irs-prior/f1040--2022.pdf"

# USCIS Form I-9, 2020-07-22 revision. Live source: https://www.uscis.gov/sites/default/files/document/forms/i-9.pdf
fetch_demo_form "i-9.pdf" \
  "https://web.archive.org/web/20200722211013/https://www.uscis.gov/sites/default/files/document/forms/i-9.pdf"

cd ..

# --- Pinned Adobe Core 14 AFM files ------------------------------------------
# scripts/generate-standard14.csx reads corpora/afm/*.afm to regenerate
# src/PlumePdf/Fonts/Standard14/Standard14Metrics.g.cs. Adobe's own devnet zip
# (https://www.adobe.com/devnet/font/pdfs/Core14_AFMs.zip) has no stable per-file URL to pin
# a checksum against, so this fetches from tecnickcom/tc-font-core14-afms — a version-
# controlled mirror of that exact same zip's contents (same Adobe AFM license, same
# provenance already recorded in NOTICE/docs/spec-sources.md) — pinned to one signed commit,
# with each file's SHA-256 verified after download.
mkdir -p afm
cd afm

AFM_COMMIT="0675784d24b28a55c607cad6b74596ce19ce333c"
AFM_BASE_URL="https://raw.githubusercontent.com/tecnickcom/tc-font-core14-afms/${AFM_COMMIT}"

# name -> sha256, one fetch_font call per Core 14 font (fetch_font/verify_checksum are
# defined below, alongside the OFL font fixtures they were written for).
AFM_FILES=(
  "Helvetica.afm:da33f1870474c8e68bfe3e2353ff107ab6c6eea1f9836ce2aaf1e1a07b17982f"
  "Helvetica-Bold.afm:b880d96baf56d0cc059f258f60b4d764ef49b555ab9db294b959c0016dee41f2"
  "Helvetica-Oblique.afm:b4609b71b660a392ac09df35060271a876c2ce66617dd83bf826f742bb9d9721"
  "Helvetica-BoldOblique.afm:69984a35ca26973a39f261cf83e0d367ea2e6517c590b0b030d4d4a219d9c269"
  "Times-Roman.afm:768e1cabea085d489a63da3e80b96bc5abf0ec98d3073c9b4d6ba76e7bccba64"
  "Times-Bold.afm:b4a000ed85cb22c6cdd985aa0fd3f6f78ed5079b7c6860dc4f0234e0d0e3c522"
  "Times-Italic.afm:ed37fa2e6a67b5b17dfd47f36fc7e90df32891a4408860dbc8d4cbbe9959e242"
  "Times-BoldItalic.afm:93c4744ba955215de02c4aae0b777133442ada2f7ab5a2af30a040b792b3c55d"
  "Courier.afm:521e0d7c7521efd4be78a5a9c5398e4c67d0771e396115b0346bc4ef74ada53d"
  "Courier-Bold.afm:ad0150d4bedcc8877742bf94251fcec13e348dd599d4603f679d92027d1e6e99"
  "Courier-Oblique.afm:b27103b2a2ef6030c110626597e2ab47bb8279075a039ab9170facb0aa1f70e1"
  "Courier-BoldOblique.afm:cb82e69ef5f6d421e8f404fe00bb0d993425aae79725331d0ebf847e94e97e92"
  "Symbol.afm:3d2128a820375a10de9bc8bf6cfb15ded482c01ca0f95cc0b3277f37ec8bde66"
  "ZapfDingbats.afm:a32565c90afd1b57a7008fc567b78d95cf1c22adff5e086094d666d88b039859"
)

checksum_of() {
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | cut -d' ' -f1
  else
    sha256sum "$1" | cut -d' ' -f1
  fi
}

verify_checksum() {
  local name="$1" expected="$2" actual
  actual=$(checksum_of "$name")
  if [ "$actual" != "$expected" ]; then
    echo "CHECKSUM MISMATCH for $name: expected $expected, got $actual" >&2
    rm -f "$name"
    exit 1
  fi
}

for entry in "${AFM_FILES[@]}"; do
  name="${entry%%:*}"
  sha256="${entry##*:}"
  if [ -f "$name" ]; then
    echo "$name: already present"
    continue
  fi

  echo "fetching ${name}…"
  curl -sSfL "${AFM_BASE_URL}/${name}" -o "$name"
  verify_checksum "$name" "$sha256"
done

cd ..

# --- Pinned OFL font fixtures ------------------------------------------------
# TrueType (glyf-outline) OFL fonts for the Fonts subsystem's tests: SfntReader, cmap
# (format 4 + format 12 — every font below ships both subtable formats simultaneously),
# glyf/loca, GSUB ligature substitution, GPOS pair kerning, and the subsetter. Each is
# pinned to an immutable commit/release (never a mutable branch tip) with a SHA-256
# checksum verified after download — a corrupted or substituted download fails loudly
# rather than silently feeding a different font into the test suite. Fetched into
# corpora/fonts/ (gitignored, never committed); font tests self-skip (with a visible
# console message — a skipped lane must be distinguishable from a passing one) when this
# directory is absent.
mkdir -p fonts
cd fonts

# checksum_of/verify_checksum are already defined above (the AFM fetch section).

fetch_font() {
  local name="$1" url="$2" sha256="$3"
  if [ -f "$name" ]; then
    echo "$name: already present"
    return
  fi

  echo "fetching ${name}…"
  curl -sSfL "$url" -o "$name"
  verify_checksum "$name" "$sha256"
}

# Noto Sans Regular/Bold, static TrueType build — notofonts/latin-greek-cyrillic release
# NotoSans-v2.015 (immutable GitHub release asset). Provides GSUB 'liga' + GPOS 'kern'
# (class-based, format 2) alongside format-4 and format-12 cmap subtables.
if [ -f "NotoSans-Regular.ttf" ] && [ -f "NotoSans-Bold.ttf" ]; then
  echo "NotoSans-Regular.ttf, NotoSans-Bold.ttf: already present"
else
  echo "fetching NotoSans-v2.015.zip (for NotoSans-Regular.ttf, NotoSans-Bold.ttf)…"
  curl -sSfL "https://github.com/notofonts/latin-greek-cyrillic/releases/download/NotoSans-v2.015/NotoSans-v2.015.zip" -o "NotoSans.zip"
  verify_checksum "NotoSans.zip" "0c34df072a3fa7efbb7cbf34950e1f971a4447cffe365d3a359e2d4089b958f5"
  unzip -q -o "NotoSans.zip" "NotoSans/unhinted/ttf/NotoSans-Regular.ttf" "NotoSans/unhinted/ttf/NotoSans-Bold.ttf"
  mv "NotoSans/unhinted/ttf/NotoSans-Regular.ttf" NotoSans-Regular.ttf
  mv "NotoSans/unhinted/ttf/NotoSans-Bold.ttf" NotoSans-Bold.ttf
  rm -rf "NotoSans.zip" "NotoSans"
  verify_checksum "NotoSans-Regular.ttf" "f3961a9cde016d41a4879aecda1474d3a36d6bf54fa0e4643de029cc2248b0e8"
  verify_checksum "NotoSans-Bold.ttf" "87cb2d84472a7d66da659ee47b6cdb9552326e8c128245231f191b6ac72529d9"
fi

# EB Garamond, ligature-rich Latin face (OFL) — pinned to google/fonts commit
# e1118da94a8cb00cf6d06cdac9ef13eb1e5c6ab7. Its GSUB 'liga' feature substitutes ff/fi/fl/
# ffi/ffl, exercising LigatureSubst format 1 with 3-component ligatures.
fetch_font "EBGaramond.ttf" \
  "https://raw.githubusercontent.com/google/fonts/e1118da94a8cb00cf6d06cdac9ef13eb1e5c6ab7/ofl/ebgaramond/EBGaramond%5Bwght%5D.ttf" \
  "ef9512f92f6d579e5dc75af59a5a4b1b8b47d2eda89e00b954d44520e5369027"

# --- Pinned Arabic/Devanagari shaping fixtures ---------------------------------------
# Noto Naskh Arabic + Noto Sans Devanagari, static-glyf OFL builds — verified (outside this
# script; see tests/PlumePdf.Tests/Fonts/FontFixtureOutlineFormatTests.cs) to carry glyf/loca
# (no CFF/CFF2/fvar) and the arab / dev2+deva GSUB script records the shaping engine needs.
# Pinned to immutable notofonts GitHub *release* zips (not a mutable branch tip), same
# fetch_font/verify_checksum pattern as the Noto Sans stanza above.
if [ -f "NotoNaskhArabic-Regular.ttf" ]; then
  echo "NotoNaskhArabic-Regular.ttf: already present"
else
  echo "fetching NotoNaskhArabic-v2.021.zip (for NotoNaskhArabic-Regular.ttf)…"
  curl -sSfL "https://github.com/notofonts/arabic/releases/download/NotoNaskhArabic-v2.021/NotoNaskhArabic-v2.021.zip" -o "NotoNaskhArabic.zip"
  verify_checksum "NotoNaskhArabic.zip" "6c050ab9bd087d69b733c505a7576e60c528c2f33cd7b91005a5bd7da4514032"
  unzip -q -o "NotoNaskhArabic.zip" "NotoNaskhArabic/unhinted/ttf/NotoNaskhArabic-Regular.ttf"
  mv "NotoNaskhArabic/unhinted/ttf/NotoNaskhArabic-Regular.ttf" NotoNaskhArabic-Regular.ttf
  rm -rf "NotoNaskhArabic.zip" "NotoNaskhArabic"
  verify_checksum "NotoNaskhArabic-Regular.ttf" "c34fdbd98af4dbc45ca192a23d2eeb77032add83086f5fe32957d23b3f36b221"
fi

if [ -f "NotoSansDevanagari-Regular.ttf" ]; then
  echo "NotoSansDevanagari-Regular.ttf: already present"
else
  echo "fetching NotoSansDevanagari-v2.006.zip (for NotoSansDevanagari-Regular.ttf)…"
  curl -sSfL "https://github.com/notofonts/devanagari/releases/download/NotoSansDevanagari-v2.006/NotoSansDevanagari-v2.006.zip" -o "NotoSansDevanagari.zip"
  verify_checksum "NotoSansDevanagari.zip" "4c582c103f0a42836338df07148b23a0aa080cce8393ddc4364af87eb22ebd85"
  unzip -q -o "NotoSansDevanagari.zip" "NotoSansDevanagari/unhinted/ttf/NotoSansDevanagari-Regular.ttf"
  mv "NotoSansDevanagari/unhinted/ttf/NotoSansDevanagari-Regular.ttf" NotoSansDevanagari-Regular.ttf
  rm -rf "NotoSansDevanagari.zip" "NotoSansDevanagari"
  verify_checksum "NotoSansDevanagari-Regular.ttf" "216921eded5a97435fa0638deca66496bf51f52fa3467f566deb9938c25a71de"
fi

cd ..

# --- Pinned UCD BidiTest.txt / BidiCharacterTest.txt ----------------------------------------
# The UCD conformance oracle for BidiConformanceTests.cs, pinned to the exact same
# UNICODE_VERSION (16.0.0) scripts/generate-unicode-data.csx pins for the Bidi_Class table
# these tests exercise — a version bump to one must be paired with a bump to the other.
# Fetched directly (not zipped) from unicode.org's own versioned Public/ tree (immutable
# once published), each file's SHA-256 verified after download.
mkdir -p ucd/16.0.0
cd ucd/16.0.0

fetch_ucd_file() {
  local name="$1" sha256="$2"
  if [ -f "$name" ]; then
    echo "$name: already present"
    return
  fi

  echo "fetching ${name}…"
  curl -sSfL "https://www.unicode.org/Public/16.0.0/ucd/${name}" -o "$name"
  verify_checksum "$name" "$sha256"
}

fetch_ucd_file "BidiTest.txt" "93e5eb9d88ca89dcf895f5576486a3363762ad2aa8f2db2fa56fe60cb82b9520"
fetch_ucd_file "BidiCharacterTest.txt" "d04a51a90052dcd71c4e91ee5b3a9d973ee35c12406b5a99875ac8163c8f2804"

cd ../..

echo "done — corpora/ ready."
