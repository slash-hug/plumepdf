# PlumePDF

An open-source (Apache-2.0), high-performance, agent-forward PDF library for .NET. This glossary fixes the project's vocabulary; terms follow ISO 32000 unless noted.

## Language

**Document**:
A single PDF file's complete logical content — its object graph, pages, metadata, and trailer.
_Avoid_: File (that's the byte container), PDF (ambiguous with the format)

**Manuscript**:
The creation-side element tree: a composed description of a document *before* layout. A Manuscript renders into a Document; an opened Document is never a Manuscript.
_Avoid_: template (implies fill-in-the-blanks), model (overloaded)

**Element**:
One node of a Manuscript's tree — a Section, Column, Row, Text, Image, Table, and kin.
_Avoid_: component, widget

**Page**:
One leaf of a Document's page tree, carrying its own content streams, resources, and boxes.

**PDF Object**:
A value of one of the eight basic types defined by ISO 32000: boolean, number, string, name, array, dictionary, stream, or null.
_Avoid_: COS object (Adobe-internal jargon), token (that's the lexical unit, not the value)

**Indirect Object**:
A PDF Object given an object number and generation so other objects can reference it.

**Trailer**:
The dictionary at the end of a PDF that locates the catalog, info, and cross-reference data.

**Cross-Reference Table**:
The index mapping object numbers to their byte offsets (or containing object streams), enabling random access into a Document.
_Avoid_: spelling it "xref" in public API or docs (fine in code internals)

**Content Stream**:
A stream of graphics operators that paints a Page (or form XObject) — text, paths, images.

**Filter**:
An encoding applied to stream data (Flate, DCT, CCITT, JBIG2, JPX, LZW, ASCII variants). Streams may chain several.
_Avoid_: codec (reserve for the image-compression implementations behind DCT/JBIG2/JPX and the standalone raster image codecs — PNG/JPEG/TIFF — behind RasterImage)

**JPX / JPEG 2000 / JP2**:
Three distinct terms for one feature, not synonyms. `JPX` is the PDF filter name only (`/Filter /JPXDecode`, ISO 32000-1 §7.4.9) — use it when naming the dictionary entry or the filter chain. `JPEG 2000` is the compression standard (ITU-T T.800 / ISO/IEC 15444-1) the codec implements — use it when naming the decoder itself (`Filters/Jpx`, "the in-house JPEG 2000 decoder", `PLUME37xx`'s "JPEG 2000 (JPX)" prefix). `JP2` is the box-structured file-wrapper format (Annex I) a `.jp2` file uses to carry a codestream plus metadata (`colr`, `pclr`/`cmap`, `res `) — a raw `.j2k` file has no JP2 wrapper, just the bare codestream.
_Avoid_: "JPX decoder"/"JPX codec" outside the filter-dictionary context — write "JPEG 2000 decoder"; "JP2" as a stand-in for the codestream format itself (a codestream and its JP2 wrapper are different things `Jp2Boxes`/`JpxImageDecoder` parse separately).

**Incremental Update**:
Appending changed objects and a new cross-reference section to an existing file rather than rewriting it, preserving prior revisions (required for signed documents).

**AcroForm**:
The interactive form defined at Document level, whose fields render through page widget annotations.
_Avoid_: form (alone — collides with form XObjects), XFA (a different, deprecated technology)

**Tagged PDF**:
A Document carrying a logical structure tree that makes content order and semantics explicit — the basis of accessibility (PDF/UA) and reliable extraction.

**PDF/A**:
The ISO 19005 archival profiles constraining a Document to self-contained, future-proof content.

## Creation-side language (Phase 2)

**Compose**:
The fluent veneer (`PdfDocument.Compose`) for building a Document by describing its pages
directly — builds a `Manuscript` internally, then renders it. Use `Compose` unless you need to
build, inspect, or transform the element tree as data first; then use `Manuscript` directly (the
package README's "Names to start with" section covers when to escalate).
_Avoid_: build (too generic — collides with .NET build tooling), generate (implies non-PDF output)

**Layout**:
The pass that turns a Manuscript's Element tree into positioned content on Pages: measuring
Elements against their constraints, arranging them, and breaking them across Pages
(pagination). Layout failures raise a structured `PdfLayoutException` naming the constraint,
the Element path, and the offending call.
_Avoid_: rendering as a synonym for layout-into-pixels — **Render** is creation-side only (a
Manuscript renders into a Document); page→pixels is **Rasterize** — and reflow
(implies a live, interactive process; Layout runs once per Render)

**Glyph**:
The visual representation of one character (or ligature) in a specific font — identified by a
numeric glyph ID, distinct from the Unicode codepoint(s) it represents. Content streams
reference Glyphs by ID, never by codepoint directly.
_Avoid_: character (that's the codepoint, before font-specific mapping), symbol

**Shaping**:
Turning a run of characters into positioned Glyphs for a specific font — codepoint-to-glyph
mapping, substitution (ligatures and, for complex scripts, joining-form/reordering
substitution), and positioning (kerning and, for complex scripts, mark attachment), performed by
the internal `ILineShaper` seam. Phase 2 shipped a pure-managed shaper for Latin/Cyrillic/Greek
behind that seam's original signature; Phase 6.5 amended the seam itself (per-glyph X/Y offsets,
cluster ids, run direction, a `ShapingOptions` parameter) to carry complex-script (Arabic/Indic)
output — the original "plugs in later without a signature change" framing did not survive
contact with what mark attachment and N:M clusters actually require.
_Avoid_: rasterizing as a synonym for shaping (rasterizing is turning outlines into pixels —
the Raster layer's job, not the shaper's), text-shaping
library names (HarfBuzz etc.) as if they were our own component — the seam is ours; a specific
shaper implementation is an internal detail

**Rasterize**:
Turning a Page's positioned content — text, paths, images, transparency — into a pixel raster
at a caller-chosen target size, via the in-house managed Raster layer. Read-only:
rasterizing never mutates the document. Best-effort with result-scoped Diagnostics naming what
degraded; `PdfOptions.Strict` refuses instead.
_Avoid_: render (creation-side: a Manuscript renders into a Document — never pixels), paint
(informal; the content stream "paints", the library Rasterizes), screenshot

**RasterImage**:
The standalone raster-imaging type: decoded image frames — pixels plus DPI
metadata — from PNG/JPEG/TIFF/JPEG 2000 bytes, or the output of Rasterize; encodes back out via
`EncodePng`/`EncodeJpeg`. Distinct from **Image**, which is the creation-side layout element.
_Avoid_: bitmap (platform-API connotation), Image (that's the Element)

**Cluster**:
The group of one or more Glyphs that trace back to one or more source-text codepoints as a
single shaping unit — a 1:N ligature (three characters, one Glyph), an N:1 mark attachment (one
base Glyph plus its combining marks), or an N:M Indic syllable (several codepoints reordered
into several Glyphs, none of which map cleanly back to one codepoint each). `ShapedGlyph.Cluster`
carries the id; `/ToUnicode` construction and cursor/selection mapping both key off it, never off
a naive one-glyph-one-codepoint assumption.
_Avoid_: grapheme cluster (a Unicode Standard Annex #29 concept — user-perceived characters
before shaping — related but distinct from a post-shaping glyph Cluster)

**Directional run**:
A maximal span of text at one embedding level under the Unicode Bidirectional Algorithm (UAX #9)
— the unit the bidi algorithm reorders into visual order before line layout and shaping proceed.
A `ShapedRun`'s `Direction` records which way its own Glyphs are already ordered for painting.
_Avoid_: "line" (a Directional run is a bidi-algorithm concept that can span, or fall short of,
a single wrapped line; the two are related but not interchangeable)

**Joining form**:
The specific cursive-connection shape (isolated, initial, medial, or final) a cursive-joining
script's character takes depending on its neighbors — Arabic's defining shaping behavior, driven
by the Unicode `Joining_Type`/`Joining_Group` properties and selected via OpenType's
`isol`/`init`/`medi`/`fina` GSUB features.
_Avoid_: ligature (a joining form is a different *shape* of the same character; a ligature
*merges* multiple characters into one Glyph — Arabic shaping uses both, but they are distinct
operations)

**Syllable**:
An Indic script's structural unit for reordering — typically a base consonant plus any
conjuncts, dependent vowel signs (matras), and modifier marks, identified via the Unicode
`Indic_Syllabic_Category`/`Indic_Positional_Category` properties. Devanagari shaping reorders
within a syllable (e.g. a pre-base matra moves before its base consonant) before emitting
Glyphs.
_Avoid_: conjunct alone (a conjunct — stacked consonants sharing one visual form — is one kind of
syllable-internal structure, not a synonym for the whole syllable)

**Subsetting**:
Producing a reduced copy of a font program that contains only the Glyphs a Document actually
uses, with a deterministic hash-derived six-letter tag prefix (e.g. `ABCDEF+Inter`) distinguishing
it from the full font, per the PDF spec's subset-naming convention.
_Avoid_: compression (subsetting removes glyphs; Filters compress bytes — different operations,
often both applied to the same `/FontFile2` stream)

**Font (Standard-14 / embedded / subset)**:
Named by the public `PdfFont` facade (`PdfFont.Helvetica`, `PdfFont.FromFile`,
`PdfFont.FromBytes`). Three flavors: a **Standard-14** font is one of the 14 base fonts every
PDF viewer must support without embedding (metrics-only, no font program in the file); an
**embedded** font carries its full font program in the output Document; a **subset** font is an
embedded font that has been Subsetted to only the Glyphs actually used. The internal
extension seam a third party would implement to add a new font source (`IFontMetrics`,
`ILineShaper`, the SFNT parser types) stays internal until a 1.x release makes
it public — do not confuse the public `PdfFont` facade with that internal seam.
_Avoid_: typeface (reserve for describing the design itself, not the PDF resource), font family
(a grouping concept `PdfFont` does not model in Phase 2)

**Stamp vs Watermark**:
Both are Manuscript-level Elements painted onto composed Pages; stamping an
already-*opened* Document is `Pdf.Stamp` / `doc.Stamp`, which takes the same `Stamp`. A **Stamp** paints *above*
existing page content (e.g. "DRAFT" in a corner, a signature block) — opaque by default,
typically foreground. A **Watermark** paints *behind* or *through* existing page content —
typically translucent, tiled, or centered, meant to be visible without obscuring the underlying
content (e.g. a diagonal "CONFIDENTIAL"). The distinction is paint order and typical opacity, not
the underlying mechanism — both are `Element` subtypes composed like any other.
_Avoid_: overlay (ambiguous between the two), annotation (a distinct PDF concept — an interactive
object, not painted content)

## Extraction-side language (Phase 3)

**Letter**:
The extraction-side positioned unit: one decoded character (or, rarely, ligature) read off a
content stream, carrying its Unicode text, origin, bounding box, font name/size, and rendering
mode. Deliberately *not* named "Glyph" — the creation-side `Glyph` (above) is a font's visual
representation identified by glyph ID, the opposite direction from a `Letter`'s already-decoded
Unicode text; using one word for both would blur a real distinction PdfPig's own vocabulary
(NearestNeighbourWordExtractor's `Letter`) already avoids. `Letter`s are the escape hatch beneath
`Word`/`Line` — see the "Escape hatch all the way down" pattern already established for
`doc.Objects` and `PdfLayoutException.Measurements`.
_Avoid_: Glyph (the creation-side term, encoding direction — see above), character (ambiguous
with the pre-decode byte code)

**Word**:
A run of `Letter`s assembled by proximity/overlap (the `WordAssembler`, ported/adapted from
PdfPig's `NearestNeighbourWordExtractor`, Apache-2.0) — no dictionary or language knowledge
involved, purely geometric clustering.

**Line**:
A run of `Word`s assembled by the same baseline/geometry pass, in reading order along that
baseline.

**Reading order**:
The page-level ordering PlumePDF assigns to `Line`s. Since Phase 6, a document with a parseable
`/StructTreeRoot` gets **structure-tree-driven** order: words rank by the tag tree's own
document order, resolved through each `Letter`'s marked-content id (`Letter.Mcid`, from the
content stream's `BDC`/`EMC` nesting). Untagged documents — and pages the tree doesn't
reference — keep the Phase 3 geometric heuristic (content-order with geometric line/block
ordering, ported/adapted from PdfPig's `ContentOrderTextExtractor`), with an informational
`PLUME6070` diagnostic recording the fallback. Neither path promises semantically "correct"
order for every layout (tables, multi-column, and rotated text are inherently heuristic in the
geometric case — true of every PDF library); a pluggable strategy family stays deferred.

**ToUnicode CMap**:
The optional PDF stream a font dictionary's `/ToUnicode` entry points at (ISO 32000-1 §9.10.3):
a PostScript-syntax mapping from a font's character codes to Unicode text, consulted first (when
present) when decoding a `Tj`/`TJ` string into `Letter` text. Distinct from Phase 2's `cmap`
(above) — the OpenType SFNT table, a different artifact in a different syntax, in the opposite
direction (Unicode→GID), read from the font *program*, not the font *dictionary*. A Type0
(composite) font's `/Encoding` CMap (mapping byte codes to CIDs) is a third, related-but-distinct
CMap the same parser (`CMapParser`, `PlumePdf.Fonts.Reading`) also reads.
_Avoid_: cmap (bare, lowercase — reserve for the Phase 2 SFNT table; always write "ToUnicode
CMap" or "CMap stream" for this one to keep the two unambiguous in prose)

**ExtractionFont**:
The internal, read-only font family (`PlumePdf.Fonts.Reading`, never public) built from an
opened document's font dictionary: resolves a content-stream character code to `(codeLength,
unicode, width)` via `/Widths`+`/FirstChar` (simple fonts) or `/W`+`/DW` (Type0/composite fonts),
consulting the ToUnicode CMap first when present. Distinct from the public, write-only `PdfFont`
facade (Phase 2, unchanged) — the two have no shared lifecycle; `ExtractionFont` is
discovered from an opened document, `PdfFont` is supplied by the caller composing one.

## AcroForm-side language (Phase 4)

**Field**:
The document-level entry in the AcroForm's `/Fields` tree (ISO 32000-1 §12.7.3): carries a
field's type (`/FT`), value (`/V`), and — for a field with children — its position in the field
hierarchy via `/Kids`. A Field is data; it has no appearance of its own until a Widget renders
it.
_Avoid_: form field alone where "Field" (capitalized, this glossary's term) is unambiguous;
control (a UI-toolkit word this codebase never uses)

**Widget**:
A page-level annotation (`/Annots`, `/Subtype /Widget`, ISO 32000-1 §12.5.6.19) that renders one
Field on one Page — the `/Rect`, `/AP` (appearance streams), and `/MK` (appearance
characteristics: border/background) all live on the Widget, not the Field. A single Field may
have more than one Widget (the same value shown on several pages), though the common real-world
case is exactly one.
_Avoid_: annotation alone (too broad — links, markup, and stamps are annotations too; a Widget is
one specific annotation Subtype), control

**Merged (field+widget) dictionary**:
The overwhelmingly common real-world shape (136/136 widgets on the pinned f1040 exit-demo
fixture): when a Field has exactly one Widget, ISO 32000-1 permits — and nearly
every real-world PDF producer takes advantage of — storing both the Field's entries (`/FT`,
`/V`, `/T`, …) and the Widget's entries (`/Rect`, `/AP`, `/MK`, `/Subtype /Widget`, …) in a
single dictionary object rather than two dictionaries linked by `/Parent`. PlumePDF's field/
widget reader (`AcroFormReader`/`WidgetAnnotationReader`) treats this merged shape as the primary
case, not a special one.

**Appearance stream**:
The content stream (ISO 32000-1 §12.5.5) painted for one Widget's current visual appearance,
referenced from its `/AP` dictionary — `/N` (normal, the only one PlumePDF generates or reads for
fill/flatten), `/D` (down/pressed), `/R` (rollover). For a checkbox/radio Widget, `/AP /N` is
itself a sub-dictionary keyed by On-state name (below), one appearance stream per possible state.
_Avoid_: form XObject alone (an appearance stream *is* a form XObject structurally, but "appearance
stream" is the term that carries the `/AP`-specific meaning — use "form XObject" only when the
Form-XObject mechanism itself, not its use for a widget appearance, is under discussion)

**On-state**:
The arbitrary name (ISO 32000-1 §12.7.4.2.3) identifying a checkbox or radio-button Widget's
"checked"/"selected" appearance — the key under `/AP /N` (and the value `/AS` is set to when that
state is selected). Real-world forms almost never use `/Yes`; the f1040 fixture uses `/1`…`/5`
depending on the field. PlumePDF discovers a field's actual on-state names from its own `/AP /N`
keys rather than assuming any particular name; `/Off` is the one name ISO 32000-1 reserves
by convention for "unchecked," though even that lacks a guaranteed appearance sub-dictionary
entry on every real-world form.
_Avoid_: checked value, selected value (both imply a boolean the PDF structure doesn't actually
have — a radio group's on-state is a name, and different buttons in the same group use different
names)

**Flatten**:
Baking every Widget's current appearance stream into its Page's own content stream (via a form
XObject reference) and then removing the interactive structure entirely — the Widget annotations
from `/Annots`, the Field dictionaries, and the catalog's `/AcroForm` itself. The result renders
identically in any viewer but is no longer fillable; nothing is left for `/NeedAppearances`
to regenerate, which is why Flatten always requires a real appearance stream (generated or
pre-existing) and never accepts `/NeedAppearances` as a substitute.
_Avoid_: rasterize (Flatten stays vector/text content — no pixels are produced; producing
pixels is `Rasterize`, a separate operation), print (a different, unrelated PDF concept)

**Fully-qualified name**:
A Field's complete identity: every ancestor Field's own `/T` (partial name) segment, from the
`/Fields` tree root down to the field itself, joined with `.` (ISO 32000-1 §12.7.3.2) — e.g.
`topmostSubform[0].Page1[0].c1_01[0]` on the f1040 fixture, decoded from UTF-16BE per `/T`'s
string encoding. `doc.Form.Fields[name]` matches a fully-qualified name exactly first,
then falls back to a unique trailing-segment ("partial name") match, throwing a coded,
candidate-naming exception on ambiguity rather than silently picking one.
_Avoid_: field ID, field key (PDF has no separate identifier for a field beyond this name path)

## Digital-signature language (Phase 5)

**PAdES level (B-B / B-T / B-LT / B-LTA)**:
The four escalating conformance levels PlumePDF's signing surface targets (ETSI EN 319 142-1),
each strictly adding validation-longevity material over the last: **B-B** (Basic) is a bare
detached signature — the CMS/PKCS#7 blob and the signer's certificate, valid only as long as
the certificate itself is; **B-T** (Timestamp) adds an unsigned RFC 3161 timestamp attribute
inside the CMS, proving the signature existed at a given time even after the certificate
expires; **B-LT** (Long-Term) adds a `/DSS`/`/VRI` incremental revision carrying the
certificate chain and its revocation material (OCSP/CRL), so validation no longer depends on
those being fetchable later; **B-LTA** (Long-Term Archival) adds a standalone `/DocTimeStamp`
revision over the whole document-so-far, re-timestamping periodically
(`doc.Signatures.AddDocumentTimestampAsync`) so the archive survives even the LTV timestamp's
own eventual expiry. Each level is a
strict superset of the one before, expressed as further incremental revisions — never a
rewrite of an earlier one.
_Avoid_: "signature level" alone (ambiguous with certificate assurance level, a different PKI
concept); PAdES tier (this codebase reserves "tier" for the commercial trust-tier framing in
`docs/spec.md`'s vision)

**ByteRange**:
A signature or `/DocTimeStamp` dictionary's `/ByteRange` array (ISO 32000-1 §12.8.1): the exact
byte offsets and lengths, outside the `/Contents` placeholder itself, that the signature's
digest was computed over and that verification re-hashes to check the signature still matches.
ByteRange-coverage checking — confirming these ranges actually span the *whole* file, not just
some suspiciously partial subset — is a first-class, un-hideable property of
`SignatureVerificationResult`, never a diagnostic a caller could overlook (the "shadow attack"
literature this rule responds to).
_Avoid_: byte range (two words, generic) where the specific `/ByteRange` array is meant —
capitalize/code-format it to keep the PDF-structural meaning unambiguous

**Contents**:
A signature or `/DocTimeStamp` dictionary's `/Contents` entry: the raw CMS/PKCS#7 (or, for a
`/DocTimeStamp`, RFC 3161 DER) bytes themselves, written as a fixed-width hex string so its byte
length — and therefore every offset after it — is known before the real signature exists to
fill it in. Never encrypted, in either direction, even inside an encrypted document (ISO
32000-1 §7.6.2) — the one string value in the whole object model exempt from the security
handler.
_Avoid_: signature bytes alone (ambiguous with the signature *algorithm's* raw output before CMS
wrapping — `/Contents` is always the fully-formed CMS/DER blob, never a bare signature value)

**CMS / detached signature**:
Cryptographic Message Syntax (RFC 5652), the PKCS#7-derived container `/Contents` carries.
"Detached" means the signed content (the ByteRange-covered document bytes) lives outside the
CMS structure itself, referenced only by digest — the CMS blob never embeds a copy of the
document it signs. Built via the BCL's `System.Security.Cryptography.Pkcs.SignedCms`,
never hand-rolled.
_Avoid_: PKCS#7 alone once CMS is available (CMS is the current name for the same syntax family;
use PKCS#7 only when citing the historical/Adobe-specific `adbe.pkcs7.detached` SubFilter name
itself)

**TSA (Time-Stamp Authority)**:
The RFC 3161 service that, given a document's digest, returns a signed token asserting that
digest existed at a specific time — consulted through the public `ITimestampAuthority` seam,
never called directly by name; PlumePDF ships a default `HttpClient`-backed
implementation callers opt into, never one constructed by default.
_Avoid_: timestamp server (a real term, but this codebase always says "TSA" in code and
prose to match the seam's own name and RFC 3161's own terminology)

**DSS (Document Security Store) / VRI (Validation-Related Information)**:
The catalog-level `/DSS` dictionary (Arlington `DSS.tsv`) carries every certificate,
OCSP response, and CRL a signature's long-term validation might need, added as its own
incremental revision (B-LT). Its `/VRI` sub-dictionary partitions that material per signature
(keyed by a hash of the signature's own bytes), so a document with multiple signatures doesn't
force every verifier to sift through material meant for a different one.
_Avoid_: revocation store, cert store (generic terms for a concept the PDF format names
specifically — use the PDF's own `/DSS`/`/VRI` vocabulary in code and docs about this codebase)

**LTV (Long-Term Validation)**:
The umbrella property B-LT/B-LTA exist to provide: a signature stays verifiable long after the
signing certificate itself expires or is revoked, because the validation material it depended
on (chain, revocation status, a trusted timestamp) was captured and preserved inside the
document rather than assumed fetchable on demand later. Not itself a PAdES level name — LTV is
the *property*; B-LT/B-LTA are the *levels* that deliver it.

**DocMDP (Document Modification Detection and Prevention)**:
A certifying signature's declared permission level (ISO 32000-1 §12.8.2.2, Arlington
`SignatureReferenceDocMDP.tsv`'s `/P` value — 1: no changes permitted, 2: form-filling and
signing permitted, 3: form-filling, signing, and annotation permitted) for every change made to
the document *after* that signature. PlumePDF takes an advisory-proceed stance: a
P-value-violating Fill/Flatten is diagnosed by default and refused only under
`PdfOptions.Strict`.
_Avoid_: certification alone (a certifying signature is one specific kind of `/Sig`; "DocMDP"
names the permission mechanism that signature carries, not the act of signing itself)

## Compliance & polish language (Phase 6)

**PDF/A conformance levels**:
The specific ISO 19005 profile a document targets or is validated against — `PdfAConformance`
(`None`/`A1b`/`A2b`), not "PDF/A" as an undifferentiated whole (that broader term is the main
Language section's own entry, above). Phase 6 create/validate coverage is **A1b** (basic, ISO
19005-1 — forces PlumePDF's PDF-version write knob to `1.4` and forbids object/cross-reference
streams) and **A2b** (basic, ISO 19005-2 — Phase 6's primary create target) only;
every other profile (2u/2a/3(a/b/u)/4(e/f)) is out of v1.0 scope and PlumePDF's own validator
reports it as not-checked rather than silently passing.
_Avoid_: "PDF/A-compliant" as a bare claim without naming the level — always say which
conformance level (1b, 2b, ...) a document targets or was validated against.

**PDF/UA**:
The ISO 14289 accessibility profile built on Tagged PDF's structure tree (ISO 32000-1 §14.8):
correct reading order, alternate text on non-text content, a document title and language, and a
structurally sound `/StructTreeRoot`. PlumePDF's scope is the honest bar: a structurally
valid tree with caller-supplied semantics, opt-in and coded-refusal when
PDF/UA output is requested and a required semantic (alt text, heading level, title, language) is
missing — never best-effort auto-tagging, and never an unconditional "PDF/UA-clean" claim, since
conformance is substantially about authoring quality no library can guarantee on the caller's
behalf.
_Avoid_: accessibility alone (the broader, non-PDF-specific concept); "PDF/UA-compliant" as an
unconditional claim about arbitrary output — PlumePDF's own claim is scoped to structural
validity plus whatever semantics the caller actually supplied.

**Output Intent**:
The catalog-level `/OutputIntents` array entry (ISO 32000-1 §14.11.5) declaring the color
characteristics a PDF/A (or PDF/X) document's content is intended to be reproduced under — an
embedded ICC profile plus a registered output-condition identifier. Mandatory for PDF/A-1b/2b
conformance; PlumePDF ships a bundled CC0-licensed compact sRGB ICC profile as its default so PDF/A creation
works out of the box, with a caller-supplied-profile override for a different target color
space.
_Avoid_: color profile alone (the ICC profile is the *payload*; "Output Intent" is the
dictionary structure wrapping it, including the registered condition identifier a bare profile
doesn't carry)

**Structure Tree**:
A document's logical structure hierarchy (`/StructTreeRoot`, ISO 32000-1 §14.7.2) — the object
graph of nested `StructureElement`s (paragraph, heading, table, figure, ...) that Tagged PDF
authoring builds and PDF/UA conformance depends on, paired with a `/ParentTree` number tree
mapping page content back to its owning structure element. Read and write sides share one model
(`Documents.Structure`): once a parseable Structure Tree exists, extraction's
Reading order (Phase 3 glossary entry, above) becomes Structure-Tree-driven instead of the
geometric-heuristic default.
_Avoid_: tag tree, accessibility tree (both real terms elsewhere; this codebase always says
"structure tree" to match ISO 32000-1's own `/StructTreeRoot` naming)

**Marked Content**:
A content stream's explicit `BDC`/`BMC`/`EMC` operator pairs (ISO 32000-1 §14.6) delimiting a
run of graphics operators as belonging to one Structure Tree element (via an `MCID` tag) or as an
`/Artifact` (page furniture — headers, footers, watermarks — deliberately excluded from the
logical structure). The mechanism that actually connects painted content to the Structure Tree
above; a Structure Tree with no corresponding Marked Content in the page's content stream is
structurally incomplete.
_Avoid_: tagged content (a looser synonym; "Marked Content" is the ISO 32000-1 operator-level
term this codebase's Content layer emits)

**Redaction**:
Making content genuinely unrecoverable — never merely visually hidden — both at the object level
(the full-rewrite writer's reachability garbage collection, so no prior revision preserves the
original bytes; requires `Save`, never `SaveIncremental`) and at the content-stream
level (removing the actual text-showing/painting operators a redacted region covers, not drawing
an opaque shape on top of surviving content underneath — the infamous real-world failure mode
this distinction exists to prevent). An enumerated scrub-surface list (DocInfo, XMP, annotation
`/Contents`, `/ActualText`/`/Alt`, `/PieceInfo`, embedded-file attachments, outline titles) is
part of the same guarantee; a redaction that only clears one surface is not PlumePDF's Redaction.
_Avoid_: masking, blackout (both describe the visual-only anti-pattern this term explicitly
excludes); delete (too generic — Redaction is a specific, audited removal with a result report,
not an ordinary content-mutation call)

**Linearization**:
Reordering a document's objects (first page's objects first, plus hint streams with back-patched
byte offsets) so a viewer can begin rendering page one while the rest of the file is still
downloading — "fast web view" (ISO 32000-1 Annex F). A `Save`-time-only option:
two-pass output through the writer's existing temp-file staging (a documented, narrow carve-out
of "no whole-document byte buffer, ever" — the promise reads as no whole-document *in-memory*
buffer), destroyed by construction if any later `SaveIncremental`
appends onto the file (recorded as a de-linearization diagnostic by default, a refusal under
`PdfOptions.Strict`).
_Avoid_: fast web view alone as the primary term in code/API (fine as parenthetical context;
"Linearization" is what this codebase's option/verb names say)

**Optimization**:
Writing a document's cross-reference data as compressed Object Streams and a Cross-Reference
Stream (ISO 32000-1 §7.5.7/§7.5.8) instead of a classic table plus loose indirect objects —
previously entirely absent from PlumePDF's writer (a `Save` of a modern, already-optimized
source actually *inflated* it before Phase 6). Gated by PDF/A conformance level: forbidden at
PDF/A-1b (pre-1.5 object/cross-reference streams don't exist at that version), permitted at
PDF/A-2b.
_Avoid_: compression alone (Filters, above, already owns that word for stream-payload encoding;
Optimization is specifically about xref/object-stream *structure*, orthogonal to whether any
given stream's own bytes are Flate-compressed)
