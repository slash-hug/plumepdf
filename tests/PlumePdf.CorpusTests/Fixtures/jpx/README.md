# JPX fixture matrix
Committed fixtures for the in-house JPEG 2000 decoder. Generated deterministically by `scripts/generate-jpx-fixtures.sh` (delegates to `scripts/jpx-fixtures/generate.py` + `markers.py`) against a pinned OpenJPEG 2.5.4 CLI. Never run in CI -- regenerate by hand only when the matrix itself changes.
**45 fixtures** (36 opj_compress-generated, 9 hand-built byte-level constructions).
## Regenerating
```
./scripts/generate-jpx-fixtures.sh
./scripts/generate-jpx-fixtures.sh --check   # structural asserts only, no OpenJPEG needed
```
`--check` re-derives every `MANIFEST.json` assert field from the committed bytes via `markers.py`'s marker-segment walker and fails loudly on any mismatch. It never calls opj_compress/opj_decompress and never regenerates a file.
## OpenJPEG behaviours this matrix works around
- **Forced sYCC on subsampled 3-component streams.** opj_decompress attempts a sYCC->RGB conversion for any 3-component image where `comps[0].dx == comps[0].dy && comps[1].dx != 1`, regardless of the `colr` box. For uniform 2x2 subsampling this bails with `CAN NOT CONVERT` on stderr (exit 0) and writes unconverted planes; for mixed 4:2:0 it succeeds. Neither is a decode failure.
- **`-POC T0` is a no-op.** `Tn` in `-POC` syntax is 1-based; `T1` addresses tile index 0.
- **`-F` honours declared precision exactly; PGX does not.** The PGX format derives precision from the data's value range, which loses one bit for signed data (a saturating signed 16-bit source reads back as `Ssiz` 15). Every >8-bit or signed fixture in this matrix uses raw `-F` input, never a PGX source.
- **`-allow-partial` is required to decode any truncated stream at all** -- without it, `opj_decompress` exits 1 with 'Stream too short' and writes nothing.
- **`opj_compress` never emits a `res ` box.** The 300 dpi `res`/`resd` fixture's expected value is pinned entirely by construction; there is no oracle output (no PNG `pHYs`, no `opj_dump` resolution field) to check it against.
- **Default (huge) precincts read back as `PPx = PPy = 15`** when `COD`'s Scod bit 0 is clear -- there is no explicit per-resolution precinct-size list to read in that case; 15 is the T.800-defined default, not a measured value.
- **`opj_decompress` converts EnumCS 12 (CMYK) to RGB** before writing any non-TIFF output (`color_cmyk_to_rgb`: three planes out, the fourth freed, no opt-out for PGX). The `cmyk-enumcs12.jp2` references are therefore decoded from the bare codestream inside the JP2 (`"referenceSource": "codestream"` in `MANIFEST.json`, honoured by `JpxFixtureFreshnessTests` too): same bytes, same decode, four untouched CMYK planes.
## Deviations from the literal fixture recipes
- **`cblksty-m*` fixtures generated with `-PLT` added** (the natural recipe is `-M <bits>` alone). The MANIFEST assertion 'the largest code-block signals >= 11 passes in the packet header' needs either a full Tier-2 (packet-header) decoder in the generator or a way to skip straight to the target packet. Every fixture in this set is single-tile/single-component/default-precinct with exactly one code-block per subband (verified programmatically, not assumed -- `markers.assert_single_codeblock_finest_subband`), so with `-PLT`'s packet-length list the generator can jump straight to the finest resolution's packet and read only its first code-block's number-of-new-passes field. Without `-PLT` this would require replicating the 'selective bypass' style's non-trivial per-bit-plane segmentation grouping (T.800 B.10.7) purely to skip past earlier packets it never needed to interpret. Measured >= 11 passes (most fixtures measure 22) on every cblksty value.
## Inventory
| File | Class | Tolerance | References |
|---|---|---|---|
| `baseline-53.j2k` | generated | bit-exact | baseline-53.ref_0.pgx |
| `baseline-53-jp2.jp2` | generated | bit-exact | baseline-53-jp2.ref_0.pgx |
| `wavelet-97.jp2` | generated | tolerance-a | wavelet-97.ref_0.pgx |
| `wavelet-97-ict.jp2` | generated | tolerance-a | wavelet-97-ict.ref_0.pgx, wavelet-97-ict.ref_1.pgx, wavelet-97-ict.ref_2.pgx |
| `scan-97-ict-640x480.jp2` | generated | tolerance-a | scan-97-ict-640x480.ref_0.pgx, scan-97-ict-640x480.ref_1.pgx, scan-97-ict-640x480.ref_2.pgx |
| `tiles-3x2-partial.j2k` | generated | bit-exact | tiles-3x2-partial.ref_0.pgx, tiles-3x2-partial.ref_1.pgx, tiles-3x2-partial.ref_2.pgx |
| `precincts-explicit.jp2` | generated | bit-exact | precincts-explicit.ref_0.pgx |
| `prog-lrcp.jp2` | generated | bit-exact | prog-lrcp.ref_0.pgx |
| `prog-rlcp.jp2` | generated | bit-exact | prog-rlcp.ref_0.pgx |
| `prog-pcrl.jp2` | generated | bit-exact | prog-pcrl.ref_0.pgx |
| `prog-cprl.jp2` | generated | bit-exact | prog-cprl.ref_0.pgx |
| `poc-640x480.j2k` | generated | tolerance-a | poc-640x480.ref_0.pgx, poc-640x480.ref_1.pgx, poc-640x480.ref_2.pgx |
| `cblksty-m1.j2k` | generated | bit-exact | cblksty-m1.ref_0.pgx |
| `cblksty-m2.j2k` | generated | bit-exact | cblksty-m2.ref_0.pgx |
| `cblksty-m4.j2k` | generated | bit-exact | cblksty-m4.ref_0.pgx |
| `cblksty-m8.j2k` | generated | bit-exact | cblksty-m8.ref_0.pgx |
| `cblksty-m16.j2k` | generated | bit-exact | cblksty-m16.ref_0.pgx |
| `cblksty-m32.j2k` | generated | bit-exact | cblksty-m32.ref_0.pgx |
| `cblksty-m63.j2k` | generated | bit-exact | cblksty-m63.ref_0.pgx |
| `layers-3.jp2` | generated | bit-exact | layers-3.ref_0.pgx |
| `layers-10.jp2` | generated | bit-exact | layers-10.ref_0.pgx |
| `subsample-uniform-2x2.jp2` | generated | tolerance-a | subsample-uniform-2x2.ref_0.pgx, subsample-uniform-2x2.ref_1.pgx, subsample-uniform-2x2.ref_2.pgx |
| `subsample-mixed-420.jp2` | generated | tolerance-a | subsample-mixed-420.ref_0.pgx, subsample-mixed-420.ref_1.pgx, subsample-mixed-420.ref_2.pgx |
| `depth-12u.j2k` | generated | bit-exact | depth-12u.ref_0.pgx |
| `depth-16u.j2k` | generated | bit-exact | depth-16u.ref_0.pgx |
| `depth-16s.j2k` | generated | bit-exact | depth-16s.ref_0.pgx |
| `depth-16s-97.j2k` | generated | tolerance-a | depth-16s-97.ref_0.pgx |
| `alpha-cdef.jp2` | generated | bit-exact | alpha-cdef.ref_0.pgx, alpha-cdef.ref_1.pgx, alpha-cdef.ref_2.pgx, alpha-cdef.ref_3.pgx |
| `sop-eph.jp2` | generated | bit-exact | sop-eph.ref_0.pgx |
| `plt-marker.jp2` | generated | bit-exact | plt-marker.ref_0.pgx |
| `tlm-marker.jp2` | generated | bit-exact | tlm-marker.ref_0.pgx |
| `tileparts-tpr.jp2` | generated | bit-exact | tileparts-tpr.ref_0.pgx |
| `prog-rpcl-precincts-layers.jp2` | generated | bit-exact | prog-rpcl-precincts-layers.ref_0.pgx |
| `prog-cprl-precincts-layers.jp2` | generated | bit-exact | prog-cprl-precincts-layers.ref_0.pgx |
| `rgn-refusal.j2k` | generated | refusal:PLUME3703 | (none) |
| `truncated.j2k` | generated | not-compared:PLUME3701 | (none) |
| `pclr-cmap.jp2` | hand-built | bit-exact | pclr-cmap.ref_0.pgx, pclr-cmap.ref_1.pgx, pclr-cmap.ref_2.pgx |
| `sycc-enumcs18.jp2` | hand-built | tolerance-a | sycc-enumcs18.ref_0.pgx, sycc-enumcs18.ref_1.pgx, sycc-enumcs18.ref_2.pgx |
| `premultiplied-alpha.jp2` | hand-built | bit-exact | premultiplied-alpha.ref_0.pgx, premultiplied-alpha.ref_1.pgx, premultiplied-alpha.ref_2.pgx, premultiplied-alpha.ref_3.pgx |
| `res-metadata.jp2` | hand-built | metadata-only | (none) |
| `two-colr.jp2` | hand-built | metadata-only | (none) |
| `ppm-refusal.j2k` | hand-built | refusal:PLUME3704 | (none) |
| `part2-refusal.j2k` | hand-built | refusal:PLUME3702 | (none) |
| `precision17-refusal.j2k` | hand-built | refusal:PLUME3705 | (none) |
| `cmyk-enumcs12.jp2` | hand-built | bit-exact | cmyk-enumcs12.ref_0.pgx, cmyk-enumcs12.ref_1.pgx, cmyk-enumcs12.ref_2.pgx, cmyk-enumcs12.ref_3.pgx |

## Cross-platform note on the irreversible references

The committed `*.ref_N.pgx` files were produced by `opj_decompress` 2.5.4 on macOS arm64. Reversible (5/3) output is integer-exact and byte-identical on every platform. **Irreversible output (9/7 wavelet, ICT, sYCC) is float arithmetic inside OpenJPEG and differs by a few LSBs between platforms/compilers** — the first armed CI run on linux x86_64 (main `b8e4322`) reproduced exactly the five irreversible fixtures at |Δ| ≤ 2. `JpxFixtureFreshnessTests` therefore byte-compares only `bit-exact` fixtures and compares the rest within Tolerance A; `JpxOracleTests` was always tolerance-based for those classes. Regenerating the matrix on a different platform may legitimately change the irreversible references' bytes.

