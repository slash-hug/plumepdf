# PlumePdf error codes

Every `PlumePdfException` (and every `PLUME####` entry in `doc.Diagnostics`) carries a stable, greppable `Code` and, for exceptions, an `Exception.HelpLink` pointing at that code's page in this directory:

```
https://github.com/slash-hug/plumepdf/blob/main/docs/errors/PLUME####.md
```

Codes are never renumbered or reused once shipped. If a failure mode is retired, its page stays and is marked deprecated rather than deleted.

## Range allocation

Codes are allocated per layer, four digits, grouped by leading digit. This table is the authority for range allocation.

| Range | Layer / concern |
|---|---|
| `PLUME0xxx` | Reserved (illustrative codes in XML-doc `<example>`s and tests only — never a real shipped code) |
| `PLUME1xxx` | IO — file/stream/memory-mapped access, resource-limit trips |
| `PLUME2xxx` | Parsing / Objects — tokenizer, object parser, cross-reference resolution, recovery ladder |
| `PLUME3xxx` | Filters — codecs, predictor, filter registry; from Phase 7, sub-allocated by codec: `3200`–`3249` JPEG, `3250`–`3279` PNG, `3300`–`3349` TIFF, `3400`–`3449` CCITT, `3500`–`3559` JBIG2, `3600`–`3629` `RasterImage`/`Pdf.FromImages` refusals that are really codec refusals surfacing through the verb (`Pdf.FromImages`' own *orchestration* failures — empty input set, unreadable source, save-to-open-path — reuse the existing 5xxx/6xxx verb machinery instead); `3700`–`3749` JPEG 2000 (JPX) |
| `PLUME4xxx` | Encryption & signing — security-handler setup, wrong password, unsupported cipher; from Phase 5, also CMS build/parse, DER normalization, certificate-chain build/validation, and TSA/OCSP/CRL transport failures |
| `PLUME5xxx` | Writer — incremental update, full rewrite, save-to-open-path contract, encrypted-source save refusal; from Phase 4, also the general dirty-object-set/allocator and encrypt-on-append; from Phase 5, also ByteRange/`/Contents`-placeholder reservation overflow and the signed-source `Save` guard; from Phase 6, also linearization and the redaction-dirty `SaveIncremental` refusal |
| `PLUME6xxx` | Documents / verbs — `PdfDocument` hub, `Pdf.Merge`/`Split`, page mutation; from Phase 4, also the field/widget model, `/AcroForm` reading, fill/flatten orchestration; from Phase 5, also the signature-field model, verify orchestration, DSS/LTV, and DocMDP semantics; from Phase 6, also redaction/PDF/A/PDF-UA orchestration, the structure-tree model, and XMP/DocInfo metadata write |
| `PLUME7xxx` | Content — content-stream generation, encode-side filters; from Phase 4, also widget appearance-stream generation; from Phase 6, also marked-content emission and redaction's content-stream rewriting |
| `PLUME8xxx` | Fonts — SFNT parsing, embedding, subsetting, shaping; from Phase 4, also `/DA` font resolution and the Unicode→character-code encode direction; from Phase 6, also PDF/A Standard-14 embedding refusals; from Phase 6.5, also complex-script (Arabic/Devanagari) OpenType Layout shaping refusals — shaping budget exceeded, missing capability, unsupported script tier (starting at `8024`) |
| `PLUME9xxx` | Layout — `Manuscript`, layout engine, pagination; from Phase 6, also tagging from the Manuscript tree; from Phase 6.5, also UAX#9 bidirectional-algorithm and text-direction refusals (starting at `9013` — `BidiAlgorithm`'s embedding-depth cap itself follows UAX#9's own defined graceful overflow rather than refusing; `9013` is `TextLayouter`'s bidi-isolate refusal, X10's isolating-run-sequence construction not being implemented) |
| `PLUME7500`–`PLUME7999` | **Phase 8/9** (Raster — page rasterization, SSIM-vs-PDFium). This sub-band keeps raster codes separate from this table's own live `7001`–`7018` Content-layer codes (both are content-stream concerns — generation vs. consumption — so they share the leading digit; codes stay four-digit, the `HelpLinkTests`-pinned `PLUME####` shape is untouched). Reserved in Phase 7 precisely so Phase 8/9 cannot mint into the range `7019`–`7499` already leaves open for Content's own future growth. In use as of Phase 8: `7500`–`7504` (surface/display-list caps and rotation-adapter wiring), `7510`–`7512` (substitute-font diagnostics and the glyph-outline render cap), `7709`–`7728` (functions, color spaces, shadings, tiling patterns, soft masks — `7724` is skipped, reserved for future use within the same sub-range rather than reused), `7729`–`7730` (text rasterization: `7729` retired 2026-09 — non-embedded Symbol/ZapfDingbats now renders via the bundled Foxit faces and reports `PLUME7510` instead — the code stays reserved, never reused; `7730` is best-effort stroke/clip text render modes). In use as of Phase 9: `7731`–`7742` (annotations, optional content/OCG, mesh shadings, Cal\*/`/Lab` color, `/Matte`, non-isolated transparency-group backdrop-capture (`7739`, the reserved knockout-cap slot), and the explicitly-1.x print-pipeline exotica diagnostics), plus `7743` (the fatal, page-wide `/Annots`-array-size cap — split out from `7731` so the recoverable per-annotation `/AP` diagnostic and the unconditional resource-limit refusal never share one code). In use for image-XObject rasterization: `7744`–`7748` and `7750` (per-image decode/dictionary degradation, the retired JPX filter-registration diagnostic, the formerly-silent Do drop paths, and XObject-level `/OC` suppression) — `7749` is skipped, reserved rather than reused (the `7724` precedent): it was planned as the stencil-stream-`/Mask` deferral diagnostic, but both `/Mask` forms shipped in scope, so no deferral exists to diagnose. `7745` retired 2026-09: JPEG 2000 (`JPXDecode`) now decodes in-house by default, so the "register your own `IPdfFilter`" diagnostic it named never fires — the code stays reserved, never reused (the `7729` precedent), and its page is marked deprecated with a pointer at the `3700`–`3718` JPX-refusal band and the still-live `7744` (decode failure). Last-used code: `7753` (JPX declared-`/ColorSpace` component-count mismatch against the codestream; `7749`/`7751` both stay reserved-unminted). |

`doc.Diagnostics` entries share this same code space: a diagnostic and an exception for the same failure class carry the same code, so an agent can look a code up once regardless of whether it appeared as a thrown exception or a recorded diagnostic.

## Exception vs. diagnostic

Per `AGENTS.md`'s exception policy: a recoverable deviation from a well-formed PDF (malformed but repairable cross-reference table, a truncated Flate stream that still decodes usably, and so on) is recorded to `doc.Diagnostics` and reading continues — it is **never** silently swallowed and **never** thrown as an exception, unless `PdfOptions.Strict` is set. Only unrecoverable failures — and programmer errors like invalid arguments — throw. Programmer errors (null/invalid arguments at public entry points) throw plain BCL exceptions (`ArgumentNullException`, etc.), not `PlumePdfException` — those don't get a `PLUME####` page here.

## Index

This index is populated one page per code as codes are minted in `src/`, in the same PR that mints the code or the next one. Each page follows the same shape:

- **Cause** — what condition trips this code
- **Example** — a minimal snippet or input that trips it
- **Fix** — what the caller should change
- **Recovery attempted** — what PlumePdf already tried before giving up (for the recovery ladder, which rung failed)

| Code | Summary |
|---|---|
| [PLUME1001](PLUME1001.md) | could not access the source path |
| [PLUME1002](PLUME1002.md) | file not found |
| [PLUME1003](PLUME1003.md) | empty file cannot be memory-mapped |
| [PLUME1004](PLUME1004.md) | could not memory-map the file |
| [PLUME1005](PLUME1005.md) | source region too large for a single in-memory array |
| [PLUME1006](PLUME1006.md) | cannot buffer an over-2GiB mapped source to save over it in place |
| [PLUME2010](PLUME2010.md) | object nesting exceeded the configured limit |
| [PLUME2011](PLUME2011.md) | unexpected end of input while parsing a value |
| [PLUME2012](PLUME2012.md) | unexpected token type while parsing a value (diagnostic) |
| [PLUME2013](PLUME2013.md) | malformed token substituted with a best-effort value (diagnostic) |
| [PLUME2014](PLUME2014.md) | array ran to end of input without a closing ']' (diagnostic) |
| [PLUME2016](PLUME2016.md) | unexpected keyword while parsing a value (diagnostic) |
| [PLUME2017](PLUME2017.md) | dictionary ran to end of input without a closing '>>' (diagnostic) |
| [PLUME2018](PLUME2018.md) | expected a dictionary key name (diagnostic) |
| [PLUME2019](PLUME2019.md) | stream length recovered by scanning for 'endstream' (diagnostic) |
| [PLUME2020](PLUME2020.md) | expected 'endstream' after the stream payload (diagnostic) |
| [PLUME2021](PLUME2021.md) | malformed indirect-object framing |
| [PLUME2022](PLUME2022.md) | expected 'endobj' after an object's value (diagnostic) |
| [PLUME2030](PLUME2030.md) | cross-reference /Prev chain revisits an offset (diagnostic) |
| [PLUME2031](PLUME2031.md) | cross-reference /Prev chain exceeded the configured length limit (diagnostic) |
| [PLUME2032](PLUME2032.md) | no usable cross-reference trailer with a /Root entry was found |
| [PLUME2033](PLUME2033.md) | cross-reference offset out of range (diagnostic) |
| [PLUME2034](PLUME2034.md) | no recognizable cross-reference section at the given offset (diagnostic) |
| [PLUME2035](PLUME2035.md) | classic cross-reference table ran to end of input before 'trailer' (diagnostic) |
| [PLUME2036](PLUME2036.md) | expected a subsection header or 'trailer' (diagnostic) |
| [PLUME2037](PLUME2037.md) | malformed or overstated cross-reference subsection entry (diagnostic) |
| [PLUME2038](PLUME2038.md) | cross-reference stream is missing a valid /W field-width array, or declares negative/zero field widths |
| [PLUME2039](PLUME2039.md) | cross-reference stream data ended before all declared entries were read (diagnostic) |
| [PLUME2040](PLUME2040.md) | 'startxref' not found, or not followed by a valid offset |
| [PLUME2041](PLUME2041.md) | brute-force recovery found neither a trailer nor a /Type /Catalog object |
| [PLUME2042](PLUME2042.md) | trailer synthesized from a recovered /Type /Catalog object (diagnostic) |
| [PLUME2043](PLUME2043.md) | cross-reference data could not be read cleanly; falling back to brute-force recovery (diagnostic) |
| [PLUME2050](PLUME2050.md) | object stream has no entry at the requested index |
| [PLUME2051](PLUME2051.md) | object stream /Extends chain cycles |
| [PLUME2052](PLUME2052.md) | object stream /Extends chain exceeded the configured depth limit |
| [PLUME2053](PLUME2053.md) | the referenced object is not an object stream |
| [PLUME2054](PLUME2054.md) | object stream is missing /N or /First |
| [PLUME2055](PLUME2055.md) | object stream entry has an out-of-range offset (diagnostic) |
| [PLUME2056](PLUME2056.md) | object stream header entry is missing its object number or offset; header stopped early (diagnostic) |
| [PLUME2057](PLUME2057.md) | object stream /N clamped to what the payload or PdfOptions.MaxObjectStreamEntries could plausibly hold (diagnostic) |
| [PLUME2060](PLUME2060.md) | object not present in the cross-reference table, or marked free (diagnostic) |
| [PLUME2061](PLUME2061.md) | failed to parse an object at its cross-reference offset (diagnostic) |
| [PLUME2062](PLUME2062.md) | cross-reference offset resolved to a different object number than expected (diagnostic) |
| [PLUME2063](PLUME2063.md) | failed to read an object out of its object stream (diagnostic) |
| [PLUME2064](PLUME2064.md) | object recovered via full-file scan (diagnostic) |
| [PLUME3001](PLUME3001.md) | FlateDecode: zlib header missing or corrupt; retrying as raw DEFLATE (diagnostic) |
| [PLUME3002](PLUME3002.md) | FlateDecode: not valid zlib-wrapped or raw DEFLATE data |
| [PLUME3003](PLUME3003.md) | FlateDecode: stream ended before its DEFLATE data was fully consumed (diagnostic) |
| [PLUME3004](PLUME3004.md) | decompressed output exceeds PdfOptions.MaxDecompressedStreamBytes |
| [PLUME3010](PLUME3010.md) | unsupported filter name |
| [PLUME3011](PLUME3011.md) | **deprecated** — indirect /Filter or /DecodeParms is not resolved (superseded by indirect-reference resolution in the filter pipeline) |
| [PLUME3012](PLUME3012.md) | indirect /Filter or /DecodeParms resolved to an unusable value (diagnostic) |
| [PLUME3020](PLUME3020.md) | ASCIIHexDecode: invalid character (diagnostic) |
| [PLUME3021](PLUME3021.md) | ASCIIHexDecode: missing EOD marker (diagnostic) |
| [PLUME3030](PLUME3030.md) | ASCII85Decode: byte outside the base-85 digit range (diagnostic) |
| [PLUME3031](PLUME3031.md) | ASCII85Decode: 'z' shortcut used mid-group (diagnostic) |
| [PLUME3032](PLUME3032.md) | ASCII85Decode: invalid 1-digit final group (diagnostic) |
| [PLUME3033](PLUME3033.md) | ASCII85Decode: missing or malformed EOD marker (diagnostic) |
| [PLUME3040](PLUME3040.md) | RunLengthDecode: missing EOD marker (diagnostic) |
| [PLUME3041](PLUME3041.md) | RunLengthDecode: truncated run (diagnostic) |
| [PLUME3042](PLUME3042.md) | RunLengthDecode output exceeds the decompression cap |
| [PLUME3050](PLUME3050.md) | LZWDecode: code references an unpopulated dictionary entry (diagnostic) |
| [PLUME3051](PLUME3051.md) | LZWDecode: missing EOD code (diagnostic) |
| [PLUME3052](PLUME3052.md) | LZWDecode: decompressed output exceeds the configured cap |
| [PLUME3053](PLUME3053.md) | LZWDecode: invalid /EarlyChange value (diagnostic) |
| [PLUME3100](PLUME3100.md) | predictor row width is zero |
| [PLUME3101](PLUME3101.md) | unsupported PNG predictor filter type on a row |
| [PLUME3102](PLUME3102.md) | TIFF predictor with unsupported bit depth |
| [PLUME3103](PLUME3103.md) | predictor parameters are not positive integers |
| [PLUME3200](PLUME3200.md) | JPEG: arithmetic entropy coding is not supported |
| [PLUME3201](PLUME3201.md) | JPEG: input does not begin with an SOI marker |
| [PLUME3202](PLUME3202.md) | JPEG: unsupported frame type or sample precision |
| [PLUME3203](PLUME3203.md) | JPEG: malformed marker segment |
| [PLUME3204](PLUME3204.md) | JPEG: unsupported component count |
| [PLUME3205](PLUME3205.md) | JPEG: entropy-coded scan data ended before all blocks were decoded (diagnostic) |
| [PLUME3206](PLUME3206.md) | JPEG: restart marker not found where expected (diagnostic) |
| [PLUME3207](PLUME3207.md) | JPEG: stream ended without an EOI marker (diagnostic) |
| [PLUME3208](PLUME3208.md) | JPEG: decoded pixel buffer exceeds the resource cap |
| [PLUME3209](PLUME3209.md) | JPEG: scan references an undefined Huffman or quantization table id (diagnostic) |
| [PLUME3250](PLUME3250.md) | PNG: missing or invalid signature |
| [PLUME3251](PLUME3251.md) | PNG: malformed chunk stream |
| [PLUME3252](PLUME3252.md) | PNG: unsupported bit depth/color type combination |
| [PLUME3253](PLUME3253.md) | PNG: missing, malformed, or out-of-range palette |
| [PLUME3254](PLUME3254.md) | PNG: IDAT stream failed to decompress or is truncated |
| [PLUME3255](PLUME3255.md) | PNG: decoded pixel count exceeds the decode cap |
| [PLUME3256](PLUME3256.md) | PNG: unsupported interlace method |
| [PLUME3257](PLUME3257.md) | PNG: malformed ancillary chunk (tRNS/pHYs) ignored (diagnostic) |
| [PLUME3300](PLUME3300.md) | TIFF: data does not begin with a recognized byte-order marker |
| [PLUME3301](PLUME3301.md) | TIFF: an IFD offset or entry is out of range or truncated |
| [PLUME3302](PLUME3302.md) | TIFF: IFD chain revisits an already-seen offset (cycle guard) |
| [PLUME3303](PLUME3303.md) | TIFF: IFD chain exceeds the frame-count cap |
| [PLUME3304](PLUME3304.md) | TIFF: frame dimensions exceed the pixel-decode cap |
| [PLUME3310](PLUME3310.md) | TIFF: unsupported Compression value |
| [PLUME3311](PLUME3311.md) | TIFF: CMYK or YCbCr photometric interpretation is not supported |
| [PLUME3312](PLUME3312.md) | TIFF: PlanarConfiguration 2 (separate planes) is not supported |
| [PLUME3313](PLUME3313.md) | TIFF: old-style JPEG compression (6) is not supported |
| [PLUME3314](PLUME3314.md) | TIFF: new-style JPEG compression (7) is not supported (deliberate 1.x scope) |
| [PLUME3315](PLUME3315.md) | TIFF: a frame is missing required tags or has invalid dimensions |
| [PLUME3316](PLUME3316.md) | TIFF: a strip or tile's data is out of range, or the frame failed to decode |
| [PLUME3317](PLUME3317.md) | TIFF: unsupported BitsPerSample value |
| [PLUME3318](PLUME3318.md) | TIFF: SamplesPerPixel is invalid for the frame's PhotometricInterpretation |
| [PLUME3319](PLUME3319.md) | TIFF: frame's row/pixel buffer size exceeds the supported allocation size |
| [PLUME3401](PLUME3401.md) | CCITTFaxDecode: bad code word |
| [PLUME3402](PLUME3402.md) | CCITTFaxDecode: premature end of input |
| [PLUME3403](PLUME3403.md) | CCITTFaxDecode: a decoded run overran the declared row width (diagnostic) |
| [PLUME3404](PLUME3404.md) | CCITTFaxDecode /EncodedByteAlign contradicted by the data; flag ignored (diagnostic) |
| [PLUME3405](PLUME3405.md) | CCITTFaxDecode: decoded output exceeds the pixel-decode cap |
| [PLUME3501](PLUME3501.md) | JBIG2: the stream could not be decoded at all |
| [PLUME3502](PLUME3502.md) | JBIG2: symbol count exceeds the symbol-budget cap |
| [PLUME3503](PLUME3503.md) | JBIG2: page bitmap would exceed the pixel-decode cap |
| [PLUME3504](PLUME3504.md) | JBIG2: the /JBIG2Globals stream could not be resolved to raw segment bytes |
| [PLUME3550](PLUME3550.md) | JBIG2: halftone/pattern-dictionary segments are not supported (per-segment fallback) |
| [PLUME3551](PLUME3551.md) | JBIG2: unrecognized segment type (per-segment fallback) |
| [PLUME3552](PLUME3552.md) | JBIG2: a segment failed to decode (per-segment fallback) |
| [PLUME3553](PLUME3553.md) | JBIG2: generic refinement region uses TPGRON typical prediction, which is not supported |
| [PLUME3554](PLUME3554.md) | JBIG2: Huffman-coded symbol dictionary is not supported |
| [PLUME3555](PLUME3555.md) | JBIG2: symbol dictionary refinement/aggregation issue (per-segment fallback) |
| [PLUME3556](PLUME3556.md) | JBIG2: symbol dictionary export flags selected zero symbols (diagnostic, exports everything decoded instead) |
| [PLUME3557](PLUME3557.md) | JBIG2: Huffman-coded text region is not supported |
| [PLUME3558](PLUME3558.md) | JBIG2: decoded arithmetic integer exceeds Int32 range (per-segment fallback, surfaces as PLUME3552) |
| [PLUME3559](PLUME3559.md) | JBIG2: text region declares symbol instances but no symbols are available (per-segment fallback) |
| [PLUME3600](PLUME3600.md) | RasterImage.Decode: unrecognized container |
| [PLUME3601](PLUME3601.md) | codec dispatch not yet wired in this facade — **retired**, no longer thrown |
| [PLUME3602](PLUME3602.md) | Image(RasterImageFrame): unsupported pixel format |
| [PLUME3603](PLUME3603.md) | RasterImage.Decode: every frame of a TIFF source failed to decode |
| [PLUME3604](PLUME3604.md) | RasterImage.Decode: unsupported JPEG 2000 channel layout |
| [PLUME3610](PLUME3610.md) | Pdf.FromImages does not produce PDF/A output |
| [PLUME3611](PLUME3611.md) | Pdf.FromImages: no image sources |
| [PLUME3612](PLUME3612.md) | Pdf.FromImages: output path collides with an input source |
| [PLUME3613](PLUME3613.md) | RasterImage.Decode: could not read the source file |
| [PLUME3614](PLUME3614.md) | Pdf.FromImages: source skipped (diagnostic) |
| [PLUME3615](PLUME3615.md) | Pdf.FromImages: a source or output path is malformed |
| [PLUME3700](PLUME3700.md) | JPEG 2000 (JPX): not a JPEG 2000 stream |
| [PLUME3701](PLUME3701.md) | JPEG 2000 (JPX): codestream truncated (diagnostic) |
| [PLUME3702](PLUME3702.md) | JPEG 2000 (JPX): Part 2 codestream refused |
| [PLUME3703](PLUME3703.md) | JPEG 2000 (JPX): RGN (region of interest) refused |
| [PLUME3704](PLUME3704.md) | JPEG 2000 (JPX): packed packet headers (PPM/PPT) refused |
| [PLUME3705](PLUME3705.md) | JPEG 2000 (JPX): component precision exceeds 16 bits |
| [PLUME3706](PLUME3706.md) | JPEG 2000 (JPX): image/tile geometry inconsistent |
| [PLUME3707](PLUME3707.md) | JPEG 2000 (JPX): missing, duplicate, or misplaced main-header marker |
| [PLUME3708](PLUME3708.md) | JPEG 2000 (JPX): tile-part sequencing broken (diagnostic) |
| [PLUME3709](PLUME3709.md) | JPEG 2000 (JPX): packet header malformed (diagnostic) |
| [PLUME3710](PLUME3710.md) | JPEG 2000 (JPX): code-block segments disagree with tier-2 signalling (diagnostic) |
| [PLUME3711](PLUME3711.md) | JPEG 2000 (JPX): segmentation symbol mismatch (diagnostic) |
| [PLUME3712](PLUME3712.md) | JPEG 2000 (JPX): unsupported wavelet, MCT, or quantisation style |
| [PLUME3713](PLUME3713.md) | JPEG 2000 (JPX): POC volume malformed (diagnostic) |
| [PLUME3714](PLUME3714.md) | JPEG 2000 (JPX): JP2 box structure malformed (diagnostic) |
| [PLUME3715](PLUME3715.md) | JPEG 2000 (JPX): colr box unrecognised or inconsistent (diagnostic) |
| [PLUME3716](PLUME3716.md) | JPEG 2000 (JPX): fragment table or multiple codestreams (diagnostic) |
| [PLUME3717](PLUME3717.md) | JPEG 2000 (JPX): illegal code-block size |
| [PLUME3718](PLUME3718.md) | JPEG 2000 (JPX): image exceeds the pixel-decode cap |
| [PLUME3749](PLUME3749.md) | JPEG 2000 (JPX): decode not implemented in this build — **deprecated** |
| [PLUME4001](PLUME4001.md) | encryption dictionary is missing /O or /U |
| [PLUME4002](PLUME4002.md) | no supplied password authenticates the document |
| [PLUME4003](PLUME4003.md) | AES-256 encryption dictionary is malformed |
| [PLUME4004](PLUME4004.md) | AES-encrypted data is shorter than its required 16-byte IV prefix |
| [PLUME4005](PLUME4005.md) | AES decryption failed |
| [PLUME4006](PLUME4006.md) | failed to decrypt an object; still-encrypted value used (diagnostic) |
| [PLUME4007](PLUME4007.md) | unsupported or unusable signing certificate |
| [PLUME4009](PLUME4009.md) | malformed ASN.1/DER input |
| [PLUME4011](PLUME4011.md) | OCSP/CRL revocation fetch failed |
| [PLUME4012](PLUME4012.md) | RFC 3161 timestamp request failed or was not configured |
| [PLUME4013](PLUME4013.md) | signing/verification resource cap exceeded (untrusted input) |
| [PLUME4016](PLUME4016.md) | unsupported digest algorithm |
| [PLUME5001](PLUME5001.md) | `Save` refused on an encrypted source |
| [PLUME5002](PLUME5002.md) | `SaveIncremental` refused on an encrypted source |
| [PLUME5003](PLUME5003.md) | `SaveIncremental` requires a backing byte source |
| [PLUME5005](PLUME5005.md) | `SaveIncremental` has no prior `startxref` to chain onto |
| [PLUME5010](PLUME5010.md) | writer object-graph invariant violated |
| [PLUME5011](PLUME5011.md) | signature larger than its reserved `/Contents` space |
| [PLUME5012](PLUME5012.md) | `/ByteRange` value too large for its reserved digit width |
| [PLUME5013](PLUME5013.md) | signing pass missing its placeholder objects |
| [PLUME5014](PLUME5014.md) | `Save` (full rewrite) would invalidate an existing signature |
| [PLUME5015](PLUME5015.md) | signature placeholder written outside a signing pass |
| [PLUME5016](PLUME5016.md) | `SaveIncremental` refused on a document with an applied redaction |
| [PLUME5017](PLUME5017.md) | `Optimize` requires PDF 1.5 or later (PDF/A-1b forces 1.4) |
| [PLUME5018](PLUME5018.md) | `Optimize` and `Linearize` cannot be combined |
| [PLUME5019](PLUME5019.md) | `SaveIncremental` after a linearized save de-linearizes (diagnostic; `Strict` refuses) |
| [PLUME5020](PLUME5020.md) | `Linearize` requires at least one page |
| [PLUME5021](PLUME5021.md) | `Save` left out what pointed at removed pages (info) |
| [PLUME6001](PLUME6001.md) | catalog could not be resolved (diagnostic) |
| [PLUME6002](PLUME6002.md) | catalog's `/Pages` could not be resolved (diagnostic) |
| [PLUME6010](PLUME6010.md) | malformed page-tree node skipped (diagnostic) |
| [PLUME6011](PLUME6011.md) | page-tree cycle or depth cap exceeded (diagnostic) |
| [PLUME6012](PLUME6012.md) | cannot merge/split pages from an encrypted source |
| [PLUME6020](PLUME6020.md) | Form XObject recursion exceeded the configured depth limit |
| [PLUME6021](PLUME6021.md) | page text extraction produced more than the configured letter limit |
| [PLUME6022](PLUME6022.md) | inline image skipped (diagnostic) |
| [PLUME6023](PLUME6023.md) | image uses a filter chain PlumePDF cannot decode (diagnostic) |
| [PLUME6024](PLUME6024.md) | extraction against a document whose /P clears the "extract content" bit (advisory) |
| [PLUME6025](PLUME6025.md) | Form XObject recursion during image extraction exceeded the configured depth limit |
| [PLUME6026](PLUME6026.md) | **deprecated** — a font's /ToUnicode CMap could not be decoded (superseded by PLUME8022) |
| [PLUME6027](PLUME6027.md) | **deprecated** — a font has no /ToUnicode entry; text falls back to U+FFFD (superseded by PLUME8020) |
| [PLUME6028](PLUME6028.md) | page text extraction exceeded the configured cumulative operator limit |
| [PLUME6029](PLUME6029.md) | page has no object graph to extract from |
| [PLUME6030](PLUME6030.md) | field name matches more than one field |
| [PLUME6031](PLUME6031.md) | no form field with this name |
| [PLUME6032](PLUME6032.md) | field type does not support the requested operation |
| [PLUME6033](PLUME6033.md) | unrecognized checkbox/radio on-state |
| [PLUME6034](PLUME6034.md) | value not among a choice field's `/Opt` options |
| [PLUME6035](PLUME6035.md) | form resource limit exceeded |
| [PLUME6036](PLUME6036.md) | widget has no appearance to flatten and none synthesizable; left live (diagnostic) |
| [PLUME6037](PLUME6037.md) | `/XFA` dropped on fill (diagnostic) |
| [PLUME6038](PLUME6038.md) | usage rights/signature invalidated by fill (diagnostic) |
| [PLUME6039](PLUME6039.md) | advisory 'fill form fields' permission bit (diagnostic) |
| [PLUME6040](PLUME6040.md) | malformed form structure tolerated (diagnostic) |
| [PLUME6041](PLUME6041.md) | filled field's appearance not regenerated (diagnostic) |
| [PLUME6042](PLUME6042.md) | widget /Rect unusable for appearance generation |
| [PLUME6043](PLUME6043.md) | /DA font unresolvable for appearance generation |
| [PLUME6044](PLUME6044.md) | fill value not encodable in the /DA font |
| [PLUME6045](PLUME6045.md) | generated appearance exceeds the size cap |
| [PLUME6046](PLUME6046.md) | multiline fill value truncated in the generated appearance (diagnostic) |
| [PLUME6047](PLUME6047.md) | signing target has no resolvable field/catalog to attach to |
| [PLUME6048](PLUME6048.md) | no signer configured |
| [PLUME6049](PLUME6049.md) | synchronous `Sign`/`Add` requires a network round trip |
| [PLUME6050](PLUME6050.md) | no existing signature to extend |
| [PLUME6051](PLUME6051.md) | DocMDP certification requires this to be the first signature |
| [PLUME6052](PLUME6052.md) | signature verification resource cap exceeded |
| [PLUME6053](PLUME6053.md) | deterministic signing scope violated |
| [PLUME6054](PLUME6054.md) | encrypted-source signing is out of scope |
| [PLUME6055](PLUME6055.md) | IPdfSigner.DigestAlgorithm does not match PdfSignOptions.DigestAlgorithm |
| [PLUME6056](PLUME6056.md) | serialized XMP packet exceeds `PdfOptions.MaxXmpPacketWriteBytes` |
| [PLUME6057](PLUME6057.md) | XMP packet and DocInfo metadata disagree |
| [PLUME6058](PLUME6058.md) | PDF/A metadata combined with `PdfOptions.Deterministic` requires caller-supplied dates |
| [PLUME6059](PLUME6059.md) | cannot set XMP metadata: document has no resolvable catalog |
| [PLUME6060](PLUME6060.md) | redaction match count exceeds the configured limit |
| [PLUME6061](PLUME6061.md) | encrypted-source redaction is out of scope |
| [PLUME6062](PLUME6062.md) | redaction would invalidate an existing signature |
| [PLUME6063](PLUME6063.md) | **deprecated** — redacting a permissions-restricted source (unreachable from the day it shipped; removed) |
| [PLUME6064](PLUME6064.md) | number tree entry count exceeds the configured limit |
| [PLUME6065](PLUME6065.md) | structure tree exceeds the element-count safety cap |
| [PLUME6066](PLUME6066.md) | structure tree nests deeper than the configured limit |
| [PLUME6067](PLUME6067.md) | marked-content reference targets a page outside the document |
| [PLUME6068](PLUME6068.md) | unresolvable marked-content reference tolerated (diagnostic) |
| [PLUME6069](PLUME6069.md) | structure tree truncated at a safety cap (diagnostic) |
| [PLUME6070](PLUME6070.md) | structure-tree reading order unavailable, falling back to geometry (diagnostic) |
| [PLUME6071](PLUME6071.md) | PDF/A-2B version knob above the 1.7 ceiling (or unparseable) |
| [PLUME6072](PLUME6072.md) | stamp text not encodable in the Standard-14 stamp font |
| [PLUME6073](PLUME6073.md) | `Stamp` requires at least one page |
| [PLUME6074](PLUME6074.md) | redaction refused rather than removing an intersecting image |
| [PLUME6075](PLUME6075.md) | annotation appearance wiped by an intersecting redaction region (diagnostic) |
| [PLUME6076](PLUME6076.md) | redaction's structure-tree scrub exceeded the configured caps |
| [PLUME6080](PLUME6080.md) | XMP packet exceeds the read-size cap |
| [PLUME6081](PLUME6081.md) | XMP field contains a character XML cannot represent |
| [PLUME6082](PLUME6082.md) | malformed marked-content /MCID tolerated (diagnostic) |
| [PLUME6083](PLUME6083.md) | page /Contents resolved to no usable content streams (diagnostic) |
| [PLUME7001](PLUME7001.md) | content-stream graphics-state stack underflow |
| [PLUME7002](PLUME7002.md) | nested text object (`BT` while already inside one) |
| [PLUME7003](PLUME7003.md) | `ET` with no matching `BT` |
| [PLUME7004](PLUME7004.md) | content stream finalized with unbalanced `q`/`Q` |
| [PLUME7005](PLUME7005.md) | content stream finalized with an unclosed text object |
| [PLUME7010](PLUME7010.md) | content stream contains more operators than the configured limit |
| [PLUME7011](PLUME7011.md) | content stream ended with pending operands and no closing operator (diagnostic) |
| [PLUME7012](PLUME7012.md) | malformed inline image (diagnostic) |
| [PLUME7013](PLUME7013.md) | content-stream graphics-state stack nesting exceeded the configured limit |
| [PLUME7014](PLUME7014.md) | 'Q' with no matching 'q' to restore (diagnostic) |
| [PLUME7015](PLUME7015.md) | non-finite matrix operand (diagnostic) |
| [PLUME7016](PLUME7016.md) | `EMC` with no matching `BMC`/`BDC` to end |
| [PLUME7017](PLUME7017.md) | unmatched marked-content sequence at `Build()` |
| [PLUME7018](PLUME7018.md) | redaction content-stream editing exceeded the cumulative operator budget |
| [PLUME7500](PLUME7500.md) | rasterize target surface is invalid or exceeds the size cap |
| [PLUME7501](PLUME7501.md) | raster display-list build's graphics-state stack nested too deep |
| [PLUME7502](PLUME7502.md) | raster display-list build recursed through too many Form XObjects |
| [PLUME7503](PLUME7503.md) | raster display-list object count exceeds the configured limit |
| [PLUME7504](PLUME7504.md) | shading resolution needs an object registry that wasn't supplied |
| [PLUME7510](PLUME7510.md) | glyph rendered using a bundled substitute font (diagnostic) |
| [PLUME7511](PLUME7511.md) | non-embedded font needs CJK coverage the substitute-font bundle lacks (diagnostic) |
| [PLUME7512](PLUME7512.md) | glyph outline exceeds the configured render-path point limit |
| [PLUME7709](PLUME7709.md) | malformed PDF function dictionary |
| [PLUME7710](PLUME7710.md) | malformed Type 0 (sampled) function |
| [PLUME7711](PLUME7711.md) | Type 0 function sample table exceeds the defensive cap |
| [PLUME7712](PLUME7712.md) | Type 2 function's /C0 and /C1 disagree in component count |
| [PLUME7713](PLUME7713.md) | malformed Type 3 (stitching) function |
| [PLUME7714](PLUME7714.md) | /ICCBased color space's embedded profile is not applied (diagnostic) |
| [PLUME7715](PLUME7715.md) | malformed or unsupported /ColorSpace entry |
| [PLUME7716](PLUME7716.md) | /Lab color space out of scope (Phase 8) — **deprecated**, superseded by PLUME7738 in Phase 9 |
| [PLUME7717](PLUME7717.md) | /Indexed color space is not supported by general color conversion |
| [PLUME7718](PLUME7718.md) | /Pattern color space has no single RGB value |
| [PLUME7719](PLUME7719.md) | malformed /Separation or /DeviceN color space array |
| [PLUME7720](PLUME7720.md) | /Separation or /DeviceN tint transform has the wrong input/output arity |
| [PLUME7721](PLUME7721.md) | shading color-ramp sample count exceeds MaxShadingSamples |
| [PLUME7722](PLUME7722.md) | axial shading's /Coords array is malformed |
| [PLUME7723](PLUME7723.md) | radial shading's /Coords array is malformed |
| [PLUME7725](PLUME7725.md) | tiling pattern would require too many tile instances |
| [PLUME7726](PLUME7726.md) | malformed tiling pattern dictionary |
| [PLUME7727](PLUME7727.md) | tiling pattern's device-space step collapsed to non-positive |
| [PLUME7728](PLUME7728.md) | malformed ExtGState /SMask soft-mask dictionary |
| [PLUME7729](PLUME7729.md) | non-embedded Symbol/ZapfDingbats font has no bundled substitute (diagnostic) — **deprecated** (Symbol/ZapfDingbats now render with bundled Foxit faces) |
| [PLUME7730](PLUME7730.md) | text rendering mode painted best-effort (diagnostic) |
| [PLUME7731](PLUME7731.md) | annotation /AP stream malformed / exceeds decompression cap (recoverable per-annotation diagnostic) |
| [PLUME7732](PLUME7732.md) | non-widget annotation lacks /AP; skipped (diagnostic) |
| [PLUME7733](PLUME7733.md) | optional-content layer suppressed page content (default-OFF OCG) (diagnostic) |
| [PLUME7734](PLUME7734.md) | /OCProperties config malformed; best-effort visible (diagnostic) |
| [PLUME7735](PLUME7735.md) | mesh shading vertex data exceeds cap |
| [PLUME7736](PLUME7736.md) | mesh shading stream truncated / malformed |
| [PLUME7737](PLUME7737.md) | /Matte entry invalid (diagnostic) |
| [PLUME7738](PLUME7738.md) | CalRGB/CalGray parameter invalid; fallback (diagnostic) |
| [PLUME7739](PLUME7739.md) | non-isolated transparency group backdrop capture exceeds cap |
| [PLUME7740](PLUME7740.md) | transfer function (non-soft-mask) ignored (diagnostic) |
| [PLUME7741](PLUME7741.md) | BG/UCR ignored (diagnostic) |
| [PLUME7742](PLUME7742.md) | halftone dictionary ignored (diagnostic) |
| [PLUME7743](PLUME7743.md) | page /Annots array exceeds the per-page annotation cap (fatal, page-wide) |
| [PLUME7744](PLUME7744.md) | image XObject could not be decoded; skipped (diagnostic) |
| [PLUME7745](PLUME7745.md) | image uses JPXDecode; register an IPdfFilter to paint it (diagnostic) — **deprecated** (JPX now decodes in-house, see PLUME3700–3718/7744) |
| [PLUME7746](PLUME7746.md) | adversarial/malformed image dictionary; skipped (diagnostic) |
| [PLUME7747](PLUME7747.md) | Do names a missing or non-stream /XObject resource (diagnostic) |
| [PLUME7748](PLUME7748.md) | XObject /Subtype missing or unsupported (diagnostic) |
| [PLUME7750](PLUME7750.md) | image suppressed by its XObject-level /OC membership (diagnostic) |
| [PLUME7752](PLUME7752.md) | pattern paint degraded (diagnostic) |
| [PLUME7753](PLUME7753.md) | JPEG 2000 (JPX): declared /ColorSpace component count mismatches the codestream; first N channels used (diagnostic) |
| [PLUME8001](PLUME8001.md) | font file exceeds the configured size limit |
| [PLUME8002](PLUME8002.md) | SFNT header or table directory is truncated |
| [PLUME8003](PLUME8003.md) | font is missing a required table |
| [PLUME8004](PLUME8004.md) | table offset/length runs past the end of the file, or a fixed-layout table is truncated |
| [PLUME8005](PLUME8005.md) | glyph count exceeds the configured limit |
| [PLUME8006](PLUME8006.md) | unsupported font format (CFF-flavored OpenType) |
| [PLUME8007](PLUME8007.md) | composite glyph cycles or nests too deep |
| [PLUME8008](PLUME8008.md) | no usable cmap subtable |
| [PLUME8009](PLUME8009.md) | codepoint has no glyph in the font |
| [PLUME8010](PLUME8010.md) | subset glyph count exceeds the configured limit |
| [PLUME8011](PLUME8011.md) | malformed cmap subtable |
| [PLUME8012](PLUME8012.md) | malformed glyf/loca data |
| [PLUME8013](PLUME8013.md) | subset alphabet exceeds format 4 cmap capacity |
| [PLUME8014](PLUME8014.md) | internal font-table invariant violated |
| [PLUME8015](PLUME8015.md) | CMap declares more entries than the configured limit |
| [PLUME8016](PLUME8016.md) | malformed entry inside a CMap block |
| [PLUME8017](PLUME8017.md) | unrecognized or malformed /Encoding on a simple font |
| [PLUME8018](PLUME8018.md) | malformed /Differences entry |
| [PLUME8019](PLUME8019.md) | MacExpertEncoding unsupported |
| [PLUME8020](PLUME8020.md) | character code has no Unicode or CID mapping (diagnostic) |
| [PLUME8021](PLUME8021.md) | unsupported predefined CMap on a Type0 font |
| [PLUME8022](PLUME8022.md) | Type0 font dictionary deviation (diagnostic) |
| [PLUME8023](PLUME8023.md) | PDF/A requires every font embedded; a Standard-14 font is in use |
| [PLUME8024](PLUME8024.md) | shaping lookup-application budget exceeded |
| [PLUME8025](PLUME8025.md) | font missing required complex-script shaping capability |
| [PLUME8026](PLUME8026.md) | script outside PlumePDF's shipped shaping tier |
| [PLUME8027](PLUME8027.md) | Multiple Substitution glyph-count ceiling exceeded |
| [PLUME8028](PLUME8028.md) | malformed Type 1 font program |
| [PLUME9001](PLUME9001.md) | content wider than available width |
| [PLUME9002](PLUME9002.md) | invalid table column/row definition |
| [PLUME9003](PLUME9003.md) | element does not fit on a single page |
| [PLUME9004](PLUME9004.md) | composed page/section/manuscript has no content |
| [PLUME9005](PLUME9005.md) | rendered page count exceeds the configured limit |
| [PLUME9006](PLUME9006.md) | element tree exceeds nesting depth or element count limit |
| [PLUME9007](PLUME9007.md) | non-positive or unbounded available space |
| [PLUME9008](PLUME9008.md) | unsupported element type |
| [PLUME9009](PLUME9009.md) | Compose descriptor called twice |
| [PLUME9010](PLUME9010.md) | element missing required tagging semantics for PDF/UA output |
| [PLUME9011](PLUME9011.md) | PDF/UA output requested without a document language |
| [PLUME9012](PLUME9012.md) | PDF/UA output requested without a document title |
| [PLUME9013](PLUME9013.md) | bidi isolate initiator/terminator not supported |
| [PLUME9014](PLUME9014.md) | CMYK JPEG cannot be embedded in a PDF/A document |
